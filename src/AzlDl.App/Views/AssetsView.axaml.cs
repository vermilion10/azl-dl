using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AzlDl.App.ViewModels;

namespace AzlDl.App.Views;

public partial class AssetsView : UserControl
{
    public AssetsView() => InitializeComponent();

    private async void OnSaveAsset(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AssetsViewModel assets || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save object",
            SuggestedFileName = assets.SuggestedFileName,
        });

        if (file?.TryGetLocalPath() is { } path)
        {
            await assets.SaveAssetCommand.ExecuteAsync(path);
        }
    }

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AssetsViewModel { ResultFolder: { } folder } && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
        }
    }
}
