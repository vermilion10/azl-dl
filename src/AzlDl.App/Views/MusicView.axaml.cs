using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AzlDl.App.ViewModels;

namespace AzlDl.App.Views;

public partial class MusicView : UserControl
{
    public MusicView() => InitializeComponent();

    private async void OnSaveTrack(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MusicViewModel music || (sender as Control)?.Tag is not TrackItem item
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save track",
            SuggestedFileName = string.Concat(item.Track.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".wav",
            DefaultExtension = "wav",
        });

        if (file?.TryGetLocalPath() is { } path)
        {
            await music.SaveAsync(item.Track, path);
        }
    }
}
