using System.ComponentModel;
using System.Diagnostics;
using FreeYouTubeConverterStudio.Models;
using FreeYouTubeConverterStudio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FreeYouTubeConverterStudio.Pages;

public sealed partial class SettingsPage : Page
{
    public AppSettings Settings { get; } = AppState.Settings;

    private bool _loading;

    public SettingsPage()
    {
        // NumberBoxes clamp their initial value to Minimum and raise ValueChanged while the XAML loads;
        // without this guard that would silently overwrite the saved settings.
        _loading = true;
        InitializeComponent();

        CmbFormat.ItemsSource = Catalog.FormatOptions;
        CmbPreset.ItemsSource = Catalog.Presets;

        var v = typeof(App).Assembly.GetName().Version;
        VersionText.Text = v == null ? "" : $"Version {v.Major}.{v.Minor}.{v.Build}";
        _loading = false;
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        Settings.PropertyChanged += OnSettingsChanged;
        RefreshControls();
        RefreshLoginStatus();
        RefreshDenoStatus();
        RefreshDepsStatus();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        Settings.PropertyChanged -= OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_loading) RefreshControls();
    }

    // ------------------------------------------------------------------
    //  Controls that can't be bound directly (combo boxes / number boxes)
    // ------------------------------------------------------------------

    private void RefreshControls()
    {
        _loading = true;

        SelectByTag(CmbTheme, Settings.Theme);
        SelectByTag(CmbBackdrop, Settings.Backdrop);
        SelectByTag(CmbEngine, Settings.Engine);
        SelectByTag(CmbCookies, Settings.CookiesBrowser);
        CmbFormat.SelectedItem = Settings.DefaultFormat;
        FillQualities();
        CmbPreset.SelectedItem = Settings.ConversionPreset;
        NumConcurrent.Value = Settings.MaxConcurrentDownloads;
        NumRetry.Value = Settings.RetryCount;

        _loading = false;
    }

    private void FillQualities()
    {
        var options = Catalog.QualitiesFor(Settings.DefaultFormat);
        CmbQuality.ItemsSource = options;

        if (!options.Contains(Settings.DefaultQuality))
            Settings.DefaultQuality = options[0];

        CmbQuality.SelectedItem = Settings.DefaultQuality;
    }

    private static void SelectByTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if ((item.Tag as string) == tag)
            {
                box.SelectedItem = item;
                return;
            }
        }
    }

    private void CmbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CmbTheme.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        Settings.Theme = tag;
    }

    private void CmbBackdrop_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CmbBackdrop.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        Settings.Backdrop = tag;
    }

    private void CmbEngine_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CmbEngine.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        Settings.Engine = tag;
    }

    private void CmbCookies_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CmbCookies.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        Settings.CookiesBrowser = tag;
    }

    private void CmbFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CmbFormat.SelectedItem is not string format) return;

        _loading = true;
        Settings.DefaultFormat = format;
        FillQualities();
        _loading = false;
    }

    private void CmbQuality_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CmbQuality.SelectedItem is not string q) return;
        Settings.DefaultQuality = q;
    }

    private void CmbPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CmbPreset.SelectedItem is not string p) return;
        Settings.ConversionPreset = p;
    }

    private void NumConcurrent_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading) return;
        if (double.IsNaN(args.NewValue)) { sender.Value = Settings.MaxConcurrentDownloads; return; }
        Settings.MaxConcurrentDownloads = (int)args.NewValue;
    }

    private void NumRetry_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading) return;
        if (double.IsNaN(args.NewValue)) { sender.Value = Settings.RetryCount; return; }
        Settings.RetryCount = (int)args.NewValue;
    }

    // ------------------------------------------------------------------
    //  Buttons
    // ------------------------------------------------------------------

    private async void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var path = await Pickers.PickFolderAsync();
        if (path != null) Settings.OutputFolder = path;
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e) =>
        DownloadManager.OpenFolder(Settings.OutputFolder);

    private async void BrowseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var path = await Pickers.PickFileAsync(".exe");
        if (path != null) Settings.FFmpegPath = path;
    }

    private async void TestFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = FFmpegService.Resolve(Settings) ?? (string.IsNullOrWhiteSpace(Settings.FFmpegPath) ? "ffmpeg" : Settings.FFmpegPath),
                Arguments = "-version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFmpeg.");
            var firstLine = await process.StandardOutput.ReadLineAsync();
            await process.WaitForExitAsync();

            FfmpegInfo.Severity = process.ExitCode == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            FfmpegInfo.Title = process.ExitCode == 0 ? "FFmpeg is working" : "FFmpeg returned an error";
            FfmpegInfo.Message = firstLine ?? "";
        }
        catch (Exception ex)
        {
            FfmpegInfo.Severity = InfoBarSeverity.Error;
            FfmpegInfo.Title = "FFmpeg not found";
            FfmpegInfo.Message = ex.Message;
        }

        FfmpegInfo.IsOpen = true;
    }

    // ------------------------------------------------------------------
    //  Dependencies
    // ------------------------------------------------------------------

    private void RefreshDepsStatus()
    {
        var state = DependencyStateFile.Read();

        DepsCard.Header = state == null
            ? "Required components — not checked yet"
            : state.AllFound
                ? $"Required components — all found (checked {state.CheckedAt:g})"
                : $"Required components — something is missing (checked {state.CheckedAt:g})";
    }

    private async void CheckDeps_Click(object sender, RoutedEventArgs e)
    {
        BtnCheckDeps.IsEnabled = false;
        DepsRing.IsActive = true;
        DepsRing.Visibility = Visibility.Visible;

        try
        {
            var result = await DependencyFlow.RunAsync(XamlRoot);

            var lines = result.Statuses.Select(s =>
                $"{(s.Found ? "✔" : s.Required ? "✖" : "•")}  {s.Name}" +
                (s.Required ? "" : " (optional)") +
                (s.Found ? $" — {s.Location}" : " — not found"));

            ShowInfo(DepsInfo,
                result.AllFound ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                result.AllFound ? "All required components were found" : "Some required components are missing",
                string.Join("\n", lines));
        }
        catch (Exception ex)
        {
            ShowInfo(DepsInfo, InfoBarSeverity.Error, "Could not check dependencies", ex.Message);
        }
        finally
        {
            DepsRing.IsActive = false;
            DepsRing.Visibility = Visibility.Collapsed;
            BtnCheckDeps.IsEnabled = true;
            RefreshDepsStatus();
            RefreshDenoStatus();
        }
    }

    private async void DownloadFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        BtnDownloadFfmpeg.IsEnabled = false;
        ShowInfo(FfmpegInfo, InfoBarSeverity.Informational, "Downloading FFmpeg…", "0%");

        try
        {
            var progress = new Progress<double>(p => FfmpegInfo.Message = $"{p:P0}");
            var path = await FFmpegService.DownloadAsync(progress, default);

            Settings.FFmpegPath = path;
            ShowInfo(FfmpegInfo, InfoBarSeverity.Success, "FFmpeg installed", path);
        }
        catch (Exception ex)
        {
            ShowInfo(FfmpegInfo, InfoBarSeverity.Error, "Could not download FFmpeg", ex.Message);
        }
        finally
        {
            BtnDownloadFfmpeg.IsEnabled = true;
            DependencyService.RefreshStateFile();
            RefreshDepsStatus();
        }
    }

    // ------------------------------------------------------------------
    //  yt-dlp
    // ------------------------------------------------------------------

    private async void BrowseYtDlp_Click(object sender, RoutedEventArgs e)
    {
        var path = await Pickers.PickFileAsync(".exe");
        if (path != null) Settings.YtDlpPath = path;
    }

    private async void TestYtDlp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var version = await YtDlpService.GetVersionAsync(Settings);
            ShowYtDlpInfo(InfoBarSeverity.Success, "yt-dlp is working", $"Version {version}");
        }
        catch (Exception ex)
        {
            ShowYtDlpInfo(InfoBarSeverity.Error, "yt-dlp is not available", ex.Message);
        }
    }

    private async void InstallYtDlp_Click(object sender, RoutedEventArgs e)
    {
        BtnInstallYtDlp.IsEnabled = false;
        YtDlpRing.IsActive = true;
        YtDlpRing.Visibility = Visibility.Visible;
        ShowYtDlpInfo(InfoBarSeverity.Informational, "Downloading yt-dlp…", "");

        try
        {
            var destination = YtDlpService.DefaultInstallPath;
            await YtDlpService.DownloadLatestAsync(destination, null);

            Settings.YtDlpPath = destination;
            var version = await YtDlpService.GetVersionAsync(Settings);
            ShowYtDlpInfo(InfoBarSeverity.Success, "yt-dlp installed", $"Version {version} — saved to {destination}");
        }
        catch (Exception ex)
        {
            ShowYtDlpInfo(InfoBarSeverity.Error, "Could not download yt-dlp", ex.Message);
        }
        finally
        {
            YtDlpRing.IsActive = false;
            YtDlpRing.Visibility = Visibility.Collapsed;
            BtnInstallYtDlp.IsEnabled = true;
            DependencyService.RefreshStateFile();
            RefreshDepsStatus();
        }
    }

    // ------------------------------------------------------------------
    //  YouTube sign-in (embedded browser)
    // ------------------------------------------------------------------

    private void RefreshLoginStatus()
    {
        var saved = YouTubeLoginService.SavedAt;

        SignInText.Text = saved == null ? "Sign in to YouTube" : "Sign in again";
        BtnSignOut.IsEnabled = saved != null;

        LoginCard.Header = saved == null
            ? "YouTube account — not signed in"
            : $"YouTube account — signed in (saved {saved:g})";
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        BtnSignIn.IsEnabled = false;

        try
        {
            var window = new LoginWindow();
            window.Activate();

            var saved = await window.Result;

            if (saved)
            {
                ShowInfo(LoginInfo, InfoBarSeverity.Success, "Signed in",
                    "Your YouTube session was saved. yt-dlp will use it automatically.");
            }
        }
        catch (Exception ex)
        {
            ShowInfo(LoginInfo, InfoBarSeverity.Error, "Could not open the sign-in window", ex.Message);
        }
        finally
        {
            BtnSignIn.IsEnabled = true;
            RefreshLoginStatus();
        }
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Remove saved YouTube login?",
            Content = "The saved cookies and the built-in browser's profile will be deleted. You can sign in again at any time.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        YouTubeLoginService.Remove();
        RefreshLoginStatus();
        ShowInfo(LoginInfo, InfoBarSeverity.Informational, "Login removed", "");
    }

    // ------------------------------------------------------------------
    //  Deno (JavaScript runtime for yt-dlp)
    // ------------------------------------------------------------------

    private void RefreshDenoStatus()
    {
        var path = DenoService.Find();
        DenoCard.Header = path == null ? "JavaScript runtime (Deno) — not found" : "JavaScript runtime (Deno) — installed";
        BtnDeno.Content = path == null ? "Install Deno" : "Reinstall / update";
    }

    private async void InstallDeno_Click(object sender, RoutedEventArgs e)
    {
        BtnDeno.IsEnabled = false;
        DenoRing.IsActive = true;
        DenoRing.Visibility = Visibility.Visible;
        ShowInfo(DenoInfo, InfoBarSeverity.Informational, "Running winget install DenoLand.Deno…",
            "This can take a minute. Windows may show a prompt.");

        try
        {
            var result = await DenoService.InstallAsync();
            ShowInfo(DenoInfo,
                result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error,
                result.Ok ? "Deno installed" : "Could not install Deno",
                result.Message);
        }
        catch (Exception ex)
        {
            ShowInfo(DenoInfo, InfoBarSeverity.Error, "Could not install Deno", ex.Message);
        }
        finally
        {
            DenoRing.IsActive = false;
            DenoRing.Visibility = Visibility.Collapsed;
            BtnDeno.IsEnabled = true;
            RefreshDenoStatus();
            DependencyService.RefreshStateFile();
            RefreshDepsStatus();
        }
    }

    private static void ShowInfo(InfoBar bar, InfoBarSeverity severity, string title, string message)
    {
        bar.Severity = severity;
        bar.Title = title;
        bar.Message = message;
        bar.IsOpen = true;
    }

    private void ShowYtDlpInfo(InfoBarSeverity severity, string title, string message)
    {
        YtDlpInfo.Severity = severity;
        YtDlpInfo.Title = title;
        YtDlpInfo.Message = message;
        YtDlpInfo.IsOpen = true;
    }

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e) =>
        DownloadManager.OpenFolder(SettingsService.Folder);

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Reset all settings?",
            Content = "Every option will return to its default value. Your queue and downloaded files are not affected.",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            Settings.CopyFrom(new AppSettings());
            RefreshControls();
        }
    }
}
