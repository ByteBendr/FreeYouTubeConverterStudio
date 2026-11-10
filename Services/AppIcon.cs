using Microsoft.UI.Windowing;

namespace FreeYouTubeConverterStudio.Services;

/// <summary>Applies the app logo to a window (title bar, taskbar button, Alt+Tab).</summary>
public static class AppIcon
{
    public static string IcoPath => Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");

    public static void Apply(AppWindow window)
    {
        try
        {
            if (File.Exists(IcoPath)) window.SetIcon(IcoPath);
        }
        catch (Exception ex)
        {
            AppLog.Info("Could not set window icon: " + ex.Message);
        }
    }
}
