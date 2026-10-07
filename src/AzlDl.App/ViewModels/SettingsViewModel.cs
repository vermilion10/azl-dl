using Avalonia;
using AzlDl.App.Settings;
using AzlDl.App.Theming;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzlDl.App.ViewModels;

public sealed record StyleOption(AppStyle Style, string Name, string Description);

public sealed record ThemeOption(ThemeMode Mode, string Name);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly Application app;
    private readonly AppSettings settings;
    private readonly Func<Task> onDataFolderChanged;

    public SettingsViewModel(Application app, AppSettings settings, Func<Task> onDataFolderChanged)
    {
        this.app = app;
        this.settings = settings;
        this.onDataFolderChanged = onDataFolderChanged;
        selectedStyle = Styles.First(s => s.Style == settings.Style);
        selectedTheme = Themes.First(t => t.Mode == settings.Theme);
        dataFolder = settings.DataFolder;
        ResetNotice = settings.WasReset ? "Your settings file couldn't be read, so the defaults are back in place." : "";
    }

    public IReadOnlyList<StyleOption> Styles { get; } =
    [
        new(AppStyle.ChartRoom, "Chart room", "Stencil names, ruled data rows, chart magenta."),
        new(AppStyle.RegistryBook, "Registry book", "Serif italic names, dotted leaders, sea green."),
        new(AppStyle.PortMenu, "Port menu", "Clipped panels, stat tiles, Super Rare gold."),
    ];

    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new(ThemeMode.System, "Follow system"),
        new(ThemeMode.Light, "Light"),
        new(ThemeMode.Dark, "Dark"),
    ];

    public string ResetNotice { get; }

    [ObservableProperty]
    private StyleOption selectedStyle;

    [ObservableProperty]
    private ThemeOption selectedTheme;

    [ObservableProperty]
    private string dataFolder;

    partial void OnSelectedStyleChanged(StyleOption value) => Apply();

    partial void OnSelectedThemeChanged(ThemeOption value) => Apply();

    private void Apply()
    {
        settings.Style = SelectedStyle.Style;
        settings.Theme = SelectedTheme.Mode;
        StyleManager.Apply(app, settings.Style, settings.Theme);
        settings.Save();
    }

    // Called by the view after the user picks a folder.
    [RelayCommand]
    private async Task UseDataFolderAsync(string folder)
    {
        DataFolder = folder;
        settings.DataFolder = folder;
        settings.Save();
        await onDataFolderChanged();
    }
}
