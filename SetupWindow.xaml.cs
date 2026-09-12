using System.Windows;
using Microsoft.Win32;

namespace KobraTimeLapse;

public partial class SetupWindow : Window
{
    // Maps the friendlier 0-100 "Sensitivity" shown in the UI to the underlying SSIM
    // threshold CaptureService actually compares against. 0 = only flag huge changes,
    // 100 = flag almost anything -- see Settings.FailureDetectionThreshold for why the
    // actual comparison direction is "lower SSIM = more different".
    private const double MinThreshold = 0.30;
    private const double MaxThreshold = 0.95;

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
        AutoPauseCheck.IsChecked = settings.AutoPauseOnAnomaly;
        var sensitivity = (int)Math.Round((settings.FailureDetectionThreshold - MinThreshold) / (MaxThreshold - MinThreshold) * 100);
        SensitivityBox.Text = Math.Clamp(sensitivity, 0, 100).ToString();
        UpdateSensitivityRowVisibility();
    }

    private void EnableFailureDetectionCheck_Changed(object sender, RoutedEventArgs e) => UpdateSensitivityRowVisibility();

    private void UpdateSensitivityRowVisibility()
    {
        SensitivityRow.IsEnabled = EnableFailureDetectionCheck.IsChecked == true;
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

        if (enableFailureDetection && (!int.TryParse(SensitivityBox.Text.Trim(), out var sensitivity) || sensitivity < 0 || sensitivity > 100))
        {
            ErrorText.Text = "Sensitivity must be a number from 0 to 100.";
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
            var savedSensitivity = int.Parse(SensitivityBox.Text.Trim());
            Settings.FailureDetectionThreshold = MinThreshold + savedSensitivity / 100.0 * (MaxThreshold - MinThreshold);
            Settings.AutoPauseOnAnomaly = AutoPauseCheck.IsChecked == true;
        }
        else
        {
            Settings.AutoPauseOnAnomaly = false;
        }
        Settings.SetupComplete = true;
        Settings.Save();

        DialogResult = true;
        Close();
    }
}
