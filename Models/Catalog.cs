namespace FreeYouTubeConverterStudio.Models;

public static class Catalog
{
    public static readonly string[] FormatOptions =
    {
        "MP4 (Video)",
        "WebM (Video)",
        "MP3 (Audio Only)",
        "M4A (Audio Only)"
    };

    public static readonly string[] VideoQualities =
    {
        "Best", "2160p", "1440p", "1080p", "720p", "480p", "360p", "240p", "144p", "Worst"
    };

    public static readonly string[] AudioQualities = { "Best", "High", "Medium", "Low", "Worst" };

    /// <summary>Names match YoutubeExplode.Converter.ConversionPreset.</summary>
    public static readonly string[] Presets =
    {
        "UltraFast", "SuperFast", "VeryFast", "Faster", "Fast", "Medium", "Slow", "Slower", "VerySlow"
    };

    public static bool IsAudioFormat(string? format) =>
        format?.Contains("Audio", StringComparison.OrdinalIgnoreCase) == true;

    public static string[] QualitiesFor(string? format) =>
        IsAudioFormat(format) ? AudioQualities : VideoQualities;
}
