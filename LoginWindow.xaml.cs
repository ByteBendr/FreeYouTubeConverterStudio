using FreeYouTubeConverterStudio.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace FreeYouTubeConverterStudio;

/// <summary>
/// Embedded browser for signing in to YouTube. On "Save login" the session cookies are
/// exported to a cookies.txt that yt-dlp uses automatically.
/// </summary>
public sealed partial class LoginWindow : Window
{
    private const string StartUrl =
        "https://accounts.google.com/ServiceLogin?service=youtube&continue=https%3A%2F%2Fwww.youtube.com%2F";

    private readonly TaskCompletionSource<bool> _completion = new();
    private bool _saved;

    /// <summary>Completes with true if a login was saved, when the window closes.</summary>
    public Task<bool> Result => _completion.Task;

    public LoginWindow()
    {
        InitializeComponent();

        Title = "Sign in to YouTube";
        AppIcon.Apply(AppWindow);
        AppWindow.Resize(new SizeInt32(1000, 780));
        CenterOnScreen();

        RootGrid.Loaded += async (_, _) => await InitializeBrowserAsync();
        Closed += (_, _) => _completion.TrySetResult(_saved);
    }

    private void CenterOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var size = AppWindow.Size;
        AppWindow.Move(new PointInt32(
            area.X + Math.Max(0, (area.Width - size.Width) / 2),
            area.Y + Math.Max(0, (area.Height - size.Height) / 2)));
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            Directory.CreateDirectory(YouTubeLoginService.WebViewFolder);

            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", YouTubeLoginService.WebViewFolder);

            await Web.EnsureCoreWebView2Async();

            Web.CoreWebView2.NavigationCompleted += async (_, _) => await CheckSignedInAsync();
            Web.CoreWebView2.Navigate(StartUrl);
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, "Could not start the embedded browser",
                ex.Message + "  The Microsoft Edge WebView2 Runtime is required (it ships with Windows 11).");
        }
    }

    private async Task CheckSignedInAsync()
    {
        try
        {
            var cookies = await YouTubeLoginService.ReadCookiesAsync(Web.CoreWebView2);
            var signedIn = YouTubeLoginService.LooksSignedIn(cookies);

            SaveButton.IsEnabled = signedIn;
            StatusText.Text = signedIn
                ? "Signed in ✓  Click “Save login” when you're done."
                : "Sign in with your Google account, then click “Save login”.";
        }
        catch
        {
            // Cookie manager can be unavailable mid-navigation; the next navigation re-checks.
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;

        try
        {
            var (count, signedIn) = await YouTubeLoginService.SaveAsync(Web.CoreWebView2);

            if (!signedIn)
            {
                ShowInfo(InfoBarSeverity.Warning, "No YouTube login detected yet",
                    "Finish signing in (including any verification steps) and try again.");
                return;
            }

            _saved = true;
            Close();
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, "Could not save the login", ex.Message);
        }
        finally
        {
            if (!_saved) SaveButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowInfo(InfoBarSeverity severity, string title, string message)
    {
        Info.Severity = severity;
        Info.Title = title;
        Info.Message = message;
        Info.IsOpen = true;
    }
}
