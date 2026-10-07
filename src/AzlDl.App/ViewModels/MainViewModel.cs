using Avalonia;
using AzlDl.App.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AzlDl.App.ViewModels;

public sealed record Section(string Title, ObservableObject Page);

// The window's shell: the list of sections and the one on screen. Only sections that exist are listed.
public sealed partial class MainViewModel : ObservableObject
{
    private bool storyLoaded;

    public MainViewModel(Application app, AppSettings settings)
    {
        Ships = new ShipsViewModel(settings);
        Story = new StoryViewModel(settings);
        Sync = new SyncViewModel(settings, ReloadDataAsync);
        SettingsPage = new SettingsViewModel(app, settings, async () =>
        {
            Sync.RefreshInstalled();
            await ReloadDataAsync();
        });

        var sync = new Section("Sync", Sync);
        Sections = [new Section("Ships", Ships), new Section("Story", Story), sync, new Section("Settings", SettingsPage)];
        selectedSection = Sections[0];
        Ships.OpenSync = () => SelectedSection = sync;
        Story.OpenSync = () => SelectedSection = sync;
    }

    public ShipsViewModel Ships { get; }

    public StoryViewModel Story { get; }

    public SyncViewModel Sync { get; }

    public SettingsViewModel SettingsPage { get; }

    public IReadOnlyList<Section> Sections { get; }

    [ObservableProperty]
    private Section selectedSection;

    // The story archive takes a moment to read, so it loads the first time it's opened.
    partial void OnSelectedSectionChanged(Section value)
    {
        if (value.Page == Story && !storyLoaded)
        {
            storyLoaded = true;
            _ = Story.LoadAsync();
        }
    }

    private async Task ReloadDataAsync()
    {
        await Ships.LoadAsync();
        if (storyLoaded)
        {
            await Story.LoadAsync();
        }
    }
}
