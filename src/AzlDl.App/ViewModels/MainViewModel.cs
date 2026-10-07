using Avalonia;
using AzlDl.App.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AzlDl.App.ViewModels;

public sealed record Section(string Title, ObservableObject Page);

// The window's shell: the list of sections and the one on screen. Only sections that exist are listed.
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(Application app, AppSettings settings)
    {
        Ships = new ShipsViewModel(settings);
        Sync = new SyncViewModel(settings, () => Ships.LoadAsync());
        SettingsPage = new SettingsViewModel(app, settings, async () =>
        {
            Sync.RefreshInstalled();
            await Ships.LoadAsync();
        });
        Sections = [new Section("Ships", Ships), new Section("Sync", Sync), new Section("Settings", SettingsPage)];
        selectedSection = Sections[0];
        Ships.OpenSync = () => SelectedSection = Sections[1];
    }

    public ShipsViewModel Ships { get; }

    public SyncViewModel Sync { get; }

    public SettingsViewModel SettingsPage { get; }

    public IReadOnlyList<Section> Sections { get; }

    [ObservableProperty]
    private Section selectedSection;
}
