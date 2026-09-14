using System.Windows;
using Microsoft.Win32;

namespace KobraTimeLapse;

public partial class SetupWindow : Window
{
    // Shown and set directly as the raw SSIM threshold percentage CaptureService actually
    // compares against -- deliberately NOT a separate 0-100 "sensitivity" score anymore. That
    // abstraction was a real, confirmed UX problem (14/09/2026, Jason: "that is odd for a user
    // to grasp, why not a sensitivity slide for %, 35 is 52% is strange"): the log reports the
    // real percentage ("threshold 60%"), so the UI should show and accept that exact number, not
    // a converted score the user has to mentally translate. Still clamped to a sane range --
    // below 30% would only ever catch a near-total scene change, above 95% would flag almost
    // any camera noise.
    private const double MinThresholdPercent = 30;
    private const double MaxThresholdPercent = 95;

    public Settings Settings { get; }

    public SetupWindow(Settings settings)
    {
        InitializeComponent();
        ThemeManager.Track(this);
        Settings = settings;

        PrinterHostBox.Text = settings.PrinterHost;
        CameraHostBox.Text = settings.CameraHost;
        CameraUsernameBox.Text = settings.CameraUsername;
        CameraPasswordBox.Password = settings.CameraPassword;
        OutputFolderBox.Text = settings.OutputFolder;
        DeleteFramesCheck.IsChecked = settings.DeleteFramesAfterAssembly;
        RotationCombo.SelectedIndex = settings.RotationDegrees switch
        {
            90 => 1,
            180 => 2,
            270 => 3,
            _ => 0,
        };

        EnableTimelapseCheck.IsChecked = settings.EnableTimelapse;
        EnableFailureDetectionCheck.IsChecked = settings.EnableFailureDetection;
        AutoCalibrationCheck.IsChecked = settings.EnableAutoCalibration;
        AutoPauseCheck.IsChecked = settings.AutoPauseOnAnomaly;
        var thresholdPercent = (int)Math.Round(settings.FailureDetectionThreshold * 100);
        SensitivityBox.Text = Math.Clamp(thresholdPercent, (int)MinThresholdPercent, (int)MaxThresholdPercent).ToString();
        UpdateSensitivityRowVisibility();
    }

    private void EnableFailureDetectionCheck_Changed(object sender, RoutedEventArgs e) => UpdateSensitivityRowVisibility();

    private void UpdateSensitivityRowVisibility()
    {
        var enabled = EnableFailureDetectionCheck.IsChecked == true;
        SensitivityRow.IsEnabled = enabled;
        AutoCalibrationCheck.IsEnabled = enabled;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = OutputFolderBox.Text };
        if (dialog.ShowDialog() == true)
        {
            OutputFolderBox.Text = dialog.FolderName;
        }
    }

    private void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        var printerHost = PrinterHostBox.Text.Trim();
        var cameraHost = CameraHostBox.Text.Trim();
        var cameraUsername = CameraUsernameBox.Text.Trim();
        var cameraPassword = CameraPasswordBox.Password;
        var outputFolder = OutputFolderBox.Text.Trim();
        var enableTimelapse = EnableTimelapseCheck.IsChecked == true;
        var enableFailureDetection = EnableFailureDetectionCheck.IsChecked == true;

        if (printerHost.Length == 0)
        {
            ErrorText.Text = "Printer IP is required.";
            return;
        }
        if (cameraHost.Length == 0)
        {
            ErrorText.Text = "Camera IP is required.";
            return;
        }
        if (cameraUsername.Length == 0)
        {
            ErrorText.Text = "Camera username is required.";
            return;
        }
        if (!enableTimelapse && !enableFailureDetection)
        {
            ErrorText.Text = "Enable at least one of Timelapse or Failure detection.";
            return;
        }
        // Save location only matters for Timelapse's output -- failure-detection-only mode
        // uses a throwaway temp folder and never touches this path.
        if (enableTimelapse && outputFolder.Length == 0)
        {
            ErrorText.Text = "Save location is required when Timelapse is enabled.";
            return;
        }

        if (enableFailureDetection && (!int.TryParse(SensitivityBox.Text.Trim(), out var thresholdPercent)
            || thresholdPercent < MinThresholdPercent || thresholdPercent > MaxThresholdPercent))
        {
            ErrorText.Text = $"Similarity threshold must be a number from {MinThresholdPercent:0} to {MaxThresholdPercent:0}.";
            return;
        }

        Settings.PrinterHost = printerHost;
        Settings.CameraHost = cameraHost;
        Settings.CameraUsername = cameraUsername;
        Settings.CameraPassword = cameraPassword;
        Settings.OutputFolder = outputFolder;
        Settings.DeleteFramesAfterAssembly = DeleteFramesCheck.IsChecked == true;
        Settings.RotationDegrees = RotationCombo.SelectedIndex switch
        {
            1 => 90,
            2 => 180,
            3 => 270,
            _ => 0,
        };
        Settings.EnableTimelapse = enableTimelapse;
        Settings.EnableFailureDetection = enableFailureDetection;
        if (enableFailureDetection)
        {
            var savedThresholdPercent = int.Parse(SensitivityBox.Text.Trim());
            Settings.FailureDetectionThreshold = savedThresholdPercent / 100.0;
            Settings.EnableAutoCalibration = AutoCalibrationCheck.IsChecked == true;
            Settings.AutoPauseOnAnomaly = AutoPauseCheck.IsChecked == true;
        }
        else
        {
            Settings.EnableAutoCalibration = false;
            Settings.AutoPauseOnAnomaly = false;
        }
        Settings.SetupComplete = true;
        Settings.Save();

        DialogResult = true;
        Close();
    }
}
