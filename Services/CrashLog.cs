using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;

namespace FreeYouTubeConverterStudio.Services;

/// <summary>Writes unexpected errors to crash.log and shows a short dialog instead of closing the app.</summary>
public static class CrashLog
{
    private static bool _dialogOpen;

    public static string FilePath => Path.Combine(SettingsService.Folder, "crash.log");

    public static void Write(Exception? ex, string? message = null)
    {
        try
        {
            Directory.CreateDirectory(SettingsService.Folder);
            File.AppendAllText(FilePath,
                $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====\r\n" +
                $"Message: {message}\r\n" +
                $"{ex}\r\n\r\n");
        }
        catch { /* nothing more we can do */ }

        Debug.WriteLine($"[Unhandled] {message} {ex}");
    }

    public static async void Report(Exception? ex, string? message)
    {
        Write(ex, message);

        try
        {
            if (_dialogOpen) return;
            if (App.MainWindow?.Content?.XamlRoot is not { } root) return;

            _dialogOpen = true;

            var text = !string.IsNullOrWhiteSpace(ex?.Message) ? ex!.Message : message ?? "Unknown error";
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = "Something went wrong",
                Content = $"{text}\n\nFull details were saved to:\n{FilePath}",
                CloseButtonText = "OK"
            };
            await dialog.ShowAsync();
        }
        catch { /* another dialog may already be open */ }
        finally
        {
            _dialogOpen = false;
        }
    }
}
