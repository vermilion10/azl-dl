using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AzlDl.App.ViewModels;

namespace AzlDl.App.Views;

public partial class AsmrView : UserControl
{
    public AsmrView()
    {
        InitializeComponent();

        // Keeps the line being spoken in sight.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is AsmrViewModel asmr)
            {
                asmr.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(AsmrViewModel.CurrentLine) && asmr.CurrentLine is { } line)
                    {
                        SubtitleList.ScrollIntoView(line);
                    }
                };
            }
        };
    }

    private async void OnSaveStory(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AsmrViewModel { SelectedStory: { } item } asmr || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save sound story",
            SuggestedFileName = string.Concat(item.Story.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".wav",
            DefaultExtension = "wav",
        });

        if (file?.TryGetLocalPath() is { } path)
        {
            await asmr.SaveAsync(path);
        }
    }
}
