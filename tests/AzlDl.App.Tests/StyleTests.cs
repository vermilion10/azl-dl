using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AzlDl.App.Settings;
using AzlDl.App.Theming;
using AzlDl.App.ViewModels;
using AzlDl.App.Views;
using Xunit;

namespace AzlDl.App.Tests;

public class StyleTests
{
    public static TheoryData<AppStyle> AllStyles => new(Enum.GetValues<AppStyle>());

    // Every style defines every token, brushes in both variants. A missing key would silently fall back to
    // another style's value or to nothing.
    [AvaloniaTheory]
    [MemberData(nameof(AllStyles))]
    public void Style_defines_every_token(AppStyle style)
    {
        var dictionary = StyleManager.Load(style);
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            foreach (var key in StyleTokens.Brushes)
            {
                Assert.True(dictionary.TryGetResource(key, variant, out var value) && value is not null,
                    $"{style} has no {key} for {variant}");
            }
        }

        foreach (var key in StyleTokens.Shared)
        {
            Assert.True(dictionary.TryGetResource(key, ThemeVariant.Default, out var value) && value is not null,
                $"{style} has no {key}");
        }
    }

    // Renders the ship page in every style and theme to tests/output for review. Needs decoded game data; point
    // AZLDL_DATA at a folder written by the command-line tool.
    [AvaloniaTheory]
    [InlineData(AppStyle.ChartRoom, ThemeMode.Light)]
    [InlineData(AppStyle.ChartRoom, ThemeMode.Dark)]
    [InlineData(AppStyle.RegistryBook, ThemeMode.Light)]
    [InlineData(AppStyle.RegistryBook, ThemeMode.Dark)]
    [InlineData(AppStyle.PortMenu, ThemeMode.Light)]
    [InlineData(AppStyle.PortMenu, ThemeMode.Dark)]
    public async Task Ship_page_renders(AppStyle style, ThemeMode theme)
    {
        var dataFolder = Environment.GetEnvironmentVariable("AZLDL_DATA");
        Assert.SkipWhen(string.IsNullOrEmpty(dataFolder), "Set AZLDL_DATA to a data folder to render snapshots.");

        var app = Application.Current!;
        var settings = new AppSettings { Style = style, Theme = theme, DataFolder = dataFolder! };
        StyleManager.Apply(app, style, theme);

        var main = new MainViewModel(app, settings);
        var window = new MainWindow { DataContext = main, Width = 1360, Height = 880 };
        window.Show();
        await main.Ships.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.True(main.Ships.IsReady, main.Ships.Message);
        Assert.NotNull(main.Ships.SelectedShip);
        Assert.NotNull(main.Ships.SelectedSkin);
        var skinList = window.GetVisualDescendants().OfType<ListBox>()
            .Single(l => Avalonia.Automation.AutomationProperties.GetName(l) == "Skins");
        Assert.Same(main.Ships.SelectedSkin, skinList.SelectedItem);

        // Show a painting when one has been extracted already (Enterprise's qiye_5 in the reference data set).
        if (main.Ships.SelectedShip!.Skins.FirstOrDefault(s => s.Painting == "qiye_5") is { } skin)
        {
            main.Ships.SelectedSkin = skin;
            Dispatcher.UIThread.RunJobs();
        }

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var output = Path.Combine(OutputFolder(), $"ships-{style}-{theme}.png");
#pragma warning disable CS0618 // The replacement overload needs a concrete encoder options type this version doesn't expose.
        frame!.Save(output);
#pragma warning restore CS0618
        window.Close();
    }

    // Picking a style in Settings swaps the app's resources at once, and the Settings page shows all three styles.
    [AvaloniaFact]
    public void Settings_switches_style_live()
    {
        var app = Application.Current!;
        var settings = new AppSettings { Style = AppStyle.ChartRoom, Theme = ThemeMode.Light, DataFolder = Path.GetTempPath() };
        StyleManager.Apply(app, settings.Style, settings.Theme);
        var main = new MainViewModel(app, settings);
        var window = new MainWindow { DataContext = main, Width = 1360, Height = 880 };
        window.Show();
        main.SelectedSection = main.Sections.Single(s => s.Page is SettingsViewModel);
        Dispatcher.UIThread.RunJobs();

        string Background() =>
            window.TryFindResource(StyleTokens.BgBrush, window.ActualThemeVariant, out var brush)
                ? ((Avalonia.Media.ISolidColorBrush)brush!).Color.ToString()
                : "missing";

        Assert.Equal("#ffeef2f4", Background());
        main.SettingsPage.SelectedStyle = main.SettingsPage.Styles.Single(s => s.Style == AppStyle.PortMenu);
        main.SettingsPage.SelectedTheme = main.SettingsPage.Themes.Single(t => t.Mode == ThemeMode.Dark);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("#ff0b1220", Background());

        var frame = window.CaptureRenderedFrame();
#pragma warning disable CS0618
        frame!.Save(Path.Combine(OutputFolder(), "settings-PortMenu-Dark.png"));
#pragma warning restore CS0618
        window.Close();
    }

    // With no game data, Ships explains why and its button really opens Sync.
    [AvaloniaFact]
    public async Task Empty_data_folder_points_to_sync()
    {
        var app = Application.Current!;
        var empty = Directory.CreateTempSubdirectory("azl-dl-empty").FullName;
        var settings = new AppSettings { Style = AppStyle.RegistryBook, Theme = ThemeMode.Light, DataFolder = empty };
        StyleManager.Apply(app, settings.Style, settings.Theme);
        var main = new MainViewModel(app, settings);
        var window = new MainWindow { DataContext = main, Width = 1360, Height = 880 };
        window.Show();
        await main.Ships.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.True(main.Ships.HasProblem);
        Assert.Contains(empty, main.Ships.Message);
        SaveFrame(window, "ships-empty-RegistryBook-Light.png");

        main.Ships.GoToSyncCommand.Execute(null);
        Assert.IsType<SyncViewModel>(main.SelectedSection.Page);
        window.Close();
    }

    // Sync lists every category the server reports, with sizes. Needs network access to the game server.
    [AvaloniaFact]
    public async Task Sync_lists_server_categories()
    {
        var dataFolder = Environment.GetEnvironmentVariable("AZLDL_DATA");
        Assert.SkipWhen(string.IsNullOrEmpty(dataFolder), "Set AZLDL_DATA to a data folder to render snapshots.");

        var app = Application.Current!;
        var settings = new AppSettings { Style = AppStyle.ChartRoom, Theme = ThemeMode.Dark, DataFolder = dataFolder! };
        StyleManager.Apply(app, settings.Style, settings.Theme);
        var main = new MainViewModel(app, settings);
        var window = new MainWindow { DataContext = main, Width = 1360, Height = 880 };
        window.Show();
        main.SelectedSection = main.Sections.Single(s => s.Page is SyncViewModel);
        await main.Sync.CheckCategoriesCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(10, main.Sync.Categories.Count);
        SaveFrame(window, "sync-ChartRoom-Dark.png");

        // A full-category download needs a second, explicit confirmation; cancelling leaves nothing running.
        var row = main.Sync.Categories.First(c => c.CanDownload);
        row.AskDownloadCommand.Execute(null);
        Assert.True(row.Confirming);
        Assert.False(row.CanDownload);
        Assert.Contains(row.Name, row.ConfirmText);
        row.CancelDownloadCommand.Execute(null);
        Assert.False(row.Confirming);
        Assert.False(row.Busy);
        window.Close();
    }

    // The reader shows a known story with its resolved speakers and choices.
    [AvaloniaFact]
    public async Task Story_reader_shows_a_story()
    {
        var dataFolder = Environment.GetEnvironmentVariable("AZLDL_DATA");
        Assert.SkipWhen(string.IsNullOrEmpty(dataFolder), "Set AZLDL_DATA to a data folder to render snapshots.");

        var app = Application.Current!;
        var settings = new AppSettings { Style = AppStyle.RegistryBook, Theme = ThemeMode.Light, DataFolder = dataFolder! };
        StyleManager.Apply(app, settings.Style, settings.Theme);
        var main = new MainViewModel(app, settings);
        var window = new MainWindow { DataContext = main, Width = 1360, Height = 880 };
        window.Show();
        main.SelectedSection = main.Sections.Single(s => s.Page is StoryViewModel);
        await main.Story.LoadAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(main.Story.IsReady, main.Story.Message);

        // "Get It While It's Hot" (HAIDAORICHANG15, chapter Uncharted Summer): 45 lines of dialogue and one choice.
        main.Story.SelectedCategory = "Special";
        main.Story.Filter = "Get It While It's Hot";
        Dispatcher.UIThread.RunJobs();
        main.Story.SelectedEntry = main.Story.SelectedGroup!.Entries.Single(e => e.StoryId == "haidaorichang15");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(45, main.Story.Lines.Count(l => l.IsDialogue));
        Assert.Single(main.Story.Lines, l => l.IsChoice);
        Assert.Contains(main.Story.Lines, l => l.Line.Speaker == "Eugen");
        Assert.DoesNotContain(main.Story.Lines, l => l.Line.Text.Contains("{namecode", StringComparison.Ordinal));
        SaveFrame(window, "story-RegistryBook-Light.png");
        window.Close();
    }

    private static void SaveFrame(Window window, string name)
    {
#pragma warning disable CS0618 // The replacement overload needs an encoder options type this version doesn't expose.
        window.CaptureRenderedFrame()!.Save(Path.Combine(OutputFolder(), name));
#pragma warning restore CS0618
    }

    private static string OutputFolder()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "snapshots");
        Directory.CreateDirectory(folder);
        return folder;
    }
}
