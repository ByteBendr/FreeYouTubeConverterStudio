using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FreeYouTubeConverterStudio.Services;

/// <summary>File / folder pickers wired to the main window (required for unpackaged WinUI 3).</summary>
public static class Pickers
{
    public static async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public static async Task<string?> PickFileAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker();
        foreach (var e in extensions) picker.FileTypeFilter.Add(e);
        if (extensions.Length == 0) picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
