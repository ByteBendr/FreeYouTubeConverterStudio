using FreeYouTubeConverterStudio.Models;
using Microsoft.Win32;

namespace FreeYouTubeConverterStudio.Services;

public enum DependencyKind { FFmpeg, YtDlp, Deno, WebView2 }

public sealed record DependencyStatus(
    DependencyKind Kind,
    string Name,
    string Purpose,
    string DownloadSize,
    bool Required,
    bool Found,
    string? Location);

public static class DependencyService
{
    /// <summary>Looks for every component. Cheap (file/registry lookups only).</summary>
    public static List<DependencyStatus> Check(AppSettings s)
    {
        var ffmpeg = FFmpegService.Resolve(s);
        var ytdlp = YtDlpService.ResolveExecutable(s);
        var deno = DenoService.Find();
        var webView2 = WebView2Version();

        return new List<DependencyStatus>
        {
            new(DependencyKind.FFmpeg, "FFmpeg",
                "Merges video + audio for 720p and above, converts to MP3, embeds subtitles.",
                "about 80 MB", true, ffmpeg != null, ffmpeg),

            new(DependencyKind.YtDlp, "yt-dlp",
                "Fallback download engine for when YouTube blocks the built-in one.",
                "about 20 MB", true, ytdlp != null, ytdlp),

            new(DependencyKind.Deno, "Deno",
                "JavaScript runtime that recent yt-dlp versions need for YouTube.",
                "installed with winget", true, deno != null, deno),

            new(DependencyKind.WebView2, "WebView2 Runtime",
                "Optional. Only needed for the built-in YouTube sign-in window.",
                "", false, webView2 != null, webView2 == null ? null : $"version {webView2}"),
        };
    }

    public static bool AllRequiredFound(IEnumerable<DependencyStatus> statuses) =>
        statuses.Where(d => d.Required).All(d => d.Found);

    /// <summary>
    /// Installs one component. Returns null on success, or an error message.
    /// Must be called from the UI thread (it updates settings).
    /// </summary>
    public static async Task<string?> InstallAsync(DependencyStatus item, IProgress<double>? progress, CancellationToken ct)
    {
        switch (item.Kind)
        {
            case DependencyKind.FFmpeg:
            {
                var path = await FFmpegService.DownloadAsync(progress, ct);
                AppState.Settings.FFmpegPath = path;
                return null;
            }

            case DependencyKind.YtDlp:
            {
                var destination = YtDlpService.DefaultInstallPath;
                await YtDlpService.DownloadLatestAsync(destination, progress, ct);
                AppState.Settings.YtDlpPath = destination;
                return null;
            }

            case DependencyKind.Deno:
            {
                var result = await DenoService.InstallAsync(ct);
                return result.Ok ? null : result.Message;
            }

            default:
                return "This component has to be installed manually.";
        }
    }

    /// <summary>Re-checks everything and updates hasDepends.bin. Returns the new result.</summary>
    public static bool RefreshStateFile()
    {
        var ok = AllRequiredFound(Check(AppState.Settings));
        try { DependencyStateFile.Write(ok); }
        catch (Exception ex) { AppLog.Info("Could not write hasDepends.bin: " + ex.Message); }
        return ok;
    }

    private static string? WebView2Version()
    {
        const string clientKey = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

        foreach (var (hive, view) in new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.CurrentUser, RegistryView.Default),
        })
        {
            try
            {
                using var key = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(clientKey);
                if (key?.GetValue("pv") is string v && v.Length > 0 && v != "0.0.0.0") return v;
            }
            catch { /* try next */ }
        }

        return null;
    }
}
