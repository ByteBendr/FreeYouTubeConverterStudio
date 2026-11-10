using System.Text.Json;
using FreeYouTubeConverterStudio.Models;

namespace FreeYouTubeConverterStudio.Services;

public static class SettingsService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreeYouTubeConverterStudio");

    public static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (s != null)
                {
                    s.Normalize();
                    return s;
                }
            }
        }
        catch
        {
            // Corrupt file: fall back to defaults.
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // Non-fatal.
        }
    }
}
