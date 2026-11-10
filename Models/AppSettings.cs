using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FreeYouTubeConverterStudio.Models;

/// <summary>
/// All user-configurable options. Implements INotifyPropertyChanged so the Settings
/// page can two-way bind to it and the app can react (theme, backdrop, auto-save).
/// Serialized to settings.json in %LocalAppData%.
/// </summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    // ---- Dependencies ----
    private bool _checkDependenciesAtStartup = true;   // only re-checks when the last result wasn't "all found"
    public bool CheckDependenciesAtStartup { get => _checkDependenciesAtStartup; set => Set(ref _checkDependenciesAtStartup, value); }

    // ---- Download engine ----
    private string _engine = "Auto";             // Auto (YoutubeExplode, then yt-dlp) | YoutubeExplode | YtDlp
    public string Engine { get => _engine; set => Set(ref _engine, value); }

    private string _ytDlpPath = "";              // empty = auto-detect
    public string YtDlpPath { get => _ytDlpPath; set => Set(ref _ytDlpPath, value ?? ""); }

    private bool _useSavedLogin = true;          // use the cookies saved from the embedded browser sign-in
    public bool UseSavedLogin { get => _useSavedLogin; set => Set(ref _useSavedLogin, value); }

    private string _cookiesBrowser = "None";     // None | chrome | edge | firefox | brave | opera | vivaldi
    public string CookiesBrowser { get => _cookiesBrowser; set => Set(ref _cookiesBrowser, value); }

    private string _ytDlpExtraArgs = "";
    public string YtDlpExtraArgs { get => _ytDlpExtraArgs; set => Set(ref _ytDlpExtraArgs, value ?? ""); }

    // ---- Appearance ----
    private string _theme = "System";            // System | Light | Dark
    public string Theme { get => _theme; set => Set(ref _theme, value); }

    private string _backdrop = "MicaAlt";        // MicaAlt | Mica | Acrylic
    public string Backdrop { get => _backdrop; set => Set(ref _backdrop, value); }

    private bool _alwaysOnTop;
    public bool AlwaysOnTop { get => _alwaysOnTop; set => Set(ref _alwaysOnTop, value); }

    // ---- Downloads ----
    private string _outputFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "YouTube");
    public string OutputFolder { get => _outputFolder; set => Set(ref _outputFolder, value); }

    private string _defaultFormat = "MP4 (Video)";
    public string DefaultFormat { get => _defaultFormat; set => Set(ref _defaultFormat, value); }

    private string _defaultQuality = "Best";
    public string DefaultQuality { get => _defaultQuality; set => Set(ref _defaultQuality, value); }

    private string _filenameTemplate = "{title}";
    public string FilenameTemplate { get => _filenameTemplate; set => Set(ref _filenameTemplate, value); }

    private bool _createPlaylistSubfolder = true;
    public bool CreatePlaylistSubfolder { get => _createPlaylistSubfolder; set => Set(ref _createPlaylistSubfolder, value); }

    private bool _playlistIndexPrefix;
    public bool PlaylistIndexPrefix { get => _playlistIndexPrefix; set => Set(ref _playlistIndexPrefix, value); }

    private bool _overwriteExisting;
    public bool OverwriteExisting { get => _overwriteExisting; set => Set(ref _overwriteExisting, value); }

    private int _maxConcurrentDownloads = 2;
    public int MaxConcurrentDownloads { get => _maxConcurrentDownloads; set => Set(ref _maxConcurrentDownloads, Math.Clamp(value, 1, 10)); }

    private int _retryCount = 1;
    public int RetryCount { get => _retryCount; set => Set(ref _retryCount, Math.Clamp(value, 0, 5)); }

    private bool _autoStartQueue;
    public bool AutoStartQueue { get => _autoStartQueue; set => Set(ref _autoStartQueue, value); }

    private bool _singleVideoOnMixedLinks = true;
    public bool SingleVideoOnMixedLinks { get => _singleVideoOnMixedLinks; set => Set(ref _singleVideoOnMixedLinks, value); }

    // ---- Behavior ----
    private bool _autoPasteClipboard = true;
    public bool AutoPasteClipboard { get => _autoPasteClipboard; set => Set(ref _autoPasteClipboard, value); }

    private bool _openFolderWhenDone;
    public bool OpenFolderWhenDone { get => _openFolderWhenDone; set => Set(ref _openFolderWhenDone, value); }

    private bool _clearCompletedWhenDone;
    public bool ClearCompletedWhenDone { get => _clearCompletedWhenDone; set => Set(ref _clearCompletedWhenDone, value); }

    // ---- Subtitles ----
    private bool _downloadSubtitles;
    public bool DownloadSubtitles { get => _downloadSubtitles; set => Set(ref _downloadSubtitles, value); }

    private string _subtitleLanguage = "en";
    public string SubtitleLanguage { get => _subtitleLanguage; set => Set(ref _subtitleLanguage, value); }

    private bool _embedSubtitles;
    public bool EmbedSubtitles { get => _embedSubtitles; set => Set(ref _embedSubtitles, value); }

    // ---- FFmpeg ----
    private string _ffmpegPath = "ffmpeg.exe";
    public string FFmpegPath { get => _ffmpegPath; set => Set(ref _ffmpegPath, value); }

    private string _conversionPreset = "Medium";
    public string ConversionPreset { get => _conversionPreset; set => Set(ref _conversionPreset, value); }

    /// <summary>Copies every writable property from another instance (used by "Reset to defaults").</summary>
    public void CopyFrom(AppSettings other)
    {
        foreach (var p in typeof(AppSettings).GetProperties())
        {
            if (p.CanRead && p.CanWrite)
                p.SetValue(this, p.GetValue(other));
        }
    }

    /// <summary>Repairs values from a hand-edited / outdated settings file.</summary>
    public void Normalize()
    {
        if (Theme is not ("System" or "Light" or "Dark")) Theme = "System";
        if (Backdrop is not ("MicaAlt" or "Mica" or "Acrylic")) Backdrop = "MicaAlt";
        if (Engine is not ("Auto" or "YoutubeExplode" or "YtDlp")) Engine = "Auto";
        if (CookiesBrowser is not ("None" or "chrome" or "edge" or "firefox" or "brave" or "opera" or "vivaldi")) CookiesBrowser = "None";
        YtDlpPath ??= "";
        YtDlpExtraArgs ??= "";
        if (!Catalog.FormatOptions.Contains(DefaultFormat)) DefaultFormat = Catalog.FormatOptions[0];
        if (!Catalog.QualitiesFor(DefaultFormat).Contains(DefaultQuality)) DefaultQuality = "Best";
        if (!Catalog.Presets.Contains(ConversionPreset)) ConversionPreset = "Medium";
        if (string.IsNullOrWhiteSpace(OutputFolder)) OutputFolder = new AppSettings().OutputFolder;
        if (string.IsNullOrWhiteSpace(FilenameTemplate)) FilenameTemplate = "{title}";
        if (string.IsNullOrWhiteSpace(SubtitleLanguage)) SubtitleLanguage = "en";
        if (string.IsNullOrWhiteSpace(FFmpegPath)) FFmpegPath = "ffmpeg.exe";
        MaxConcurrentDownloads = MaxConcurrentDownloads; // re-clamp
        RetryCount = RetryCount;
    }
}
