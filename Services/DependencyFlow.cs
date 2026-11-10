using FreeYouTubeConverterStudio.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FreeYouTubeConverterStudio.Services;

public sealed record DependencyResult(bool AllFound, List<DependencyStatus> Statuses);

/// <summary>The user-facing side of dependency checking: dialogs that offer, run and report installs.</summary>
public static class DependencyFlow
{
    private static bool _ffmpegDeclinedThisSession;

    /// <summary>
    /// Checks all components, records the result in hasDepends.bin, and — if something required is
    /// missing — asks whether to download it. The user can decline and install things themselves.
    /// </summary>
    public static async Task<DependencyResult> RunAsync(XamlRoot root)
    {
        var statuses = DependencyService.Check(AppState.Settings);
        var ok = DependencyService.AllRequiredFound(statuses);
        SaveState(ok);
        AppLog.Info($"Dependency check: {(ok ? "all found" : "missing: " + string.Join(", ", statuses.Where(s => s.Required && !s.Found).Select(s => s.Name)))}");

        if (ok) return new DependencyResult(true, statuses);

        var missing = statuses.Where(s => s.Required && !s.Found).ToList();

        var offer = Themed(new ContentDialog
        {
            XamlRoot = root,
            Title = "Some required components are missing",
            Content = BuildMissingList(missing),
            PrimaryButtonText = "Download and install",
            CloseButtonText = "Not now — I'll install them myself",
            DefaultButton = ContentDialogButton.Primary
        }, root);

        if (await ShowSafeAsync(offer) != ContentDialogResult.Primary)
            return new DependencyResult(false, statuses);

        var errors = await InstallAsync(root, missing);

        statuses = DependencyService.Check(AppState.Settings);
        ok = DependencyService.AllRequiredFound(statuses);
        SaveState(ok);

        await ShowSafeAsync(Themed(new ContentDialog
        {
            XamlRoot = root,
            Title = ok ? "All set" : "Some components could not be installed",
            Content = ok
                ? "All required components are now installed."
                : string.Join("\n\n", errors.DefaultIfEmpty("Something went wrong.")) +
                  "\n\nYou can install them yourself and press “Check dependencies” again.",
            CloseButtonText = "OK"
        }, root));

        return new DependencyResult(ok, statuses);
    }

    /// <summary>Runs at launch: only checks again if there is no valid "all found" record.</summary>
    public static async Task StartupAsync(XamlRoot root)
    {
        if (!AppState.Settings.CheckDependenciesAtStartup) return;

        var state = DependencyStateFile.Read();
        if (state is { AllFound: true }) return;   // valid record says everything was found

        await Task.Delay(500);   // let the window finish appearing
        await RunAsync(root);
    }


    /// <summary>Returns false if the user cancelled the whole operation.</summary>
    public static async Task<bool> EnsureFFmpegAsync(XamlRoot root)
    {
        if (_ffmpegDeclinedThisSession || FFmpegService.Resolve(AppState.Settings) != null) return true;

        var dialog = Themed(new ContentDialog
        {
            XamlRoot = root,
            Title = "FFmpeg was not found",
            Content = "FFmpeg is needed for video at 720p and above, MP3 conversion and embedded subtitles. " +
                      "Download it now (about 80 MB)?",
            PrimaryButtonText = "Download FFmpeg",
            SecondaryButtonText = "Continue without it",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        }, root);

        switch (await ShowSafeAsync(dialog))
        {
            case ContentDialogResult.Primary:
                var ffmpeg = DependencyService.Check(AppState.Settings).First(d => d.Kind == DependencyKind.FFmpeg);
                var errors = await InstallAsync(root, new List<DependencyStatus> { ffmpeg });
                DependencyService.RefreshStateFile();

                if (errors.Count > 0)
                {
                    await ShowSafeAsync(Themed(new ContentDialog
                    {
                        XamlRoot = root,
                        Title = "FFmpeg could not be downloaded",
                        Content = string.Join("\n\n", errors) + "\n\nThe queue will continue without it.",
                        CloseButtonText = "OK"
                    }, root));
                }
                return true;

            case ContentDialogResult.Secondary:
                _ffmpegDeclinedThisSession = true;
                return true;

            default:
                return false;
        }
    }


    private static async Task<List<string>> InstallAsync(XamlRoot root, List<DependencyStatus> items)
    {
        var errors = new List<string>();
        using var cts = new CancellationTokenSource();

        var label = new TextBlock { Text = "Preparing…", TextWrapping = TextWrapping.Wrap };
        var bar = new ProgressBar { IsIndeterminate = true, Minimum = 0, Maximum = 100 };

        var dialog = Themed(new ContentDialog
        {
            XamlRoot = root,
            Title = "Installing components",
            Content = new StackPanel { Spacing = 14, MinWidth = 380, Children = { label, bar } },
            CloseButtonText = "Cancel"
        }, root);
        dialog.CloseButtonClick += (_, _) => cts.Cancel();

        var shown = dialog.ShowAsync();   // intentionally not awaited

        try
        {
            foreach (var item in items)
            {
                label.Text = $"Installing {item.Name} ({item.DownloadSize})…";
                bar.IsIndeterminate = true;

                var progress = new Progress<double>(p =>
                {
                    bar.IsIndeterminate = false;
                    bar.Value = Math.Clamp(p, 0, 1) * 100;
                });

                try
                {
                    var error = await DependencyService.InstallAsync(item, progress, cts.Token);
                    if (error != null) errors.Add($"{item.Name}: {error}");
                }
                catch (OperationCanceledException)
                {
                    errors.Add("Installation was cancelled.");
                    break;
                }
                catch (Exception ex)
                {
                    AppLog.Info($"Install {item.Name} failed: {ex}");
                    errors.Add($"{item.Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            dialog.Hide();
            try { await shown; } catch { /* dialog already closed */ }
        }

        return errors;
    }


    private static void SaveState(bool ok)
    {
        try { DependencyStateFile.Write(ok); }
        catch (Exception ex) { AppLog.Info("Could not write hasDepends.bin: " + ex.Message); }
    }

    private static UIElement BuildMissingList(List<DependencyStatus> missing)
    {
        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };

        panel.Children.Add(new TextBlock
        {
            Text = "These are needed for everything to work. Download and install them now?",
            TextWrapping = TextWrapping.Wrap
        });

        foreach (var m in missing)
        {
            var item = new StackPanel { Spacing = 2 };
            item.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(m.DownloadSize) ? m.Name : $"{m.Name}  ·  {m.DownloadSize}",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            item.Children.Add(new TextBlock
            {
                Text = m.Purpose,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
                FontSize = 12
            });
            panel.Children.Add(item);
        }

        panel.Children.Add(new TextBlock
        {
            Text = "If you choose “Not now”, you can install them yourself and press “Check dependencies” in Settings.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            FontSize = 12
        });

        return panel;
    }

    /// <summary>Makes a code-built dialog follow the app's light/dark choice.</summary>
    private static ContentDialog Themed(ContentDialog dialog, XamlRoot root)
    {
        if (root.Content is FrameworkElement fe) dialog.RequestedTheme = fe.ActualTheme;
        return dialog;
    }

    /// <summary>ShowAsync throws if another dialog is already open; treat that as "dismissed".</summary>
    private static async Task<ContentDialogResult> ShowSafeAsync(ContentDialog dialog)
    {
        try { return await dialog.ShowAsync(); }
        catch (Exception ex)
        {
            AppLog.Info("Dialog could not be shown: " + ex.Message);
            return ContentDialogResult.None;
        }
    }
}
