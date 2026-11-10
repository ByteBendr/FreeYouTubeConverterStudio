using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FreeYouTubeConverterStudio.Models;

public enum DownloadStatus { Queued, Downloading, Completed, Failed, Canceled }

/// <summary>One row in the queue. Notifies the UI so progress / status update live.</summary>
public sealed class DownloadItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private static readonly Brush GrayBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 140, 140, 140));
    private static readonly Brush BlueBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 76, 154, 255));
    private static readonly Brush GreenBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 46, 160, 67));
    private static readonly Brush RedBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 229, 72, 77));

    // ---- Static info ----
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string Type { get; set; } = "Video";         // Video | Playlist
    public string Format { get; set; } = "";
    public string Quality { get; set; } = "";
    public string DurationText { get; set; } = "";
    public Visibility DurationVisibility => string.IsNullOrEmpty(DurationText) ? Visibility.Collapsed : Visibility.Visible;
    public ImageSource? Thumbnail { get; set; }
    public string? PlaylistTitle { get; set; }
    public int PlaylistIndex { get; set; }

    public string Subtitle =>
        $"{Author}  ·  {Format}  ·  {Quality}" + (PlaylistTitle != null ? $"  ·  {PlaylistTitle}" : "");

    // ---- Runtime state ----
    public CancellationTokenSource? Cts { get; set; }
    public string? OutputPath { get; set; }

    private DownloadStatus _status = DownloadStatus.Queued;
    public DownloadStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
                Raise(nameof(StatusText), nameof(StatusBrush), nameof(ProgressVisibility),
                      nameof(CancelVisibility), nameof(RemoveVisibility), nameof(IsIndeterminate));
        }
    }

    private double _progress;
    public double Progress
    {
        get => _progress;
        set
        {
            if (Set(ref _progress, value))
                Raise(nameof(ProgressValue), nameof(ProgressText), nameof(IsIndeterminate), nameof(StatusText));
        }
    }

    private string _engine = "";
    /// <summary>Which engine is handling this item (shown next to the status).</summary>
    public string Engine
    {
        get => _engine;
        set { if (Set(ref _engine, value)) Raise(nameof(StatusText)); }
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        set { if (Set(ref _errorMessage, value)) Raise(nameof(StatusText)); }
    }

    // ---- Derived (bound by the UI) ----
    public double ProgressValue => Math.Clamp(_progress, 0, 1) * 100;
    public string ProgressText => $"{Math.Clamp(_progress, 0, 1):P0}";
    public bool IsIndeterminate => _status == DownloadStatus.Downloading && _progress <= 0;

    public string StatusText => _status switch
    {
        DownloadStatus.Queued => "Queued",
        DownloadStatus.Downloading => (_progress <= 0 ? "Starting…" : "Downloading") +
                                      (string.IsNullOrEmpty(_engine) ? "" : $"  ·  {_engine}"),
        DownloadStatus.Completed => "Completed",
        DownloadStatus.Failed => string.IsNullOrWhiteSpace(_errorMessage) ? "Failed" : $"Failed — {_errorMessage}",
        DownloadStatus.Canceled => "Canceled",
        _ => ""
    };

    public Brush StatusBrush => _status switch
    {
        DownloadStatus.Downloading => BlueBrush,
        DownloadStatus.Completed => GreenBrush,
        DownloadStatus.Failed => RedBrush,
        _ => GrayBrush
    };

    public Visibility ProgressVisibility => _status == DownloadStatus.Downloading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CancelVisibility => _status is DownloadStatus.Downloading or DownloadStatus.Queued ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RemoveVisibility => _status == DownloadStatus.Downloading ? Visibility.Collapsed : Visibility.Visible;
}
