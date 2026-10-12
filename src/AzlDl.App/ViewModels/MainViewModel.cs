using Avalonia;
using AzlDl.App.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AzlDl.App.ViewModels;

public sealed record Section(string Title, ObservableObject Page);

// The window's shell: the list of sections and the one on screen. Only sections that exist are listed.
public sealed partial class MainViewModel : ObservableObject
{
    private bool storyLoaded;
    private bool equipmentLoaded;
    private bool juustagramLoaded;
    private bool fleetChatLoaded;
    private bool assetsLoaded;

    public MainViewModel(Application app, AppSettings settings)
    {
        Ships = new ShipsViewModel(settings);
        Equipment = new EquipmentViewModel(settings);
        Juustagram = new JuustagramViewModel(settings);
        FleetChat = new FleetChatViewModel(settings);
        Story = new StoryViewModel(settings);
        Assets = new AssetsViewModel(settings);
        Sync = new SyncViewModel(settings, ReloadDataAsync);
        SettingsPage = new SettingsViewModel(app, settings, async () =>
        {
            Sync.RefreshInstalled();
            await ReloadDataAsync();
        });

        var sync = new Section("Sync", Sync);
        Sections = [new Section("Ships", Ships), new Section("Equipment", Equipment), new Section("Juustagram", Juustagram),
            new Section("Fleet Chat", FleetChat), new Section("Story", Story), new Section("Assets", Assets), sync, new Section("Settings", SettingsPage)];
        selectedSection = Sections[0];
        Ships.OpenSync = () => SelectedSection = sync;
        Story.OpenSync = () => SelectedSection = sync;
        Equipment.OpenSync = () => SelectedSection = sync;
        Juustagram.OpenSync = () => SelectedSection = sync;
        FleetChat.OpenSync = () => SelectedSection = sync;
        Assets.OpenSync = () => SelectedSection = sync;
    }

    public ShipsViewModel Ships { get; }

    public EquipmentViewModel Equipment { get; }

    public JuustagramViewModel Juustagram { get; }

    public FleetChatViewModel FleetChat { get; }

    public StoryViewModel Story { get; }

    public AssetsViewModel Assets { get; }

    public SyncViewModel Sync { get; }

    public SettingsViewModel SettingsPage { get; }

    public IReadOnlyList<Section> Sections { get; }

    [ObservableProperty]
    private Section selectedSection;

    // Equipment, Juustagram, Fleet Chat and the story archive take a moment to read, so they load the first time they're
    // opened. The asset list is read again
    // on every visit, since ship pages and Sync add bundles in the meantime.
    partial void OnSelectedSectionChanged(Section value)
    {
        if (value.Page == Equipment && !equipmentLoaded)
        {
            equipmentLoaded = true;
            _ = Equipment.LoadAsync();
        }
        else if (value.Page == Juustagram && !juustagramLoaded)
        {
            juustagramLoaded = true;
            _ = Juustagram.LoadAsync();
        }
        else if (value.Page == FleetChat && !fleetChatLoaded)
        {
            fleetChatLoaded = true;
            _ = FleetChat.LoadAsync();
        }
        else if (value.Page == Story && !storyLoaded)
        {
            storyLoaded = true;
            _ = Story.LoadAsync();
        }
        else if (value.Page == Assets)
        {
            assetsLoaded = true;
            _ = Assets.LoadAsync();
        }
    }

    private async Task ReloadDataAsync()
    {
        await Ships.LoadAsync();
        if (equipmentLoaded)
        {
            await Equipment.LoadAsync();
        }

        if (juustagramLoaded)
        {
            await Juustagram.LoadAsync();
        }

        if (fleetChatLoaded)
        {
            await FleetChat.LoadAsync();
        }

        if (storyLoaded)
        {
            await Story.LoadAsync();
        }

        if (assetsLoaded)
        {
            await Assets.LoadAsync();
        }
    }
}
