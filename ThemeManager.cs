using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace KobraTimeLapse;

public static class ThemeManager
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

    private static readonly List<Window> TrackedWindows = new();

    // null = follow Windows' own light/dark setting live; true/false = explicit user
    // override that ignores OS theme changes until cleared. Persisted via Settings.Theme.
    public static bool? Override { get; private set; }

    public static void SetOverride(bool? isDark)
    {
        Override = isDark;
        ApplyToAllTracked();
    }

    public static bool IsSystemDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int lightThemeFlag && lightThemeFlag == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool ResolveIsDark() => Override ?? IsSystemDarkTheme();

    public static void ApplyResources()
    {
        var dark = ResolveIsDark();
        var res = Application.Current.Resources;

        if (dark)
        {
            res["WindowBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
            res["ControlBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
            res["ControlForegroundBrush"] = new SolidColorBrush(Colors.WhiteSmoke);
            res["ControlBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            res["SecondaryForegroundBrush"] = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
        }
        else
        {
            res["WindowBackgroundBrush"] = new SolidColorBrush(Colors.White);
            res["ControlBackgroundBrush"] = new SolidColorBrush(Colors.White);
            res["ControlForegroundBrush"] = new SolidColorBrush(Colors.Black);
            res["ControlBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xAC, 0xAC, 0xAC));
            res["SecondaryForegroundBrush"] = new SolidColorBrush(Colors.Gray);
        }

        // ComboBox's dropdown popup (DropDownBorder in its default template) renders using
        // these SystemColors keys directly, not whatever's set on the ComboBox control itself
        // -- without overriding them here, the popped-out item list stays stuck on the OS's
        // default white/black regardless of the app's own theme. Highlight/HighlightText cover
        // the hovered/selected item's colors inside that list.
        res[SystemColors.WindowBrushKey] = res["ControlBackgroundBrush"];
        res[SystemColors.WindowTextBrushKey] = res["ControlForegroundBrush"];
        res[SystemColors.HighlightBrushKey] = new SolidColorBrush(dark
            ? Color.FromRgb(0x3A, 0x3A, 0x3A)
            : Color.FromRgb(0xD0, 0xE0, 0xFF));
        res[SystemColors.HighlightTextBrushKey] = res["ControlForegroundBrush"];
    }

    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var dark = ResolveIsDark() ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref dark, sizeof(int));
        }
    }

    /// <summary>Applies resources + title bar now, and keeps both in sync if the user flips Windows theme while the app is open (unless an explicit override is set).</summary>
    public static void Track(Window window)
    {
        TrackedWindows.Add(window);
        window.Closed += (_, _) => TrackedWindows.Remove(window);

        ApplyResources();
        window.SourceInitialized += (_, _) => ApplyTitleBar(window);

        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General) return;
            if (Override != null) return; // explicit override active -- ignore OS theme changes
            window.Dispatcher.Invoke(() =>
            {
                ApplyResources();
                ApplyTitleBar(window);
            });
        };
    }

    private static void ApplyToAllTracked()
    {
        ApplyResources(); // resources are shared app-wide; only needs setting once
        foreach (var window in TrackedWindows.ToArray())
        {
            window.Dispatcher.Invoke(() => ApplyTitleBar(window));
        }
    }
}
