using Microsoft.UI.Dispatching;
using FreeYouTubeConverterStudio.Models;

namespace FreeYouTubeConverterStudio.Services;

/// <summary>Global app state: settings (auto-saved on change) and the download manager.</summary>
public static class AppState
{
    private static DispatcherQueueTimer? _saveTimer;

    public static AppSettings Settings { get; private set; } = new();
    public static DownloadManager Manager { get; } = new();

    public static void Initialize()
    {
        AppLog.StartSession();
        Settings = SettingsService.Load();

        // Pick up tools installed since this app was launched (e.g. Deno via winget).
        DenoService.RefreshPath();

        _saveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => SettingsService.Save(Settings);

        Settings.PropertyChanged += (_, _) =>
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        };
    }

    public static void Shutdown()
    {
        _saveTimer?.Stop();
        SettingsService.Save(Settings);
        Manager.Shutdown();
    }
}
