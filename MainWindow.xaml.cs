using System.Windows;
using System.Windows.Controls;

namespace KobraTimeLapse;

public partial class MainWindow : Window
{
    private readonly Settings _settings;
    private CaptureService? _capture;

    public MainWindow(Settings settings)
    {
        InitializeComponent();
        ThemeManager.Track(this);
        _settings = settings;
        LoadSettingsIntoUi();
    }

    private void LoadSettingsIntoUi()
    {
        ManualModeCheck.IsChecked = _settings.ManualMode;
        IntervalBox.Text = _settings.IntervalSeconds.ToString();
        FramerateBox.Text = _settings.AssembleFramerate.ToString();
        FfmpegPathBox.Text = _settings.FfmpegPath;
        ThemeCombo.SelectedIndex = _settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        UpdateModeUi();
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _settings.Theme = ThemeCombo.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "Auto" };
        _settings.Save();
        ThemeManager.SetOverride(_settings.Theme switch { "Dark" => true, "Light" => false, _ => null });
    }

    private void UpdateModeUi()
    {
        var manual = ManualModeCheck.IsChecked == true;
        StartStopButton.Content = _capture is { IsRunning: true }
            ? "Stop"
            : manual ? "Start Recording" : "Start Watching";
    }

    private void ManualModeCheck_Changed(object sender, RoutedEventArgs e) => UpdateModeUi();

    private void SaveUiIntoSettings()
    {
        _settings.ManualMode = ManualModeCheck.IsChecked == true;
        _settings.IntervalSeconds = int.TryParse(IntervalBox.Text, out var interval) ? Math.Max(1, interval) : 10;
        _settings.AssembleFramerate = int.TryParse(FramerateBox.Text, out var fps) ? Math.Max(1, fps) : 24;
        _settings.FfmpegPath = FfmpegPathBox.Text.Trim();
        _settings.Save();
    }

    private void SetupButton_Click(object sender, RoutedEventArgs e)
    {
        var setup = new SetupWindow(_settings) { Owner = this };
        if (setup.ShowDialog() == true)
        {
            LoadSettingsIntoUi();
        }
    }

    private void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capture is { IsRunning: true })
        {
            _capture.Stop();
            StatusText.Text = "Stopping...";
            UpdateModeUi();
            return;
        }

        SaveUiIntoSettings();
        _capture = new CaptureService(_settings);
        _capture.Log += OnLog;
        _capture.Start();
        StatusText.Text = _settings.ManualMode ? "Recording..." : "Watching for a print...";
        UpdateModeUi();
    }

    private void OnLog(string message)
    {
        Dispatcher.Invoke(() =>
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            LogList.Items.Add(line);
            LogList.ScrollIntoView(line);
            StatusText.Text = message;
        });
    }
}
