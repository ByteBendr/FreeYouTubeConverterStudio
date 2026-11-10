using FreeYouTubeConverterStudio.Services;
using Microsoft.UI.Xaml;

namespace FreeYouTubeConverterStudio;

public partial class App : Application
{
    public static MainWindow MainWindow { get; private set; } = null!;

    public App()
    {
        InitializeComponent();

        UnhandledException += (_, e) =>
        {
            CrashLog.Report(e.Exception, e.Message);
            e.Handled = true;  
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write(e.ExceptionObject as Exception, "AppDomain unhandled exception");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppState.Initialize();
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
