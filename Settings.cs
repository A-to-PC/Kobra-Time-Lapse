using System.IO;
using System.Text.Json;

namespace KobraTimeLapse;

public class Settings
{
    public bool ManualMode { get; set; } = false;
    // Camera fields kept separate (not one raw RTSP URL) so Setup can ask plain questions
    // instead of expecting the user to hand-build a URL. RtspPath defaults to the Hikvision/
    // EZVIZ-family convention (confirmed working against a real EZVIZ camera) but stays a
    // separate, overridable field since other camera brands use a different path.
    public string CameraHost { get; set; } = "";
    public string CameraUsername { get; set; } = "admin";
    public string CameraPassword { get; set; } = "";
    public string CameraRtspPath { get; set; } = "/Streaming/Channels/101/";
    public string BuildRtspUrl() => $"rtsp://{CameraUsername}:{CameraPassword}@{CameraHost}:554{CameraRtspPath}";

    // Stock (non-Rinkhals) firmware has no Moonraker to poll -- only the printer's own LAN IP
    // is needed, same as Kobra LAN Monitor's setup. Everything else (MQTT broker, credentials)
    // is self-discovered via LanCredentialDiscovery.
    public string PrinterHost { get; set; } = "";
    public int IntervalSeconds { get; set; } = 10;
    // Deletes the per-print "frames" subfolder once timelapse.mp4 has been assembled
    // successfully, so completed prints don't leave hundreds of loose JPEGs behind -- only
    // skipped if assembly itself failed, so nothing is lost on an ffmpeg error.
    public bool DeleteFramesAfterAssembly { get; set; } = true;
    public string OutputFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Kobra Time Lapse");
    public int AssembleFramerate { get; set; } = 24;
    public string FfmpegPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
    // Degrees clockwise to rotate each captured frame -- for a camera physically mounted
    // sideways (e.g. portrait, to better frame a printer that's taller than it is wide).
    // Valid values: 0, 90, 180, 270.
    public int RotationDegrees { get; set; } = 0;

    // Independent toggles -- Timelapse and Failure Detection share the same capture loop and
    // camera connection, but either can run alone or both together.
    public bool EnableTimelapse { get; set; } = true;
    public bool EnableFailureDetection { get; set; } = false;
    // SSIM (structural similarity) between consecutive frames, 0.0-1.0, identical = 1.0.
    // Below this triggers a logged anomaly. Log-only by default (see AutoPauseOnAnomaly) --
    // deliberately conservative (0.60) since an enclosure curtain and dimmable COB lighting
    // both cause legitimate large frame changes that would false-positive at a stricter value;
    // expect this to need real tuning against actual footage before it's reliable.
    public double FailureDetectionThreshold { get; set; } = 0.60;
    // Off by default -- see the threshold comment above. When on, an anomaly sends the same
    // pause command Kobra LAN Monitor already uses (live-verified against a real print), once
    // per print session, not repeatedly. Deliberately opt-in: a false positive would pause a
    // perfectly good print, and the false-positive rate on any given setup isn't known until
    // it's been run log-only against a few real prints first.
    public bool AutoPauseOnAnomaly { get; set; } = false;
    // False until the first-run Setup window has been completed once. Gates whether Setup
    // shows automatically on launch; the user can still reopen it later from MainWindow.
    public bool SetupComplete { get; set; } = false;

    // "Auto" (default) follows Windows' own light/dark setting live; "Light"/"Dark" is an
    // explicit user override that ignores OS theme changes. Public repo -- can't assume every
    // user wants the OS default, so this needs to be a real, persisted choice, not just
    // automatic detection.
    public string Theme { get; set; } = "Auto";

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Kobra Time Lapse", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<Settings>(json);
                if (loaded != null) return loaded;
            }
        }
        catch
        {
            // Fall through to defaults if the file is missing or corrupt.
        }
        return new Settings();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
