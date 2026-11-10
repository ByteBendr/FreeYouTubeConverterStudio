namespace FreeYouTubeConverterStudio.Services;

/// <summary>
/// Tiny breadcrumb log (app.log). Every line is flushed to disk immediately, so even a hard crash
/// leaves a record of the last step that ran.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(SettingsService.Folder, "app.log");

    public static void StartSession()
    {
        try
        {
            Directory.CreateDirectory(SettingsService.Folder);
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > 1_000_000) info.Delete();
        }
        catch { /* ignore */ }

        Info($"==== session start | {Environment.OSVersion} | .NET {Environment.Version} ====");
    }

    public static void Info(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsService.Folder);
                File.AppendAllText(FilePath,
                    $"{DateTime.Now:HH:mm:ss.fff} [T{Environment.CurrentManagedThreadId}] {message}\r\n");
            }
        }
        catch { /* logging must never throw */ }
    }
}
