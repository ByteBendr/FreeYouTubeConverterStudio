using System.ComponentModel;
using System.Runtime.InteropServices;
using FreeYouTubeConverterStudio.Models;
using FreeYouTubeConverterStudio.Pages;
using FreeYouTubeConverterStudio.Services;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using WinRT.Interop;

namespace FreeYouTubeConverterStudio;

public sealed partial class MainWindow : Window
{
    private readonly AppSettings _s = AppState.Settings;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public MainWindow()
    {
        InitializeComponent();

        Title = "Free YouTube Converter Studio";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppIcon.Apply(AppWindow);
        SizeAndCenter(1180, 800);
        ApplyTheme();
        ApplyBackdrop();
        ApplyAlwaysOnTop();

        _s.PropertyChanged += OnSettingsChanged;

        if (Content is FrameworkElement rootElement)
        {
            rootElement.Loaded += async (_, _) =>
            {
                try { await DependencyFlow.StartupAsync(rootElement.XamlRoot); }
                catch (Exception ex) { AppLog.Info("Startup dependency check failed: " + ex); }
            };
        }
        AppState.Manager.ActivityChanged += UpdateBadge;
        Closed += (_, _) => AppState.Shutdown();
    }


    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        NavView.SelectedItem = NavDownload;
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
            Navigate(typeof(SettingsPage));
        else if (args.SelectedItemContainer?.Tag is "download")
            Navigate(typeof(DownloadPage));
    }

    private void Navigate(Type page)
    {
        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
    }

    private void UpdateBadge()
    {
        var active = AppState.Manager.Queue.Count(i => i.Status is DownloadStatus.Queued or DownloadStatus.Downloading);
        QueueBadge.Value = active;
        QueueBadge.Visibility = active > 0 ? Visibility.Visible : Visibility.Collapsed;
    }



    private void SizeAndCenter(int width, int height)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;

        var w = Math.Min((int)(width * scale), area.Width);
        var h = Math.Min((int)(height * scale), area.Height);

        AppWindow.MoveAndResize(new RectInt32(
            area.X + (area.Width - w) / 2,
            area.Y + (area.Height - h) / 2,
            w, h));
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.Theme): ApplyTheme(); break;
            case nameof(AppSettings.Backdrop): ApplyBackdrop(); break;
            case nameof(AppSettings.AlwaysOnTop): ApplyAlwaysOnTop(); break;
            case null: ApplyTheme(); ApplyBackdrop(); ApplyAlwaysOnTop(); break;
        }
    }

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = _s.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        if (!AppWindowTitleBar.IsCustomizationSupported()) return;

        var tb = AppWindow.TitleBar;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;

        switch (_s.Theme)
        {
            case "Light":
                tb.ButtonForegroundColor = Colors.Black;
                tb.ButtonHoverForegroundColor = Colors.Black;
                tb.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(25, 0, 0, 0);
                break;
            case "Dark":
                tb.ButtonForegroundColor = Colors.White;
                tb.ButtonHoverForegroundColor = Colors.White;
                tb.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(25, 255, 255, 255);
                break;
            default:
                tb.ButtonForegroundColor = null;
                tb.ButtonHoverForegroundColor = null;
                tb.ButtonHoverBackgroundColor = null;
                break;
        }
    }

    private void ApplyBackdrop()
    {
        SystemBackdrop = _s.Backdrop switch
        {
            "Mica" => new MicaBackdrop { Kind = MicaKind.Base },
            "Acrylic" => new DesktopAcrylicBackdrop(),
            _ => new MicaBackdrop { Kind = MicaKind.BaseAlt }  
        };
    }

    private void ApplyAlwaysOnTop()
    {
        if (AppWindow.Presenter is OverlappedPresenter p)
            p.IsAlwaysOnTop = _s.AlwaysOnTop;
    }
}
