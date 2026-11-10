using System.IO.Compression;
using System.Runtime.InteropServices;
using FreeYouTubeConverterStudio.Models;

namespace FreeYouTubeConverterStudio.Services;

/// <summary>Finds FFmpeg and, when it's missing, downloads a Windows build into the app's tools folder.</summary>
public static class FFmpegService
{
    public static string ToolsFolder => YtDlpService.ToolsFolder;
    public static string DefaultInstallPath => Path.Combine(ToolsFolder, "ffmpeg.exe");

    private const string GyanUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    private const string BtbnX64Url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    private const string BtbnArm64Url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-winarm64-gpl.zip";

    /// <summary>
    /// Returns the ffmpeg.exe to use, or null if none can be found. Order: the configured path
    /// (file, folder or bare name on PATH), the app's tools folder, next to the app, then PATH.
    /// </summary>
    public static string? Resolve(AppSettings s)
    {
        var configured = s.FFmpegPath?.Trim().Trim('"');

        if (!string.IsNullOrEmpty(configured))
        {
            if (Path.IsPathRooted(configured) && File.Exists(configured)) return configured;

            if (Path.IsPathRooted(configured) && Directory.Exists(configured))
            {
                var inFolder = Path.Combine(configured, "ffmpeg.exe");
                if (File.Exists(inFolder)) return inFolder;
            }

            var onPath = YtDlpService.FindOnPath(configured);
            if (onPath != null) return onPath;
        }

        if (File.Exists(DefaultInstallPath)) return DefaultInstallPath;

        var beside = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(beside)) return beside;

        return YtDlpService.FindOnPath("ffmpeg.exe");
    }

    /// <summary>Downloads FFmpeg and extracts ffmpeg.exe into the tools folder. Returns its path.</summary>
    public static async Task<string> DownloadAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ToolsFolder);

        var urls = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? new[] { BtbnArm64Url, GyanUrl }
            : new[] { GyanUrl, BtbnX64Url };

        var zipPath = Path.Combine(Path.GetTempPath(), $"ffmpeg_{Guid.NewGuid():N}.zip");
        Exception? last = null;

        try
        {
            foreach (var url in urls)
            {
                try
                {
                    AppLog.Info("Downloading FFmpeg from " + url);
                    await DownloadFileAsync(url, zipPath, progress, ct);
                    ExtractFfmpeg(zipPath, DefaultInstallPath);
                    progress?.Report(1);
                    AppLog.Info("FFmpeg installed at " + DefaultInstallPath);
                    return DefaultInstallPath;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    AppLog.Info($"FFmpeg download from {url} failed: {ex.Message}");
                }
            }

            throw new InvalidOperationException("Could not download FFmpeg: " + last?.Message, last);
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { /* best effort */ }
        }
    }

    private static async Task DownloadFileAsync(string url, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FreeYouTubeConverterStudio");

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(destination);

        var buffer = new byte[131072];
        long done = 0;
        var lastReported = -1.0;
        int read;

        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;

            if (total > 0)
            {
                var fraction = 0.95 * done / total;
                if (fraction - lastReported >= 0.01)
                {
                    lastReported = fraction;
                    progress?.Report(fraction);
                }
            }
        }
    }

    private static void ExtractFfmpeg(string zipPath, string destination)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        var entry = archive.Entries.FirstOrDefault(e =>
                        e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                    ?? archive.Entries.FirstOrDefault(e =>
                        string.Equals(e.Name, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("ffmpeg.exe was not found inside the downloaded archive.");

        var temp = destination + ".tmp";
        entry.ExtractToFile(temp, overwrite: true);
        File.Move(temp, destination, overwrite: true);
    }
}
