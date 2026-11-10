using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FreeYouTubeConverterStudio.Models;

namespace FreeYouTubeConverterStudio.Services;

/// <summary>Basic metadata for one video, from either engine.</summary>
public sealed record VideoMeta(string Id, string Url, string Title, string Author, TimeSpan? Duration);

public sealed class YtDlpException : Exception
{
    public YtDlpException(string message) : base(message) { }
}

/// <summary>
/// Runs yt-dlp.exe as an external process: link resolving (-J) and downloading with live progress.
/// Used as an automatic fallback when YoutubeExplode fails, or as the only engine if chosen.
/// </summary>
public static class YtDlpService
{
    public const string NotFoundMessage =
        "yt-dlp was not found. Open Settings → Download engine and choose “Download latest”.";

    private const string DownloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    public static string ToolsFolder => Path.Combine(SettingsService.Folder, "tools");
    public static string DefaultInstallPath => Path.Combine(ToolsFolder, "yt-dlp.exe");

    // ==================================================================
    //  Locating the executable
    // ==================================================================

    public static string? ResolveExecutable(AppSettings s)
    {
        var configured = s.YtDlpPath?.Trim().Trim('"');
        if (!string.IsNullOrEmpty(configured))
        {
            if (File.Exists(configured)) return configured;
            var onPath = FindOnPath(configured);
            if (onPath != null) return onPath;
        }

        if (File.Exists(DefaultInstallPath)) return DefaultInstallPath;

        var beside = Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe");
        if (File.Exists(beside)) return beside;

        return FindOnPath("yt-dlp.exe");
    }

    internal static string? FindOnPath(string name)
    {
        if (name.IndexOfAny(new[] { '\\', '/' }) >= 0) return null;
        var fileName = Path.HasExtension(name) ? name : name + ".exe";

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }

    public static async Task<string> GetVersionAsync(AppSettings s, CancellationToken ct = default)
    {
        var exe = ResolveExecutable(s) ?? throw new YtDlpException(NotFoundMessage);

        using var process = Process.Start(NewStartInfo(exe, new[] { "--version" }))
                            ?? throw new YtDlpException("Could not start yt-dlp.");

        var outTask = process.StandardOutput.ReadToEndAsync(ct);
        var errTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var output = (await outTask).Trim();
        if (process.ExitCode != 0)
            throw new YtDlpException(LastErrorLine(await errTask) ?? $"yt-dlp exited with code {process.ExitCode}.");

        return output;
    }

    public static async Task DownloadLatestAsync(string destination, IProgress<double>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FreeYouTubeConverterStudio");

        using var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        var temp = destination + ".download";

        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = File.Create(temp))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        File.Move(temp, destination, overwrite: true);
    }


    public static async Task<(string? PlaylistTitle, List<VideoMeta> Videos)> GetInfoAsync(
        AppSettings s, string url, bool playlist, CancellationToken ct)
    {
        var exe = ResolveExecutable(s) ?? throw new YtDlpException(NotFoundMessage);

        var args = new List<string> { "-J", "--no-warnings", playlist ? "--yes-playlist" : "--no-playlist" };
        if (playlist) args.Add("--flat-playlist");
        using var cookies = CookieScope.Apply(args, s);
        args.Add(url);

        using var process = Process.Start(NewStartInfo(exe, args))
                            ?? throw new YtDlpException("Could not start yt-dlp.");
        using var kill = ct.Register(() => TryKill(process));

        var outTask = process.StandardOutput.ReadToEndAsync();
        var errTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        ct.ThrowIfCancellationRequested();

        var json = await outTask;
        var err = await errTask;

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
            throw new YtDlpException(WithHints(LastErrorLine(err) ?? $"yt-dlp exited with code {process.ExitCode}.", err, s));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            var list = new List<VideoMeta>();
            foreach (var e in entries.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var meta = ToMeta(e);
                if (meta != null) list.Add(meta);
            }
            return (Str(root, "title"), list);
        }

        var single = ToMeta(root) ?? throw new YtDlpException("yt-dlp returned no video information.");
        return (null, new List<VideoMeta> { single });
    }

    private static VideoMeta? ToMeta(JsonElement e)
    {
        var id = Str(e, "id");
        if (string.IsNullOrEmpty(id)) return null;

        var title = Str(e, "title") ?? id;
        var author = Str(e, "uploader") ?? Str(e, "channel") ?? "";

        TimeSpan? duration = null;
        if (e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number)
            duration = TimeSpan.FromSeconds(d.GetDouble());

        return new VideoMeta(id, $"https://www.youtube.com/watch?v={id}", title, author, duration);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ==================================================================
    //  Downloading
    // ==================================================================

    /// <summary>
    /// Downloads one item. <paramref name="basePathNoExt"/> is the target path without extension;
    /// returns the final file path.
    /// </summary>
    public static async Task<string> DownloadAsync(
        AppSettings s, DownloadItem item, string basePathNoExt, string expectedExt,
        IProgress<double> progress, CancellationToken ct)
    {
        var exe = ResolveExecutable(s) ?? throw new YtDlpException(NotFoundMessage);

        var isAudio = Catalog.IsAudioFormat(item.Format);
        var wantsMp4 = item.Format.StartsWith("MP4", StringComparison.OrdinalIgnoreCase);

        var args = new List<string>
        {
            "--newline",
            "--no-playlist",
            "--progress-template",
            "download:[DL] %(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s",
            "-o", basePathNoExt.Replace("%", "%%") + ".%(ext)s",
        };

        if (s.OverwriteExisting) args.Add("--force-overwrites");

        // ---- Format selection ----
        if (isAudio)
        {
            args.AddRange(new[]
            {
                "-f", "ba/b",
                "-x",
                "--audio-format", item.Format.StartsWith("MP3", StringComparison.OrdinalIgnoreCase) ? "mp3" : "m4a",
                "--audio-quality", AudioQualityValue(item.Quality)
            });
        }
        else
        {
            string selector;
            if (item.Quality.Equals("Worst", StringComparison.OrdinalIgnoreCase))
            {
                selector = "wv*+wa/w";
            }
            else if (int.TryParse(item.Quality.TrimEnd('p', 'P'), out var h))
            {
                selector = $"bv*[height<={h}]+ba/b[height<={h}]/bv*+ba/b";
            }
            else
            {
                selector = "bv*+ba/b";
            }

            args.AddRange(new[]
            {
                "-f", selector,
                // Highest resolution first, then fps; prefer codecs that fit the chosen container.
                "-S", wantsMp4 ? "res,fps,vcodec:h264,acodec:m4a" : "res,fps,vcodec:vp9,acodec:opus",
                "--merge-output-format", wantsMp4 ? "mp4" : "webm"
            });
        }

        // ---- FFmpeg (only pass it when the user configured a real location) ----
        var ff = FFmpegService.Resolve(s);
        if (ff != null)
        {
            args.Add("--ffmpeg-location");
            args.Add(ff);
        }

        // ---- Subtitles ----
        if (s.DownloadSubtitles)
        {
            args.AddRange(new[]
            {
                "--write-subs", "--write-auto-subs",
                "--sub-langs", s.SubtitleLanguage + ".*",
                "--convert-subs", "srt"
            });
            if (s.EmbedSubtitles && !isAudio && wantsMp4) args.Add("--embed-subs");
        }

        using var cookies = CookieScope.Apply(args, s);
        args.AddRange(SplitArgs(s.YtDlpExtraArgs));
        args.Add(item.Url);

        // ---- Run ----
        AppLog.Info("yt-dlp: " + exe + " " + string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
        using var process = Process.Start(NewStartInfo(exe, args))
                            ?? throw new YtDlpException("Could not start yt-dlp.");
        using var kill = ct.Register(() => TryKill(process));

        var expectedStreams = isAudio ? 1 : 2;   // video-only + audio-only are fetched one after another
        var destinations = 0;
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            var line = e.Data;
            if (string.IsNullOrEmpty(line)) return;

            if (line.StartsWith("[download] Destination:", StringComparison.Ordinal))
            {
                destinations++;
                return;
            }

            if (!line.StartsWith("[DL] ", StringComparison.Ordinal)) return;

            var parts = line[5..].Split('|');
            if (parts.Length < 3) return;

            double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var downloaded);
            if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var total) || total <= 0)
                double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out total);

            if (total <= 0) return;

            var fraction = Math.Clamp(downloaded / total, 0, 1);
            var overall = (Math.Max(destinations, 1) - 1 + fraction) / Math.Max(expectedStreams, destinations);
            progress.Report(Math.Min(overall, 0.99));
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) lock (stderr) stderr.AppendLine(e.Data);
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        ct.ThrowIfCancellationRequested();

        string errors;
        lock (stderr) errors = stderr.ToString();
        AppLog.Info($"yt-dlp exit code {process.ExitCode}. stderr: {errors}");

        if (process.ExitCode != 0)
            throw new YtDlpException(WithHints(LastErrorLine(errors) ?? $"yt-dlp exited with code {process.ExitCode}.", errors, s));

        return FindFinalFile(basePathNoExt, expectedExt);
    }

    /// <summary>Deletes leftovers (.part, .ytdl, per-format temp files) after a cancel or failure.</summary>
    public static void CleanupPartial(string basePathNoExt)
    {
        try
        {
            var dir = Path.GetDirectoryName(basePathNoExt);
            var name = Path.GetFileName(basePathNoExt);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            foreach (var file in Directory.EnumerateFiles(dir, name + ".*"))
            {
                var fileName = Path.GetFileName(file);
                var ext = Path.GetExtension(file).ToLowerInvariant();
                var isTemp = ext is ".part" or ".ytdl" or ".temp" or ".tmp"
                             || System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.f\d+\.[^.]+(\.part)?$");
                if (isTemp) File.Delete(file);
            }
        }
        catch { /* best effort */ }
    }

    private static string FindFinalFile(string basePathNoExt, string expectedExt)
    {
        var expected = basePathNoExt + "." + expectedExt;
        if (File.Exists(expected)) return expected;

        // yt-dlp may pick another container (e.g. mkv) when the streams can't be merged into the requested one.
        try
        {
            var dir = Path.GetDirectoryName(basePathNoExt)!;
            var name = Path.GetFileName(basePathNoExt);

            var match = Directory.EnumerateFiles(dir, name + ".*")
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is not (".srt" or ".vtt" or ".part" or ".ytdl" or ".json"))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            if (match != null) return match;
        }
        catch { /* ignore */ }

        return expected;
    }

    // ==================================================================
    //  Helpers
    // ==================================================================

    private static string AudioQualityValue(string quality) => quality switch
    {
        "High" => "2",
        "Medium" => "5",
        "Low" => "8",
        "Worst" => "10",
        _ => "0"
    };

    /// <summary>
    /// Adds cookie arguments: the saved embedded-browser login if present (copied to a private temp file,
    /// because yt-dlp rewrites its cookie file and parallel runs must not collide), else browser cookies.
    /// </summary>
    private sealed class CookieScope : IDisposable
    {
        private readonly string? _temp;
        private CookieScope(string? temp) => _temp = temp;

        public static CookieScope Apply(List<string> args, AppSettings s)
        {
            if (s.UseSavedLogin && YouTubeLoginService.HasSavedLogin)
            {
                try
                {
                    var temp = Path.Combine(Path.GetTempPath(), $"ytc_{Guid.NewGuid():N}.txt");
                    File.Copy(YouTubeLoginService.CookieFilePath, temp, overwrite: true);
                    args.Add("--cookies");
                    args.Add(temp);
                    return new CookieScope(temp);
                }
                catch { /* fall through to browser cookies */ }
            }

            if (!string.IsNullOrWhiteSpace(s.CookiesBrowser) && s.CookiesBrowser != "None")
            {
                args.Add("--cookies-from-browser");
                args.Add(s.CookiesBrowser);
            }

            return new CookieScope(null);
        }

        public void Dispose()
        {
            if (_temp == null) return;
            try { File.Delete(_temp); } catch { /* best effort */ }
        }
    }

    private static ProcessStartInfo NewStartInfo(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    private static string? LastErrorLine(string stderr)
    {
        return stderr
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            ?.Replace("ERROR:", "", StringComparison.OrdinalIgnoreCase).Trim();
    }

    private static string WithHints(string message, string fullStderr, AppSettings s)
    {
        if (fullStderr.Contains("JavaScript runtime", StringComparison.OrdinalIgnoreCase))
            message += "  — Recent yt-dlp versions need a JavaScript runtime for YouTube. Click “Install Deno” in Settings → Download engine.";

        var hasCookies = (s.UseSavedLogin && YouTubeLoginService.HasSavedLogin) ||
                         (!string.IsNullOrWhiteSpace(s.CookiesBrowser) && s.CookiesBrowser != "None");

        if ((fullStderr.Contains("Sign in", StringComparison.OrdinalIgnoreCase) ||
             fullStderr.Contains("not a bot", StringComparison.OrdinalIgnoreCase)) && !hasCookies)
            message += "  — Click “Sign in to YouTube” in Settings → Download engine.";

        return message;
    }

    /// <summary>Splits a command-line style string into arguments, honouring double quotes.</summary>
    private static IEnumerable<string> SplitArgs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        var sb = new StringBuilder();
        var inQuotes = false;

        foreach (var c in text)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                continue;
            }

            sb.Append(c);
        }

        if (sb.Length > 0) yield return sb.ToString();
    }
}
