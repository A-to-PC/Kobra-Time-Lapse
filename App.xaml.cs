using System.Windows;

namespace KobraTimeLapse;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Default ShutdownMode (OnLastWindowClose) would kill the whole app the instant
        // Setup closes, since at that point it's the only window WPF knows about --
        // MainWindow hasn't been created yet. Switch to explicit shutdown until MainWindow
        // exists, then hand shutdown control back to it.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var settings = Settings.Load();
        ThemeManager.SetOverride(settings.Theme switch { "Dark" => true, "Light" => false, _ => null });

        if (!settings.SetupComplete)
        {
            var setup = new SetupWindow(settings);
            if (setup.ShowDialog() != true)
            {
                // User closed Setup without completing it -- nothing usable to run yet.
                Shutdown();
                return;
            }
        }

        var main = new MainWindow(settings);
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
    }
}
