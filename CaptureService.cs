using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace KobraTimeLapse;

public partial class CaptureService(Settings settings)
{
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    private bool _sessionActive;
    private string? _sessionFolder;       // set only when EnableTimelapse
    private string? _tempCaptureFolder;   // set only when failure-detection-only (no timelapse)
    private string? _previousFramePath;
    private int _frameIndex;
    private PrintState _lastState = PrintState.Unknown;
    private int? _currLayer;
    private int? _totalLayers;
    private KobraMqttClient? _printer;
    private bool _pauseSentForCurrentAnomaly;

    public event Action<string>? Log;

    public bool IsRunning => _loopTask is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _loopTask = settings.ManualMode ? RunManualLoopAsync(_cts.Token) : RunLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        await using var printer = new KobraMqttClient(settings.PrinterHost);
        _printer = printer;
        Log?.Invoke($"Watching {settings.PrinterHost} every {settings.IntervalSeconds}s...");

        while (!ct.IsCancellationRequested)
        {
            PrintState state;
            try
            {
                (state, _currLayer, _totalLayers) = await printer.GetStatusAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Printer connection failed: {ex.Message}");
                state = _lastState;
            }

            // Layer count is a more reliable completion signal than the raw state string --
            // the exact wording the printer uses for "finished" was never confirmed against
            // a real completed print, but curr_layer reaching total_layers has been solid all
            // session. Treat that as "no longer active" even if the state string itself still
            // reads as printing/unrecognized, so a genuinely finished print never gets stuck
            // waiting on a state-string match that may not come.
            var layerIndicatesComplete = _currLayer is { } cl && _totalLayers is { } tl && tl > 0 && cl >= tl;

            var wasActive = _lastState is PrintState.Printing or PrintState.Paused;
            var isActive = state is PrintState.Printing or PrintState.Paused && !layerIndicatesComplete;

            if (!wasActive && isActive)
            {
                BeginSession();
            }

            if (state == PrintState.Printing)
            {
                await CaptureFrameAsync(ct);
            }

            if (wasActive && !isActive)
            {
                var endState = layerIndicatesComplete && state == PrintState.Printing ? PrintState.Complete : state;
                await EndSessionAsync(endState, ct);
            }

            _lastState = state;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.IntervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // If cancelled mid-print, still assemble/clean up whatever was captured.
        if (_sessionActive)
        {
            await EndSessionAsync(PrintState.Cancelled, CancellationToken.None);
        }

        Log?.Invoke("Stopped.");
    }

    private async Task RunManualLoopAsync(CancellationToken ct)
    {
        Log?.Invoke($"Manual mode -- capturing every {settings.IntervalSeconds}s until stopped.");
        BeginSession();

        while (!ct.IsCancellationRequested)
        {
            await CaptureFrameAsync(ct);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.IntervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await EndSessionAsync(PrintState.Complete, CancellationToken.None);
        Log?.Invoke("Stopped.");
    }

    private void BeginSession()
    {
        _sessionActive = true;
        _frameIndex = 0;
        _previousFramePath = null;
        _pauseSentForCurrentAnomaly = false;

        if (settings.EnableTimelapse)
        {
            var name = $"print_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
            _sessionFolder = Path.Combine(settings.OutputFolder, name, "frames");
            Directory.CreateDirectory(_sessionFolder);
            Log?.Invoke($"Print started -- capturing to {_sessionFolder}");
        }
        else
        {
            // Failure-detection-only: no permanent frames, just a rotating pair of temp
            // files reused every capture so nothing accumulates on disk.
            _tempCaptureFolder = Path.Combine(Path.GetTempPath(), "KobraTimeLapse-detect");
            Directory.CreateDirectory(_tempCaptureFolder);
            Log?.Invoke("Print started -- watching for anomalies (timelapse disabled).");
        }
    }

    private async Task CaptureFrameAsync(CancellationToken ct)
    {
        if (!_sessionActive) return;

        string framePath;
        if (settings.EnableTimelapse)
        {
            if (_sessionFolder == null) return;
            _frameIndex++;
            framePath = Path.Combine(_sessionFolder, $"frame_{_frameIndex:D5}.jpg");
        }
        else
        {
            if (_tempCaptureFolder == null) return;
            // Alternate between two filenames so the "previous" one survives the next
            // capture, without ever writing more than 2 files to disk.
            framePath = Path.Combine(_tempCaptureFolder, $"capture_{_frameIndex % 2}.jpg");
            _frameIndex++;
        }

        var videoFilter = BuildVideoFilter();
        var args = $"-y -rtsp_transport tcp -i \"{settings.BuildRtspUrl()}\" -frames:v 1{videoFilter} -q:v 2 \"{framePath}\"";
        var ok = await RunFfmpegAsync(args, ct);

        if (!ok)
        {
            Log?.Invoke("Frame capture failed (camera unreachable?)");
            return;
        }

        if (settings.EnableTimelapse)
        {
            var layerSuffix = _currLayer is { } layer ? $" (layer {layer}/{_totalLayers?.ToString() ?? "?"})" : "";
            Log?.Invoke($"Captured frame {_frameIndex}{layerSuffix}");
        }

        if (settings.EnableFailureDetection && _previousFramePath != null)
        {
            await CheckForAnomalyAsync(_previousFramePath, framePath, ct);
        }

        _previousFramePath = framePath;
    }

    private async Task EndSessionAsync(PrintState endState, CancellationToken ct)
    {
        if (!_sessionActive) return;
        _sessionActive = false;

        if (settings.EnableTimelapse && _sessionFolder != null)
        {
            Log?.Invoke($"Print ended ({endState}) -- assembling {_frameIndex} frames...");

            var framesGlob = Path.Combine(_sessionFolder, "frame_%05d.jpg");
            var outputFolder = Path.GetDirectoryName(_sessionFolder)!;
            var outputPath = Path.Combine(outputFolder, "timelapse.mp4");
            var args = $"-y -framerate {settings.AssembleFramerate} -i \"{framesGlob}\" -c:v libx264 -pix_fmt yuv420p \"{outputPath}\"";

            if (_frameIndex == 0)
            {
                Log?.Invoke("No frames captured -- skipping assembly.");
            }
            else
            {
                var ok = await RunFfmpegAsync(args, ct);
                Log?.Invoke(ok ? $"Timelapse saved: {outputPath}" : "Assembly failed -- check ffmpeg path/output.");

                // Only delete the source frames once assembly is confirmed successful -- if
                // ffmpeg failed, leave them in place so nothing is lost and assembly can be
                // retried.
                if (ok && settings.DeleteFramesAfterAssembly)
                {
                    await DeleteFramesWithRetryAsync(_sessionFolder, ct);
                }
            }
        }
        else
        {
            Log?.Invoke($"Print ended ({endState}).");
        }

        if (_tempCaptureFolder != null)
        {
            try { Directory.Delete(_tempCaptureFolder, recursive: true); }
            catch { /* best effort -- at most 2 small temp files */ }
            _tempCaptureFolder = null;
        }

        _sessionFolder = null;
        _frameIndex = 0;
        _previousFramePath = null;
    }

    // ffmpeg can hold a file handle open for a brief moment after WaitForExitAsync returns
    // (process teardown isn't always instantaneous, and antivirus scanners can transiently
    // lock a just-written file too) -- retry a few times with a short delay rather than
    // giving up on the first failure, which is what left stills behind after assembly.
    private async Task DeleteFramesWithRetryAsync(string sessionFolder, CancellationToken ct)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Directory.Delete(sessionFolder, recursive: true);
                Log?.Invoke("Deleted snapshot frames (timelapse.mp4 kept).");
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                Log?.Invoke($"Snapshot cleanup attempt {attempt} failed ({ex.Message}), retrying...");
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Could not delete snapshot frames after {maxAttempts} attempts: {ex.Message}");
            }
        }
    }

    // Builds the ffmpeg -vf argument (e.g. " -vf transpose=1"). Returns "" (no -vf at all)
    // when no rotation is configured.
    private string BuildVideoFilter()
    {
        var filters = new List<string>();

        // transpose=1 is 90 clockwise, transpose=2 is 90 counter-clockwise; 180 is two
        // 90-clockwise passes chained, since ffmpeg has no single "180" transpose value.
        switch (settings.RotationDegrees)
        {
            case 90: filters.Add("transpose=1"); break;
            case 180: filters.Add("transpose=1"); filters.Add("transpose=1"); break;
            case 270: filters.Add("transpose=2"); break;
        }

        return filters.Count == 0 ? "" : $" -vf {string.Join(",", filters)}";
    }

    // Compares two frames via ffmpeg's own SSIM filter (structural similarity, 1.0 = identical)
    // rather than pulling in an image/vision library -- keeps this app dependency-free, and
    // ffmpeg is already a hard requirement for everything else it does. Log-only: this
    // deliberately does not pause or stop the print (see Settings.AutoPauseOnAnomaly comment)
    // until real-world false-positive rate has been observed against actual prints.
    [GeneratedRegex(@"All:([\d.]+)")]
    private static partial Regex SsimAllRegex();

    private async Task CheckForAnomalyAsync(string previousFramePath, string currentFramePath, CancellationToken ct)
    {
        var args = $"-i \"{previousFramePath}\" -i \"{currentFramePath}\" -lavfi ssim -f null -";
        var stderr = await RunFfmpegCaptureStderrAsync(args, ct);
        if (stderr == null) return;

        var match = SsimAllRegex().Match(stderr);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, out var ssim))
        {
            Log?.Invoke("Failure detection: could not read a similarity score from ffmpeg output.");
            return;
        }

        if (ssim >= settings.FailureDetectionThreshold) return;

        Log?.Invoke($"** Possible print anomaly ** -- frame similarity {ssim:P0} (threshold {settings.FailureDetectionThreshold:P0}). Could be a real issue, or the enclosure curtain/lighting -- check the camera.");

        if (!settings.AutoPauseOnAnomaly || _pauseSentForCurrentAnomaly || _printer == null) return;

        // Only send the pause once per session, not on every poll the anomaly persists across
        // -- one pause command is enough; spamming it doesn't help and just adds MQTT traffic.
        _pauseSentForCurrentAnomaly = true;
        var paused = await _printer.SendPauseCommandAsync(ct);
        Log?.Invoke(paused
            ? "Auto-pause sent to printer due to detected anomaly."
            : "Tried to auto-pause but the printer command failed to send -- check manually.");
    }

    private async Task<bool> RunFfmpegAsync(string arguments, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = settings.FfmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process == null) return false;

            // Real bug found and fixed 14/09/2026: stderr was redirected but never read, so
            // once ffmpeg's continuous progress output filled the redirected pipe's buffer,
            // ffmpeg itself blocked trying to write to it -- a genuine, permanent deadlock, not
            // just slow. Confirmed live: assembling 158 frames into a timelapse hung forever
            // (ffmpeg.exe still running minutes later), while single-frame captures never hit
            // it because they never write enough stderr output to fill the buffer. Reading the
            // stream concurrently with WaitForExitAsync (not after it) drains it as it arrives,
            // and the captured text now also gives a real reason on failure instead of nothing.
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
                Log?.Invoke($"ffmpeg exited with code {process.ExitCode}: {stderr.Trim()}");

            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"ffmpeg error: {ex.Message}");
            return false;
        }
    }

    private async Task<string?> RunFfmpegCaptureStderrAsync(string arguments, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = settings.FfmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process == null) return null;
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return stderr;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"ffmpeg error: {ex.Message}");
            return null;
        }
    }
}
