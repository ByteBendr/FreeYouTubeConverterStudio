using System.Collections.ObjectModel;
using FreeYouTubeConverterStudio.Models;
using FreeYouTubeConverterStudio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace FreeYouTubeConverterStudio.Pages;

public sealed partial class DownloadPage : Page
{
    private static DownloadManager Manager => AppState.Manager;
    private static AppSettings Settings => AppState.Settings;

    public ObservableCollection<DownloadItem> Queue => Manager.Queue;

    private bool _syncing;
    private bool _activationHooked;

    public DownloadPage()
    {
        InitializeComponent();

        CmbFormat.ItemsSource = Catalog.FormatOptions;
        ApplyDefaults();

        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.DefaultFormat) or nameof(AppSettings.DefaultQuality))
                ApplyDefaults();
        };

        Manager.ActivityChanged += UpdateSummary;
        Manager.QueueFinished += OnQueueFinished;
        Queue.CollectionChanged += (_, _) => { UpdateEmpty(); UpdateSummary(); };

        UpdateEmpty();
        UpdateSummary();

        Loaded += async (_, _) =>
        {
            if (!_activationHooked)
            {
                _activationHooked = true;
                App.MainWindow.Activated += async (_, a) =>
                {
                    if (a.WindowActivationState != WindowActivationState.Deactivated && Settings.AutoPasteClipboard)
                        await TryPasteClipboardAsync(force: false);
                };
            }

            if (Settings.AutoPasteClipboard)
                await TryPasteClipboardAsync(force: false);
        };
    }

    // ------------------------------------------------------------------
    //  Format / quality pickers
    // ------------------------------------------------------------------

    private void ApplyDefaults()
    {
        _syncing = true;
        CmbFormat.SelectedItem = Settings.DefaultFormat;
        FillQualities(Settings.DefaultQuality);
        _syncing = false;
    }

    private void FillQualities(string? preferred = null)
    {
        var options = Catalog.QualitiesFor(CmbFormat.SelectedItem as string);
        var previous = preferred ?? CmbQuality.SelectedItem as string;

        CmbQuality.ItemsSource = options;
        CmbQuality.SelectedItem = previous != null && options.Contains(previous) ? previous : options[0];
    }

    private void CmbFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing) FillQualities();
    }

    // ------------------------------------------------------------------
    //  Adding
    // ------------------------------------------------------------------

    private void UrlBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            Add_Click(sender, e);
        }
    }

    private async void Paste_Click(object sender, RoutedEventArgs e) => await TryPasteClipboardAsync(force: true);

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (url.Length == 0)
        {
            ShowInfo(InfoBarSeverity.Informational, "Nothing to add", "Please enter a video or playlist URL.");
            return;
        }

        var format = CmbFormat.SelectedItem as string ?? Catalog.FormatOptions[0];
        var quality = CmbQuality.SelectedItem as string ?? "Best";

        SetBusy(true);
        ShowInfo(InfoBarSeverity.Informational, "Resolving link…", "");

        try
        {
            var count = await Manager.AddFromUrlAsync(url, format, quality,
                msg => ShowInfo(InfoBarSeverity.Informational, msg, ""));

            UrlBox.Text = "";
            ShowInfo(InfoBarSeverity.Success, "Added to queue", count == 1 ? "1 item added." : $"{count} items added.");

            if (Settings.AutoStartQueue && !Manager.IsRunning)
                Start_Click(sender, e);
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, "Could not resolve the URL", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        AddButton.IsEnabled = !busy;
        AddRing.IsActive = busy;
        AddRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task TryPasteClipboardAsync(bool force)
    {
        try
        {
            var view = Clipboard.GetContent();
            if (!view.Contains(StandardDataFormats.Text)) return;

            var text = (await view.GetTextAsync())?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            var looksLikeYouTube = text.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                                   text.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);

            if (looksLikeYouTube && (force || string.IsNullOrWhiteSpace(UrlBox.Text)))
                UrlBox.Text = text;
        }
        catch
        {
            // Clipboard access can fail when the window isn't focused.
        }
    }

    // ------------------------------------------------------------------
    //  Drag & drop
    // ------------------------------------------------------------------

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        var v = e.DataView;
        if (v.Contains(StandardDataFormats.Text) || v.Contains(StandardDataFormats.WebLink))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add link";
        }
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        try
        {
            string? text = null;
            if (e.DataView.Contains(StandardDataFormats.WebLink))
                text = (await e.DataView.GetWebLinkAsync())?.ToString();
            else if (e.DataView.Contains(StandardDataFormats.Text))
                text = await e.DataView.GetTextAsync();

            if (!string.IsNullOrWhiteSpace(text))
                UrlBox.Text = text.Trim();
        }
        catch { /* ignore */ }
    }

    // ------------------------------------------------------------------
    //  Queue actions
    // ------------------------------------------------------------------

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        AppLog.Info("Start queue clicked");
        if (Manager.IsRunning) return;

        if (!Queue.Any(i => i.Status is DownloadStatus.Queued or DownloadStatus.Failed))
        {
            ShowInfo(InfoBarSeverity.Informational, "Nothing to start", "There are no queued downloads.");
            return;
        }

        if (!await DependencyFlow.EnsureFFmpegAsync(XamlRoot)) return;

        try
        {
            await Manager.RunQueueAsync();
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, "Could not start the queue", ex.Message);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => Manager.CancelAll();

    private void Clear_Click(object sender, RoutedEventArgs e) => Manager.ClearFinished();

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => DownloadManager.OpenFolder(Settings.OutputFolder);

    private void ItemCancel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadItem item) Manager.Cancel(item);
    }

    private void ItemRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadItem item) Manager.Remove(item);
    }

    private void OnQueueFinished(int completed, int failed)
    {
        var severity = failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        ShowInfo(severity, "Queue finished", $"{completed} completed, {failed} failed.");
    }

    // ------------------------------------------------------------------
    //  UI helpers
    // ------------------------------------------------------------------

    private void UpdateEmpty()
    {
        EmptyState.Visibility = Queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSummary()
    {
        var total = Queue.Count;
        var active = Queue.Count(i => i.Status == DownloadStatus.Downloading);
        var done = Queue.Count(i => i.Status == DownloadStatus.Completed);
        var pending = Queue.Any(i => i.Status is DownloadStatus.Queued or DownloadStatus.Failed);
        var finished = Queue.Any(i => i.Status is DownloadStatus.Completed or DownloadStatus.Canceled);

        SummaryText.Text = total == 0 ? "" : $"{total} item{(total == 1 ? "" : "s")}  ·  {active} active  ·  {done} done";
        StartButton.IsEnabled = !Manager.IsRunning && pending;
        StopButton.IsEnabled = Manager.IsRunning;
        ClearButton.IsEnabled = finished;
    }

    private void ShowInfo(InfoBarSeverity severity, string title, string message)
    {
        Info.Severity = severity;
        Info.Title = title;
        Info.Message = message;
        Info.IsOpen = true;
    }
}
