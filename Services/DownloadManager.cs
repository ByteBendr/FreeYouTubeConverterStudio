using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using System.Diagnostics;
using System.Text;
using FreeYouTubeConverterStudio.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using YoutubeExplode;
using YoutubeExplode.Converter;
using YoutubeExplode.Videos;

namespace FreeYouTubeConverterStudio.Services;

/// <summary>
/// Owns the queue and runs downloads. All public members are meant to be called from the UI
/// thread; continuations stay on the UI thread so bound properties can be updated directly.
/// </summary>
public sealed class DownloadManager
{
    private readonly YoutubeClient _youtube = new();
    private readonly CancellationTokenSource _appCts = new();

    public ObservableCollection<DownloadItem> Queue { get; } = new();
    public bool IsRunning { get; private set; }

    /// <summary>Raised whenever an item is added/removed or changes status, or the run state changes.</summary>
    public event Action? ActivityChanged;

    /// <summary>Raised when a queue run ends: (completed, failed).</summary>
    public event Action<int, int>? QueueFinished;

    private static AppSettings S => AppState.Settings;

    private DispatcherQueue? _ui;

    /// <summary>
    /// Progress sink that is safe to call from any thread: it throttles updates and always applies
    /// them on the UI thread, where the bound queue row lives.
    /// </summary>
    private sealed class UiProgress : IProgress<double>
    {
        private readonly DispatcherQueue? _queue;
        private readonly DownloadItem _item;
        private double _last = -1;

        public UiProgress(DispatcherQueue? queue, DownloadItem item) { _queue = queue; _item = item; }

        public void Report(double value)
        {
            if (value < 1 && Math.Abs(value - _last) < 0.005) return;
            _last = value;

            if (_queue == null) _item.Progress = value;
            else _queue.TryEnqueue(() => _item.Progress = value);
        }
    }

    private IProgress<double> MakeProgress(DownloadItem item) => new UiProgress(_ui, item);

    // =====================================================================
    //  Adding
    // =====================================================================

    public async Task<int> AddFromUrlAsync(string url, string format, string quality, Action<string>? report = null)
    {
        var ct = _appCts.Token;

        var hasList = url.Contains("list=", StringComparison.OrdinalIgnoreCase);
        var hasVideo = url.Contains("v=", StringComparison.OrdinalIgnoreCase) ||
                       url.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase);
        var isPlaylist = hasList && !(S.SingleVideoOnMixedLinks && hasVideo);

        (string? PlaylistTitle, List<VideoMeta> Videos) result;

        if (S.Engine == "YtDlp")
        {
            report?.Invoke("Resolving with yt-dlp…");
            result = await YtDlpService.GetInfoAsync(S, url, isPlaylist, ct);
        }
        else
        {
            try
            {
                result = await FetchWithYoutubeExplodeAsync(url, isPlaylist, report, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception) when (S.Engine == "Auto" && YtDlpService.ResolveExecutable(S) != null)
            {
                report?.Invoke("YoutubeExplode couldn't read this link — trying yt-dlp…");
                result = await YtDlpService.GetInfoAsync(S, url, isPlaylist, ct);
            }
        }

        var index = 0;
        foreach (var v in result.Videos)
        {
            index++;
            AddItem(v.Id, v.Url, v.Title, v.Author, v.Duration,
                    isPlaylist ? "Playlist" : "Video", format, quality,
                    isPlaylist ? result.PlaylistTitle : null, isPlaylist ? index : 0);
        }

        ActivityChanged?.Invoke();
        return result.Videos.Count;
    }

    private async Task<(string? PlaylistTitle, List<VideoMeta> Videos)> FetchWithYoutubeExplodeAsync(
        string url, bool isPlaylist, Action<string>? report, CancellationToken ct)
    {
        var list = new List<VideoMeta>();

        if (isPlaylist)
        {
            var playlist = await _youtube.Playlists.GetAsync(url, ct);
            report?.Invoke($"Loading playlist “{playlist.Title}”…");

            await foreach (var v in _youtube.Playlists.GetVideosAsync(playlist.Id, ct))
                list.Add(new VideoMeta(v.Id.Value, v.Url, v.Title, v.Author.ChannelTitle, v.Duration));

            return (playlist.Title, list);
        }

        var video = await _youtube.Videos.GetAsync(url, ct);
        list.Add(new VideoMeta(video.Id.Value, video.Url, video.Title, video.Author.ChannelTitle, video.Duration));
        return (null, list);
    }

    private void AddItem(string id, string url, string title, string author, TimeSpan? duration,
                         string type, string format, string quality, string? playlistTitle, int index)
    {
        var thumb = new BitmapImage(new Uri($"https://i.ytimg.com/vi/{id}/mqdefault.jpg")) { DecodePixelWidth = 256 };

        Queue.Add(new DownloadItem
        {
            Url = url,
            Title = title,
            Author = author,
            Type = type,
            Format = format,
            Quality = quality,
            DurationText = FormatDuration(duration),
            Thumbnail = thumb,
            PlaylistTitle = playlistTitle,
            PlaylistIndex = index
        });
    }

    private static string FormatDuration(TimeSpan? d)
    {
        if (d is null) return "";
        var t = d.Value;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    // =====================================================================
    //  Queue control
    // =====================================================================

    public async Task RunQueueAsync()
    {
        if (IsRunning) return;

        var pending = Queue.Where(i => i.Status is DownloadStatus.Queued or DownloadStatus.Failed).ToList();
        if (pending.Count == 0) return;

        _ui = DispatcherQueue.GetForCurrentThread();
        AppLog.Info($"RunQueue: {pending.Count} pending, engine={S.Engine}, output={S.OutputFolder}, ffmpeg={S.FFmpegPath}");
        Directory.CreateDirectory(S.OutputFolder); // may throw -> caller shows the error

        foreach (var item in pending)
        {
            item.ErrorMessage = null;
            item.Progress = 0;
            item.Status = DownloadStatus.Queued;
        }

        IsRunning = true;
        ActivityChanged?.Invoke();

        try
        {
            using var gate = new SemaphoreSlim(Math.Max(1, S.MaxConcurrentDownloads));

            await Task.WhenAll(pending.Select(async item =>
            {
                try
                {
                    await gate.WaitAsync(_appCts.Token);
                }
                catch (OperationCanceledException)
                {
                    SetStatus(item, DownloadStatus.Canceled);
                    return;
                }

                try
                {
                    if (item.Status == DownloadStatus.Queued)
                        await ProcessAsync(item);
                }
                finally
                {
                    gate.Release();
                }
            }));
        }
        finally
        {
            IsRunning = false;
        }

        var completed = pending.Count(i => i.Status == DownloadStatus.Completed);
        var failed = pending.Count(i => i.Status == DownloadStatus.Failed);

        if (S.ClearCompletedWhenDone)
        {
            foreach (var i in pending.Where(i => i.Status == DownloadStatus.Completed).ToList())
                Queue.Remove(i);
        }

        if (S.OpenFolderWhenDone && completed > 0)
            OpenFolder(S.OutputFolder);

        ActivityChanged?.Invoke();
        QueueFinished?.Invoke(completed, failed);
    }

    public void Cancel(DownloadItem item)
    {
        if (item.Status == DownloadStatus.Downloading) item.Cts?.Cancel();
        else if (item.Status == DownloadStatus.Queued) SetStatus(item, DownloadStatus.Canceled);
    }

    public void CancelAll()
    {
        foreach (var item in Queue.ToList()) Cancel(item);
    }

    public void Remove(DownloadItem item)
    {
        if (item.Status == DownloadStatus.Downloading) return;
        Queue.Remove(item);
        ActivityChanged?.Invoke();
    }

    public void ClearFinished()
    {
        foreach (var i in Queue.Where(i => i.Status is DownloadStatus.Completed or DownloadStatus.Canceled).ToList())
            Queue.Remove(i);
        ActivityChanged?.Invoke();
    }

    public void Shutdown()
    {
        _appCts.Cancel();
        foreach (var i in Queue) i.Cts?.Cancel();
    }

    private void SetStatus(DownloadItem item, DownloadStatus status)
    {
        item.Status = status;
        ActivityChanged?.Invoke();
    }

    // =====================================================================
    //  Processing one item
    // =====================================================================

    private async Task ProcessAsync(DownloadItem item)
    {
        item.Cts = CancellationTokenSource.CreateLinkedTokenSource(_appCts.Token);
        var ct = item.Cts.Token;
        var attempt = 0;

        SetStatus(item, DownloadStatus.Downloading);

        while (true)
        {
            try
            {
                item.Progress = 0;
                AppLog.Info($"Start: {item.Title} [{item.Format} / {item.Quality}] attempt {attempt + 1}");
                await DownloadCoreAsync(item, ct);
                AppLog.Info("Finished OK");
                item.Progress = 1;
                SetStatus(item, DownloadStatus.Completed);
                return;
            }
            catch (OperationCanceledException)
            {
                DeletePartial(item);
                SetStatus(item, DownloadStatus.Canceled);
                return;
            }
            catch (Exception ex)
            {
                AppLog.Info("FAILED: " + ex);
                DeletePartial(item);

                if (attempt < S.RetryCount)
                {
                    attempt++;
                    try { await Task.Delay(1500, ct); }
                    catch (OperationCanceledException) { SetStatus(item, DownloadStatus.Canceled); return; }
                    continue;
                }

                item.ErrorMessage = ex.Message;
                SetStatus(item, DownloadStatus.Failed);
                return;
            }
        }
    }

    private async Task DownloadCoreAsync(DownloadItem item, CancellationToken ct)
    {
        var engine = S.Engine;

        if (engine == "YtDlp")
        {
            item.Engine = "yt-dlp";
            await DownloadWithYtDlpAsync(item, ct);
            return;
        }

        try
        {
            item.Engine = "YoutubeExplode";
            await DownloadWithYoutubeExplodeAsync(item, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (engine == "Auto")
        {
            AppLog.Info("YoutubeExplode failed, considering yt-dlp fallback: " + ex);
            if (YtDlpService.ResolveExecutable(S) == null)
            {
                throw new InvalidOperationException(
                    $"{ex.Message}  (No yt-dlp fallback available — install it in Settings → Download engine.)");
            }

            DeletePartial(item);
            item.Progress = 0;
            item.Engine = "yt-dlp (fallback)";
            await DownloadWithYtDlpAsync(item, ct);
        }
    }

    /// <summary>Builds the final output path (folder, playlist subfolder, file name, uniqueness).</summary>
    private string PrepareOutputPath(DownloadItem item, string extension)
    {
        var s = S;

        var folder = s.OutputFolder;
        if (item.PlaylistTitle != null && s.CreatePlaylistSubfolder)
            folder = Path.Combine(folder, SanitizeFileName(item.PlaylistTitle));
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, BuildFileName(item, extension, s));
        if (!s.OverwriteExisting) path = MakeUniquePath(path);

        item.OutputPath = path;
        return path;
    }

    private async Task DownloadWithYtDlpAsync(DownloadItem item, CancellationToken ct)
    {
        var isAudio = Catalog.IsAudioFormat(item.Format);
        var extension = isAudio
            ? (item.Format.StartsWith("MP3", StringComparison.OrdinalIgnoreCase) ? "mp3" : "m4a")
            : (item.Format.StartsWith("MP4", StringComparison.OrdinalIgnoreCase) ? "mp4" : "webm");

        var path = PrepareOutputPath(item, extension);
        var basePath = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));

        var progress = MakeProgress(item);
        item.OutputPath = await YtDlpService.DownloadAsync(S, item, basePath, extension, progress, ct);
    }

    private async Task DownloadWithYoutubeExplodeAsync(DownloadItem item, CancellationToken ct)
    {
        var s = S;
        var videoId = VideoId.Parse(item.Url);

        var manifest = await _youtube.Videos.Streams.GetManifestAsync(videoId, ct);
        var resolved = StreamResolver.Resolve(manifest, item.Format, item.Quality);
        if (resolved.Streams.Count == 0)
            throw new InvalidOperationException("No matching stream was found for the requested format/quality.");

        AppLog.Info($"Resolved: ext={resolved.Extension}, direct={resolved.Direct}, streams=[" +
                    string.Join(", ", resolved.Streams.Select(x => $"{x.Container.Name} {x.Size}")) + "]");

        var path = PrepareOutputPath(item, resolved.Extension);
        AppLog.Info("Output path: " + path);

        var progress = MakeProgress(item);

        if (resolved.Direct && resolved.Streams.Count == 1)
        {
            await _youtube.Videos.Streams.DownloadAsync(resolved.Streams[0], path, progress, ct);
        }
        else
        {
            var preset = Enum.TryParse<ConversionPreset>(s.ConversionPreset, out var p) ? p : ConversionPreset.Medium;

            var request = new ConversionRequestBuilder(path)
                .SetContainer(resolved.Extension)
                .SetFFmpegPath(FFmpegService.Resolve(s) ?? s.FFmpegPath)
                .SetPreset(preset)
                .Build();

            await _youtube.Videos.DownloadAsync(resolved.Streams, request, progress, ct);
        }

        if (s.DownloadSubtitles)
            await TryDownloadSubtitlesAsync(videoId, path, ct);
    }

    private static void DeletePartial(DownloadItem item)
    {
        try
        {
            if (item.OutputPath != null)
            {
                if (File.Exists(item.OutputPath))
                    File.Delete(item.OutputPath);

                // yt-dlp leaves .part / per-format temp files behind.
                YtDlpService.CleanupPartial(Path.Combine(
                    Path.GetDirectoryName(item.OutputPath) ?? ".",
                    Path.GetFileNameWithoutExtension(item.OutputPath)));
            }
        }
        catch { /* best effort */ }
    }

    // =====================================================================
    //  Subtitles (best-effort; never fails the download)
    // =====================================================================

    private async Task TryDownloadSubtitlesAsync(VideoId videoId, string mediaPath, CancellationToken ct)
    {
        try
        {
            var manifest = await _youtube.Videos.ClosedCaptions.GetManifestAsync(videoId, ct);
            var track = manifest.Tracks.FirstOrDefault(t =>
                string.Equals(t.Language.Code, S.SubtitleLanguage, StringComparison.OrdinalIgnoreCase));
            if (track == null) return;

            var srt = Path.ChangeExtension(mediaPath, ".srt");
            await _youtube.Videos.ClosedCaptions.DownloadAsync(track, srt, null, ct);

            if (S.EmbedSubtitles && mediaPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) && File.Exists(srt))
                await EmbedSubtitlesAsync(mediaPath, srt, FFmpegService.Resolve(S) ?? S.FFmpegPath, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* subtitles are a bonus */ }
    }

    private static async Task EmbedSubtitlesAsync(string video, string srt, string ffmpeg, CancellationToken ct)
    {
        var temp = Path.Combine(
            Path.GetDirectoryName(video) ?? ".",
            Path.GetFileNameWithoutExtension(video) + "_withsubs" + Path.GetExtension(video));

        var psi = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(ffmpeg) ? "ffmpeg" : ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var a in new[] { "-y", "-i", video, "-i", srt, "-c", "copy", "-c:s", "mov_text", temp })
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi);
        if (process == null) return;

        // Drain output so ffmpeg never blocks on a full pipe.
        _ = process.StandardOutput.ReadToEndAsync(ct);
        _ = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode == 0 && File.Exists(temp))
        {
            File.Delete(video);
            File.Move(temp, video);
        }
        else if (File.Exists(temp))
        {
            File.Delete(temp);
        }
    }

    // =====================================================================
    //  File naming
    // =====================================================================

    private static string BuildFileName(DownloadItem item, string extension, AppSettings s)
    {
        var template = string.IsNullOrWhiteSpace(s.FilenameTemplate) ? "{title}" : s.FilenameTemplate;

        var name = template
            .Replace("{title}", item.Title)
            .Replace("{author}", item.Author)
            .Replace("{id}", TryGetVideoId(item.Url))
            .Replace("{quality}", item.Quality)
            .Replace("{format}", item.Format)
            .Replace("{index}", item.PlaylistIndex > 0 ? item.PlaylistIndex.ToString("000") : "")
            .Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd"));

        if (s.PlaylistIndexPrefix && item.PlaylistIndex > 0 && !template.Contains("{index}"))
            name = $"{item.PlaylistIndex:000} - {name}";

        name = SanitizeFileName(name);
        if (name.Length > 150) name = name[..150].TrimEnd();
        if (string.IsNullOrWhiteSpace(name)) name = "download";

        return $"{name}.{extension}";
    }

    private static string TryGetVideoId(string url)
    {
        try { return VideoId.Parse(url).Value; }
        catch { return "unknown"; }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString().Trim().TrimEnd('.');
    }

    private static string MakeUniquePath(string filePath)
    {
        if (!File.Exists(filePath)) return filePath;

        var dir = Path.GetDirectoryName(filePath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath);

        var n = 1;
        string candidate;
        do { candidate = Path.Combine(dir, $"{name} ({n++}){ext}"); }
        while (File.Exists(candidate));
        return candidate;
    }

    public static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch { /* ignore */ }
    }
}
