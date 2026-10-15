using System.Collections.Specialized;
using System.ComponentModel;
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
    private bool musicLoaded;
    private bool asmrLoaded;

    public MainViewModel(Application app, AppSettings settings)
    {
        Ships = new ShipsViewModel(settings);
        Equipment = new EquipmentViewModel(settings);
        Juustagram = new JuustagramViewModel(settings);
        FleetChat = new FleetChatViewModel(settings);
        Player = new PlayerViewModel();
        Music = new MusicViewModel(settings, Player);
        Asmr = new AsmrViewModel(settings, Player);
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
            new Section("Fleet Chat", FleetChat), new Section("Music", Music), new Section("ASMR", Asmr), new Section("Story", Story),
            new Section("Assets", Assets), sync, new Section("Settings", SettingsPage)];
        selectedSection = Sections[0];
        Ships.OpenSync = () => SelectedSection = sync;
        Story.OpenSync = () => SelectedSection = sync;
        Equipment.OpenSync = () => SelectedSection = sync;
        Juustagram.OpenSync = () => SelectedSection = sync;
        FleetChat.OpenSync = () => SelectedSection = sync;
        Assets.OpenSync = () => SelectedSection = sync;
        Music.OpenSync = () => SelectedSection = sync;
        Asmr.OpenSync = () => SelectedSection = sync;

        foreach (var page in new ObservableObject[] { Ships, Equipment, Juustagram, FleetChat, Music, Asmr, Story, Assets, Sync })
        {
            page.PropertyChanged += OnPageChanged;
        }

        Sync.Categories.CollectionChanged += (_, e) =>
        {
            foreach (var row in e.NewItems?.OfType<CategoryRow>() ?? [])
            {
                row.PropertyChanged += OnPageChanged;
            }

            UpdateActivity();
        };
    }

    // What the app is working on, for the top bar: work started on one page (reading data, downloading card images, a
    // Sync download) stays visible after switching to another.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private string activity = "";

    public bool IsBusy => Activity.Length > 0;

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e) => UpdateActivity();

    private void UpdateActivity()
    {
        var work = new List<string>();
        void Loading(bool opened, bool loading, string message)
        {
            if (opened && loading)
            {
                work.Add(message.TrimEnd('…', '.'));
            }
        }

        Loading(true, Ships.IsLoading, Ships.Message);
        Loading(equipmentLoaded, Equipment.IsLoading, Equipment.Message);
        Loading(juustagramLoaded, Juustagram.IsLoading, Juustagram.Message);
        Loading(fleetChatLoaded, FleetChat.IsLoading, FleetChat.Message);
        Loading(storyLoaded, Story.IsLoading, Story.Message);
        Loading(musicLoaded, Music.IsLoading, Music.Message);
        Loading(asmrLoaded, Asmr.IsLoading, Asmr.Message);
        Loading(musicLoaded, Music.Status.EndsWith('…'), Music.Status);
        Loading(asmrLoaded, Asmr.Busy, Asmr.Status);
        Loading(assetsLoaded, Assets.IsLoading, Assets.Message);
        Loading(true, Ships.ImagesBusy, Ships.ImageMessage);
        Loading(true, Equipment.ImagesBusy, Equipment.ImageMessage);
        Loading(true, Sync.Updating, Sync.Status.Length > 0 ? Sync.Status : "Updating game data");
        foreach (var row in Sync.Categories.Where(r => r.Busy))
        {
            work.Add($"Downloading {row.Name} files, {row.Progress:0}%");
        }

        Activity = work.Count switch
        {
            0 => "",
            1 => $"{work[0]}…",
            _ => $"{work[0]}… and {work.Count - 1} more",
        };
    }

    public ShipsViewModel Ships { get; }

    public EquipmentViewModel Equipment { get; }

    public JuustagramViewModel Juustagram { get; }

    public FleetChatViewModel FleetChat { get; }

    public PlayerViewModel Player { get; }

    public MusicViewModel Music { get; }

    public AsmrViewModel Asmr { get; }

    public StoryViewModel Story { get; }

    public AssetsViewModel Assets { get; }

    public SyncViewModel Sync { get; }

    public SettingsViewModel SettingsPage { get; }

    public IReadOnlyList<Section> Sections { get; }

    [ObservableProperty]
    private Section selectedSection;

    // Pages other than Ships take a moment to read, so they load the first time they're opened. The asset list is read
    // again on every visit, since ship pages and Sync add bundles in the meantime.
    partial void OnSelectedSectionChanged(Section value)
    {
        LoadSection(value);
        UpdateActivity();
    }

    private void LoadSection(Section value)
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
        else if (value.Page == Music && !musicLoaded)
        {
            musicLoaded = true;
            _ = Music.LoadAsync();
        }
        else if (value.Page == Asmr && !asmrLoaded)
        {
            asmrLoaded = true;
            _ = Asmr.LoadAsync();
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

        if (musicLoaded)
        {
            await Music.LoadAsync();
        }

        if (asmrLoaded)
        {
            await Asmr.LoadAsync();
        }

        if (assetsLoaded)
        {
            await Assets.LoadAsync();
        }
    }
}
