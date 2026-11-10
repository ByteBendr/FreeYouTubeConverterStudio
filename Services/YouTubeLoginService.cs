using System.Text;
using Microsoft.Web.WebView2.Core;

namespace FreeYouTubeConverterStudio.Services;

/// <summary>
/// Stores the YouTube/Google session captured from the embedded browser as a Netscape-format
/// cookies.txt, which yt-dlp reads via --cookies.
/// </summary>
public static class YouTubeLoginService
{
    /// <summary>Persistent WebView2 profile, so the sign-in survives between sessions.</summary>
    public static string WebViewFolder => Path.Combine(SettingsService.Folder, "webview");

    public static string CookieFilePath => Path.Combine(SettingsService.Folder, "youtube-cookies.txt");

    public static bool HasSavedLogin => File.Exists(CookieFilePath);

    public static DateTime? SavedAt => HasSavedLogin ? File.GetLastWriteTime(CookieFilePath) : null;

    // Any of these means the user is actually signed in to Google/YouTube.
    private static readonly string[] LoginCookieNames =
    {
        "SAPISID", "__Secure-3PAPISID", "__Secure-1PAPISID", "SID", "__Secure-3PSID", "LOGIN_INFO"
    };

    private static readonly string[] CookieUrls =
    {
        "https://www.youtube.com",
        "https://accounts.youtube.com",
        "https://www.google.com",
        "https://accounts.google.com"
    };

    public static async Task<List<CoreWebView2Cookie>> ReadCookiesAsync(CoreWebView2 core)
    {
        var unique = new Dictionary<string, CoreWebView2Cookie>();

        foreach (var url in CookieUrls)
        {
            var cookies = await core.CookieManager.GetCookiesAsync(url);
            foreach (var c in cookies)
                unique[$"{c.Domain}|{c.Path}|{c.Name}"] = c;
        }

        return unique.Values.ToList();
    }

    public static bool LooksSignedIn(IEnumerable<CoreWebView2Cookie> cookies) =>
        cookies.Any(c => LoginCookieNames.Contains(c.Name) && !string.IsNullOrEmpty(c.Value));

    /// <summary>Exports the current browser cookies. Returns (cookie count, signed in?).</summary>
    public static async Task<(int Count, bool SignedIn)> SaveAsync(CoreWebView2 core)
    {
        var cookies = await ReadCookiesAsync(core);
        var signedIn = LooksSignedIn(cookies);

        if (!signedIn) return (cookies.Count, false);

        var sb = new StringBuilder();
        sb.AppendLine("# Netscape HTTP Cookie File");
        sb.AppendLine("# Exported by Free YouTube Converter Studio. Do not share this file.");
        sb.AppendLine();

        // Session cookies get a long-ish expiry so cookie parsers don't discard them as expired.
        var sessionExpiry = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();

        foreach (var c in cookies)
        {
            long expires;
            if (c.IsSession)
            {
                expires = sessionExpiry;
            }
            else
            {
                // In the WinUI projection, Expires is a double: Unix time in seconds (negative = session).
                expires = double.IsFinite(c.Expires) ? (long)c.Expires : 0;
                if (expires <= 0) expires = sessionExpiry;
            }

            var includeSubdomains = c.Domain.StartsWith('.') ? "TRUE" : "FALSE";
            var prefix = c.IsHttpOnly ? "#HttpOnly_" : "";

            sb.Append(prefix).Append(c.Domain).Append('\t')
              .Append(includeSubdomains).Append('\t')
              .Append(string.IsNullOrEmpty(c.Path) ? "/" : c.Path).Append('\t')
              .Append(c.IsSecure ? "TRUE" : "FALSE").Append('\t')
              .Append(expires).Append('\t')
              .Append(c.Name).Append('\t')
              .Append(c.Value).Append('\n');
        }

        Directory.CreateDirectory(SettingsService.Folder);
        await File.WriteAllTextAsync(CookieFilePath, sb.ToString(), new UTF8Encoding(false));

        return (cookies.Count, true);
    }

    /// <summary>Forgets the saved login (cookie file and the embedded browser profile).</summary>
    public static void Remove()
    {
        try { if (File.Exists(CookieFilePath)) File.Delete(CookieFilePath); } catch { /* ignore */ }
        try { if (Directory.Exists(WebViewFolder)) Directory.Delete(WebViewFolder, recursive: true); } catch { /* may be in use */ }
    }
}
