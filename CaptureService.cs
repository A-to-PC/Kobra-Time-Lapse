using System.Diagnostics;
using System.IO;
using System.Linq;
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
    private string? _fileName;
    private KobraMqttClient? _printer;
    private bool _pauseSentForCurrentAnomaly;

    // Second, independent line of defense against a print that stops without ever being
    // recognised as over -- see MapState's own comment for the real root cause (a raw state
    // string matching the generic "print" check before a terminal one). This catches whatever
    // that fix doesn't: any other raw state string this printer's firmware might one day send
    // that isn't accounted for. If curr_layer hasn't moved this long while nominally active,
    // treat it as stopped rather than trust the state string forever -- 10 minutes is well
    // beyond what a single real layer takes even on a slow/complex print, so this should never
    // fire on a genuinely still-printing job.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(10);
    private DateTime? _layerLastChangedAt;
    private DateTime? _finalLayerStartedAt;
    private TimeSpan _lastLayerDuration;

    // Auto-calibration (14/09/2026, see Settings.EnableAutoCalibration's own comment for the
    // full reasoning): collects this print's own normal frame-similarity scores during an early
    // window, still compared against the fixed FailureDetectionThreshold as a safety net the
    // whole time, then derives a per-print threshold once enough samples exist.
    private readonly List<double> _calibrationScores = [];
    private double? _calibratedThreshold;
    private const int CalibrationSampleCount = 15;
    private const double CalibrationMargin = 0.08;
    private const double CalibrationFloor = 0.20;

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

    /// <summary>
    /// Cancels the capture loop and waits for it to actually finish -- including the final
    /// assemble/delete cleanup, which deliberately runs on its own uncancellable token so a plain
    /// Stop() can't interrupt it. Exists for MainWindow's Closing handler (14/09/2026): closing
    /// the window directly used to kill the whole process instantly with nothing waiting for that
    /// cleanup, which is the real reason a completed session once left its source frames
    /// undeleted despite auto-delete being enabled -- see the README's own note on this.
    /// </summary>
    public async Task StopAndWaitAsync()
    {
        _cts?.Cancel();
        if (_loopTask != null)
        {
            try { await _loopTask; }
            catch { /* the loop already logs its own errors; nothing further to do here */ }
        }
    }

    // Status is polled far more often than a frame is actually captured -- purely so a layer
    // change can be caught close to when it really happens. Captures still only happen on the
    // user's configured interval, just anchored to layer-change moments instead of running on
    // their own free clock (14/09/2026, Jason: "still timed but reset every layer so same point
    // every 10s or whatever is set"). The practical effect: a slow, wide layer (lots of X/Y
    // travel before Z advances) naturally accumulates more frames before the layer changes,
    // while a fast, thin layer only gets one or two -- without ever touching G-code, since it's
    // purely reactive to the real per-layer duration this app already observes.
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(2);

    private async Task RunLoopAsync(CancellationToken ct)
    {
        await using var printer = new KobraMqttClient(settings.PrinterHost);
        _printer = printer;
        Log?.Invoke($"Watching {settings.PrinterHost} every {StatusPollInterval.TotalSeconds}s, capturing every {settings.IntervalSeconds}s per layer...");

        DateTime? nextCaptureDue = null;

        while (!ct.IsCancellationRequested)
        {
            var previousLayer = _currLayer;
            PrintState state;
            try
            {
                (state, _currLayer, _totalLayers, _fileName) = await printer.GetStatusAsync(ct);
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
            // curr_layer reaches total_layers the moment the FINAL layer STARTS, not when it
            // finishes -- confirmed live 27/09/2026: a 20-layer print was ended at layer 20 with
            // 11 minutes of printing still to go, so the last layer was never captured. So the
            // layer count only counts as "complete" after the final layer has had a fair chance
            // to finish: 1.5x the previous layer's duration, at least 2 minutes.
            var reachedFinalLayer = _currLayer is { } cl && _totalLayers is { } tl && tl > 0 && cl >= tl;
            if (!reachedFinalLayer) _finalLayerStartedAt = null;
            else _finalLayerStartedAt ??= DateTime.UtcNow;
            var finalLayerGrace = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(2).Ticks, (long)(_lastLayerDuration.Ticks * 1.5)));
            var layerIndicatesComplete = reachedFinalLayer && _finalLayerStartedAt is { } finalStart
                && DateTime.UtcNow - finalStart > finalLayerGrace;

            var wasActive = _lastState is PrintState.Printing or PrintState.Paused;
            var isActive = state is PrintState.Printing or PrintState.Paused && !layerIndicatesComplete;

            if (!wasActive && isActive)
            {
                BeginSession();
                // Deliberately NOT capturing yet here -- the printer reports the whole job
                // (including bed leveling/homing/priming) as "Printing" with curr_layer sitting
                // at 0 during that lead-in, confirmed live 15/09/2026 (Jason: "print not started"
                // while frames were already being captured). Real capture only starts once
                // curr_layer actually reaches 1 below.
                nextCaptureDue = null;
            }

            // Real filament going down starts at layer 1, not layer 0 (leveling/homing/priming
            // all report as layer 0) -- gate every capture on this so nothing gets recorded
            // before the print itself has actually begun.
            var realPrintingStarted = _currLayer is { } cl2 && cl2 >= 1;

            var layerChanged = isActive && realPrintingStarted && previousLayer != _currLayer;

            if (layerChanged && _layerLastChangedAt is { } prevChange) _lastLayerDuration = DateTime.UtcNow - prevChange;
            if (!isActive) _layerLastChangedAt = null;
            else if (layerChanged || _layerLastChangedAt == null) _layerLastChangedAt = DateTime.UtcNow;

            // Adaptive: a slow flat print can have layers longer than a fixed 10 minutes (the
            // 27/09/2026 honeycomb panels ran ~15 minutes a layer), so the stall limit scales
            // with the real layer time seen so far and never drops below the fixed floor.
            var stallLimit = TimeSpan.FromTicks(Math.Max(StallTimeout.Ticks, _lastLayerDuration.Ticks * 3));
            if (isActive && _layerLastChangedAt is { } lastChange && DateTime.UtcNow - lastChange > stallLimit)
            {
                Log?.Invoke($"No layer progress for over {stallLimit.TotalMinutes:0} minutes (stuck at layer {_currLayer}) -- treating as stopped rather than trusting the state string forever.");
                isActive = false;
            }
            if (layerChanged) nextCaptureDue = DateTime.UtcNow; // reset the interval to this instant

            if (state == PrintState.Printing && realPrintingStarted && nextCaptureDue is { } due && DateTime.UtcNow >= due)
            {
                await CaptureFrameAsync(ct);
                nextCaptureDue = DateTime.UtcNow.AddSeconds(settings.IntervalSeconds);
            }

            if (wasActive && !isActive)
            {
                var endState = layerIndicatesComplete && state == PrintState.Printing ? PrintState.Complete : state;
                await EndSessionAsync(endState, ct);
                nextCaptureDue = null;
            }

            _lastState = state;

            try
            {
                await Task.Delay(StatusPollInterval, ct);
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
        _finalLayerStartedAt = null;
        _lastLayerDuration = TimeSpan.Zero;
        _frameIndex = 0;
        _previousFramePath = null;
        _pauseSentForCurrentAnomaly = false;
        _calibrationScores.Clear();
        _calibratedThreshold = null;

        if (settings.EnableTimelapse)
        {
            // Leads with the real print job name where the printer reports one (Jason: the old
            // date-only folder name "is not helpful to search as a human" -- 15/09/2026), with
            // the timestamp kept as a suffix for uniqueness (same file printed twice in one day)
            // and as a safe fallback on its own if the printer hasn't reported a filename yet.
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var name = string.IsNullOrWhiteSpace(_fileName)
                ? $"print_{timestamp}"
                : $"{SanitizeForFolderName(_fileName)}_{timestamp}";
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

    // Strips the printer's own file extension (.gcode.3mf) and anything the filesystem can't
    // take, so the real job name reads cleanly as a folder rather than carrying a raw filename's
    // punctuation straight through.
    private static string SanitizeForFolderName(string fileName)
    {
        var name = fileName;
        foreach (var ext in new[] { ".gcode.3mf", ".gcode", ".3mf" })
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^ext.Length];
                break;
            }
        }
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        name = name.Trim().Trim('.');
        return name.Length == 0 ? "print" : name;
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

    // Rotation actually applied to each frame: Kobra LAN Monitor's saved rotation for the printer
    // whose network camera is this same camera, when the match option is on and that setting can
    // be read; otherwise this app's own RotationDegrees. Re-read on every capture (a tiny JSON
    // file) so changing the rotation in LAN Monitor takes effect without restarting anything.
    private int EffectiveRotationDegrees()
    {
        if (settings.MatchLanMonitorRotation)
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "KobraLanMonitor", "settings.json");
                if (File.Exists(path))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                    if (doc.RootElement.TryGetProperty("Printers", out var printers) &&
                        printers.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var p in printers.EnumerateArray())
                        {
                            if (p.TryGetProperty("NetworkCameraHost", out var host) &&
                                string.Equals(host.GetString(), settings.CameraHost, StringComparison.OrdinalIgnoreCase) &&
                                p.TryGetProperty("CameraRotationDeg", out var deg) &&
                                deg.TryGetInt32(out var value))
                            {
                                return value;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Unreadable/partially-written file -- fall back to this app's own setting.
            }
        }
        return settings.RotationDegrees;
    }

    // Builds the ffmpeg -vf argument (e.g. " -vf transpose=1"). Returns "" (no -vf at all)
    // when no rotation is configured.
    private string BuildVideoFilter()
    {
        var filters = new List<string>();

        // transpose=1 is 90 clockwise, transpose=2 is 90 counter-clockwise; 180 is two
        // 90-clockwise passes chained, since ffmpeg has no single "180" transpose value.
        switch (EffectiveRotationDegrees())
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

        // Auto-calibration: the fixed setting is always the safety net -- every comparison is
        // checked against it, calibration or not, so an early-print failure (a common real
        // failure time) is never missed while still collecting samples. Once enough of this
        // print's own scores are in, the effective threshold can drop below that (never above
        // it) to match this print's own normal motion, cutting false positives without ever
        // going blind or suppressing what the user explicitly asked to catch.
        var effectiveThreshold = settings.FailureDetectionThreshold;
        if (settings.EnableAutoCalibration)
        {
            if (_calibratedThreshold is { } calibrated)
            {
                effectiveThreshold = Math.Min(effectiveThreshold, calibrated);
            }
            else
            {
                _calibrationScores.Add(ssim);
                if (_calibrationScores.Count >= CalibrationSampleCount)
                {
                    var learned = Math.Clamp(_calibrationScores.Min() - CalibrationMargin, CalibrationFloor, settings.FailureDetectionThreshold);
                    _calibratedThreshold = learned;
                    Log?.Invoke($"Auto-calibration complete -- this print's own threshold is now {learned:P0} (was {settings.FailureDetectionThreshold:P0}).");
                }
            }
        }

        if (ssim >= effectiveThreshold) return;

        Log?.Invoke($"** Possible print anomaly ** -- frame similarity {ssim:P0} (threshold {effectiveThreshold:P0}). Could be a real issue, or the enclosure curtain/lighting -- check the camera.");

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
