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
using AzlDl.Core.Data;
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
        Assert.True(main.Ships.IsGallery);
        Assert.NotEmpty(main.Ships.CardRows);
        // Only the rows on screen are built; realizing all ~900 cards froze the window for seconds.
        Assert.InRange(window.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("card")), 1, 120);
        await WaitUntil(() => main.Ships.CardRows.SelectMany(r => r.Cards).Where(c => c.HasImage).Take(3).All(c => c.Image is not null));
        var gallery = window.CaptureRenderedFrame();
#pragma warning disable CS0618
        gallery!.Save(Path.Combine(OutputFolder(), $"gallery-{style}-{theme}.png"));
#pragma warning restore CS0618
        if (style == AppStyle.ChartRoom && theme == ThemeMode.Light)
        {
            // Sorted by number, the first rows mix every rarity.
            main.Ships.SelectedSort = main.Ships.SortOptions.Single(o => o.Title == "Ship number");
            await WaitUntil(() => main.Ships.CardRows.Take(4).SelectMany(r => r.Cards).Where(c => c.HasImage).All(c => c.Image is not null));
            SaveFrame(window, "gallery-by-number.png");
            main.Ships.ClearFiltersCommand.Execute(null);
            main.Ships.FilterGroups.Single(g => g.Title == "Collection").Chips.Where(c => c.Label is "Research" or "META").ToList()
                .ForEach(c => c.IsChecked = true);
            await WaitUntil(() => main.Ships.CardRows.Take(4).SelectMany(r => r.Cards).Where(c => c.HasImage).All(c => c.Image is not null));
            SaveFrame(window, "gallery-research-meta.png");
            window.Height = 2400;
            Dispatcher.UIThread.RunJobs();
            SaveFrame(window, "gallery-tall.png");
            window.Height = 880;
            Dispatcher.UIThread.RunJobs();
            main.Ships.ClearFiltersCommand.Execute(null);
            main.Ships.SelectedSort = main.Ships.SortOptions[0];
        }

        main.Ships.OpenShipCommand.Execute(main.Ships.SelectedShip);
        Dispatcher.UIThread.RunJobs();
        Assert.True(main.Ships.IsDetail);
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

        if (main.Ships.SelectedShip.Name == "Enterprise")
        {
            Assert.Contains(main.Ships.SelectedShip.Skills, s => s.Name == "Lucky E" && s.Description.Contains("40.0% → 70.0%"));
            Assert.Contains(main.Ships.VoiceLines, v => v.Label == "Log in");
            Assert.Contains(main.Ships.VoiceLines, v => v.Label == "Main screen 1");
        }

        if (style == AppStyle.ChartRoom && theme == ThemeMode.Light)
        {
            window.Height = 2800;
            Dispatcher.UIThread.RunJobs();
            SaveFrame(window, "ships-tall-ChartRoom-Light.png");
            window.Height = 880;
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

    // The gallery offers only filter values some ship has, combines groups with AND and chips in a group with OR, and
    // frames each card by its rarity tier.
    [AvaloniaFact]
    public async Task Ship_gallery_filters_by_rarity_hull_faction_and_collection()
    {
        var dataFolder = Environment.GetEnvironmentVariable("AZLDL_DATA");
        Assert.SkipWhen(string.IsNullOrEmpty(dataFolder), "Set AZLDL_DATA to a data folder.");

        var ships = new ShipsViewModel(new AppSettings { DataFolder = dataFolder! });
        await ships.LoadAsync();
        var total = ships.Ships.Count;
        Assert.Equal(["Rarity", "Collection", "Fleet", "Hull type", "Faction"], ships.FilterGroups.Select(g => g.Title));
        Assert.All(ships.FilterGroups, g => Assert.True(g.ShowsAll));

        FilterChip Chip(string group, string label) => ships.FilterGroups.Single(g => g.Title == group).Chips.Single(c => c.Label == label);
        Assert.Equal("SSR", Chip("Rarity", "Super Rare").Code);
        Assert.Equal("DD", Chip("Hull type", "Destroyer").Code);
        Assert.Contains(ships.FilterGroups.Single(g => g.Title == "Faction").Chips, c => c.Label == "Eagle Union");
        Assert.DoesNotContain(ships.FilterGroups.Single(g => g.Title == "Faction").Chips, c => c.Label.Any(ch => ch > 0x2E80));

        Chip("Collection", "Research").IsChecked = true;
        Assert.InRange(ships.Ships.Count, 1, total - 1);
        Assert.All(ships.Ships, s => Assert.Contains(s.Tier, new[] { RarityTier.Priority, RarityTier.Decisive }));

        // Two chips in one group widen the result; a second group narrows it.
        Chip("Collection", "META").IsChecked = true;
        var researchOrMeta = ships.Ships.Count;
        Assert.Contains(ships.Ships, s => s.Tier == RarityTier.Meta);
        Chip("Fleet", "Vanguard").IsChecked = true;
        Assert.InRange(ships.Ships.Count, 1, researchOrMeta - 1);
        Assert.All(ships.Ships, s => Assert.Equal("vanguard", s.TeamType));

        ships.Filter = "no ship is called this";
        Assert.True(ships.NoMatches);
        ships.ClearFiltersCommand.Execute(null);
        Assert.Equal(total, ships.Ships.Count);
        Assert.All(ships.FilterGroups, g => Assert.True(g.ShowsAll));

        // Rows hold as many cards as the view asks for, and every ship has exactly one card.
        ships.Columns = 7;
        Assert.All(ships.CardRows.SkipLast(1), r => Assert.Equal(7, r.Cards.Count));
        Assert.Equal(total, ships.CardRows.Sum(r => r.Cards.Count));

        // Event rows filed under a ship's group (Compiler under Universal Bulin) don't replace the ship.
        var bulin = ships.Ships.Single(s => s.GroupId == 10000);
        Assert.Equal("Universal Bulin", bulin.Name);
        Assert.Equal((RarityTier.Elite, "DD", "3"), (bulin.Tier, bulin.HullCode, bulin.RarityKey));
        Assert.DoesNotContain(ships.Ships, s => s.Name == "Compiler");
        Assert.Equal("4_1", ships.Ships.First(s => s.Tier == RarityTier.Meta && s.Rarity == 5).RarityKey);

        var enterprise = ships.Ships.Single(s => s.GroupId == 10706);
        Assert.Equal(RarityTier.SuperRare, enterprise.Tier);
        Assert.Equal("CV", enterprise.HullCode);
        Assert.Equal(ShipCollection.Standard, enterprise.Collection);
        ships.OpenShipCommand.Execute(enterprise);
        Assert.True(ships.IsDetail);
        Assert.Same(enterprise, ships.SelectedShip);
        ships.BackToGalleryCommand.Execute(null);
        Assert.True(ships.IsGallery);
    }

    // Files in the asset folders that aren't Unity bundles still open: sound banks list their cues as WAV, the Lua data
    // tables read as JSON.
    [Fact]
    public void Asset_browser_opens_sound_banks_and_data_tables()
    {
        var dataFolder = Environment.GetEnvironmentVariable("AZLDL_DATA");
        Assert.SkipWhen(string.IsNullOrEmpty(dataFolder), "Set AZLDL_DATA to a data folder.");
        var bundles = Path.Combine(dataFolder!, "AssetBundles");

        var bank = Path.Combine(bundles, "cue", "cv-10517.b");
        var cues = AzlDl.Core.Unity.LooseFileReader.Inspect(bank).Assets;
        Assert.NotEmpty(cues);
        var sound = AzlDl.Core.Unity.LooseFileReader.Read(bank, cues[0]);
        Assert.Equal(AzlDl.Core.Unity.PreviewKind.Audio, sound.Kind);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(sound.Bytes, 0, 4));

        var table = Path.Combine(bundles, "sharecfgdata", "aircraft_template");
        var entry = Assert.Single(AzlDl.Core.Unity.LooseFileReader.Inspect(table).Assets);
        var json = AzlDl.Core.Unity.LooseFileReader.Read(table, entry);
        Assert.Equal("aircraft_template.json", json.FileName);
        Assert.StartsWith("{", json.Text);
    }

    // Music lists the collection's albums and every music bank; ASMR lists the sound stories with their subtitles, and
    // a downloaded recording decodes to sound (its USM audio is masked and ADX-encoded).
    [AvaloniaFact]
    public async Task Music_and_asmr_pages_read_game_audio()
    {
        var dataFolder = Environment.GetEnvironmentVariable("AZLDL_DATA");
        Assert.SkipWhen(string.IsNullOrEmpty(dataFolder), "Set AZLDL_DATA to a data folder.");

        var app = Application.Current!;
        var settings = new AppSettings { Style = AppStyle.ChartRoom, Theme = ThemeMode.Light, DataFolder = dataFolder! };
        StyleManager.Apply(app, settings.Style, settings.Theme);
        var main = new MainViewModel(app, settings);
        var window = new MainWindow { DataContext = main, Width = 1360, Height = 880 };
        window.Show();

        main.SelectedSection = main.Sections.Single(s => s.Page is MusicViewModel);
        await WaitUntil(() => main.Music.IsReady || main.Music.HasProblem);
        Assert.True(main.Music.IsReady, main.Music.Message);
        Assert.True(main.Music.Albums.Count >= 10);
        Assert.Contains(main.Music.Albums, a => a.Name == "All game music" && a.Tracks.Count > 400);
        await WaitUntil(() => main.Music.Albums[0].Image is not null);
        SaveFrame(window, "music.png");

        main.SelectedSection = main.Sections.Single(s => s.Page is AsmrViewModel);
        var asmr = main.Asmr;
        await WaitUntil(() => asmr.IsReady || asmr.HasProblem);
        Assert.True(asmr.IsReady, asmr.Message);
        Assert.Equal(30, asmr.Stories.Count);
        var kashino = asmr.Stories.Single(s => s.Story.Resource == "soundstory2_jp");
        asmr.SelectedStory = kashino;
        await WaitUntil(() => asmr.Lines.Count > 0);
        Assert.NotEmpty(asmr.Lines);
        SaveFrame(window, "asmr.png");

        var recording = Path.Combine(dataFolder!, "AssetBundles", "originsource", "cipher", "soundstory2_jp.cpk");
        if (File.Exists(recording))
        {
            var wav = await asmr.ReadAsync(kashino.Story);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
            Assert.InRange(wav.Length / (48000.0 * 4), 200, 300);
        }
    }

    // Stats follow the game's own formula (model/vo/ship.lua): growth per level, extra growth past 100, enhancement,
    // research and META bonuses, affinity, then retrofit nodes.
    [Fact]
    public void Ship_stats_follow_the_game_formula()
    {
        var dataFolder = Environment.GetEnvironmentVariable("AZLDL_DATA");
        Assert.SkipWhen(string.IsNullOrEmpty(dataFolder), "Set AZLDL_DATA to a data folder.");

        var catalog = ShipCatalog.Load(dataFolder!);
        var love = catalog.AffinityLevels.Single(a => a.Bonus == 0.06);
        string Stat(long group, int level, string label, bool developed = true, bool retrofit = false) =>
            catalog.StatsAt(catalog.Ships.Single(s => s.GroupId == group), level, love, developed, retrofit).Single(s => s.Label == label).Value;

        // USS Enterprise at 120 and 100 affinity: HP 2566 + 119 x 27.54, plus 6%; aviation adds its full enhancement.
        Assert.Equal("6193", Stat(10706, 120, "HP"));
        Assert.Equal("430", Stat(10706, 120, "AVI"));
        Assert.Equal("Medium", Stat(10706, 120, "Armor"));
        Assert.Equal("13", Stat(10706, 120, "Cost"));
        Assert.True(int.Parse(Stat(10706, 120, "AVI", developed: false)) < 430);

        // Eldridge's retrofit adds its node bonuses on top.
        Assert.Equal("1720", Stat(10126, 120, "HP"));
        Assert.Equal("1885", Stat(10126, 120, "HP", retrofit: true));

        // Neptune's development levels add a flat HP bonus and fate simulation luck.
        Assert.Equal(DevelopmentKind.Research, catalog.Ships.Single(s => s.GroupId == 29901).Growth!.Kind);
        Assert.Equal("15", Stat(29901, 120, "LCK"));
        Assert.Equal("0", Stat(29901, 120, "LCK", developed: false));
    }

    // Equipment lists each codex entry once with its stats at +0 and fully enhanced, filters like the ship gallery, and
    // shows the picked piece in the detail pane.
    [AvaloniaFact]
    public async Task Equipment_gallery_shows_stats_and_filters()
    {
        var dataFolder = Environment.GetEnvironmentVariable("AZLDL_DATA");
        Assert.SkipWhen(string.IsNullOrEmpty(dataFolder), "Set AZLDL_DATA to a data folder.");

        var app = Application.Current!;
        var settings = new AppSettings { Style = AppStyle.PortMenu, Theme = ThemeMode.Dark, DataFolder = dataFolder! };
        StyleManager.Apply(app, settings.Style, settings.Theme);
        var main = new MainViewModel(app, settings);
        var window = new MainWindow { DataContext = main, Width = 1360, Height = 880 };
        window.Show();
        main.SelectedSection = main.Sections.Single(s => s.Page is EquipmentViewModel);
        var equipment = main.Equipment;
        await WaitUntil(() => equipment.IsReady || equipment.HasProblem);
        Assert.True(equipment.IsReady, equipment.Message);
        Assert.Equal(["Rarity", "Type", "Tier", "Faction"], equipment.FilterGroups.Select(g => g.Title));

        // Twin 380mm SK C/34: damage grows with enhancement, its FP bonus doesn't.
        var gun = equipment.Shown.Single(e => e.Id == 44100);
        Assert.Equal("Artillery (Battleship)", gun.TypeName);
        Assert.Equal("Iron Blood", gun.NationName);
        Assert.Equal(6, gun.MaxLevel);
        Assert.Contains(gun.Stats, s => s.Label == "Damage" && s.Base == "48 x 2" && s.Max == "86 x 2");
        Assert.Contains(gun.Stats, s => s.Label == "FP" && s.Base == "12");
        Assert.Equal(7, gun.Stats.Single(s => s.Label == "Damage").Values.Count);
        Assert.Equal("damage", gun.Stats.Single(s => s.Label == "Damage").Icon);
        Assert.Contains("Battleship", gun.UsableBy);
        Assert.Equal("Volley", gun.Speciality);

        var total = equipment.Shown.Count;
        var ultra = equipment.FilterGroups.Single(g => g.Title == "Rarity").Chips.Single(c => c.Label == "Ultra Rare");
        ultra.IsChecked = true;
        Assert.InRange(equipment.Shown.Count, 1, total - 1);
        Assert.All(equipment.Shown, e => Assert.Equal(RarityTier.UltraRare, e.Tier));
        ultra.IsChecked = false;
        Assert.Equal(total, equipment.Shown.Count);

        equipment.SelectCommand.Execute(gun);
        Assert.Same(gun, equipment.SelectedEquipment);
        Assert.Single(equipment.CardRows.SelectMany(r => r.Cards), c => c.IsSelected);
        await WaitUntil(() => equipment.CardRows.Take(5).SelectMany(r => r.Cards).Where(c => c.HasImage).All(c => c.Image is not null)
                              && (equipment.SelectedCard?.HasImage != true || equipment.SelectedCard.Image is not null));
        SaveFrame(window, "equipment-PortMenu-Dark.png");
        window.Close();
    }

    // Juustagram shows a post with its picture from the game's image host, its comments and replies, and the replies the
    // Commander can leave, each with the author's answer.
    [AvaloniaFact]
    public async Task Juustagram_shows_posts_comments_and_replies()
    {
        var dataFolder = Environment.GetEnvironmentVariable("AZLDL_DATA");
        Assert.SkipWhen(string.IsNullOrEmpty(dataFolder), "Set AZLDL_DATA to a data folder.");

        var app = Application.Current!;
        var settings = new AppSettings { Style = AppStyle.ChartRoom, Theme = ThemeMode.Light, DataFolder = dataFolder! };
        StyleManager.Apply(app, settings.Style, settings.Theme);
        var main = new MainViewModel(app, settings);
        var window = new MainWindow { DataContext = main, Width = 1360, Height = 880 };
        window.Show();
        main.SelectedSection = main.Sections.Single(s => s.Page is JuustagramViewModel);
        var juustagram = main.Juustagram;
        await WaitUntil(() => juustagram.IsReady || juustagram.HasProblem);
        Assert.True(juustagram.IsReady, juustagram.Message);
        Assert.DoesNotContain(juustagram.Posts, p => p.Post.Message == "Future Content");

        // Shangri-La's post about Essex at the barbecue.
        juustagram.SelectedPost = juustagram.Posts.Single(p => p.Post.Id == 102);
        Assert.StartsWith("Essex is currently concentrating", juustagram.SelectedPost.Post.Message);
        Assert.NotEmpty(juustagram.Comments);
        Assert.True(juustagram.CanReply);
        await WaitUntil(() => juustagram.Picture is not null || juustagram.PictureMessage.StartsWith("The picture didn't", StringComparison.Ordinal));
        Assert.NotNull(juustagram.Picture);

        juustagram.ChooseCommand.Execute(juustagram.Options[0]);
        Assert.False(juustagram.CanReply);
        Assert.Equal("Commander", juustagram.Reply!.Name);
        Assert.Single(juustagram.Reply.Replies);
        await WaitUntil(() => juustagram.Comments.All(c => !c.Avatar.HasImage || c.Avatar.Image is not null)
                              && juustagram.PostAvatar?.Image is not null);
        SaveFrame(window, "juustagram-ChartRoom-Light.png");
        window.Close();
    }

    // Fleet Chat plays a chat up to the Commander's reply and continues down the branch that reply picks.
    [AvaloniaFact]
    public async Task Fleet_chat_follows_the_picked_replies()
    {
        var dataFolder = Environment.GetEnvironmentVariable("AZLDL_DATA");
        Assert.SkipWhen(string.IsNullOrEmpty(dataFolder), "Set AZLDL_DATA to a data folder.");

        var app = Application.Current!;
        var settings = new AppSettings { Style = AppStyle.RegistryBook, Theme = ThemeMode.Dark, DataFolder = dataFolder! };
        StyleManager.Apply(app, settings.Style, settings.Theme);
        var main = new MainViewModel(app, settings);
        var window = new MainWindow { DataContext = main, Width = 1360, Height = 880 };
        window.Show();
        main.SelectedSection = main.Sections.Single(s => s.Page is FleetChatViewModel);
        var chat = main.FleetChat;
        await WaitUntil(() => chat.IsReady || chat.HasProblem);
        Assert.True(chat.IsReady, chat.Message);

        chat.SelectedContact = chat.Contacts.Single(c => c.Title == "Eagle Union Group Chat");
        chat.SelectedTopic = chat.Topics.Single(t => t.Name == "Movie Screening");
        Assert.Equal(6, chat.Messages.Count);
        Assert.True(chat.IsWaiting);
        Assert.Equal("Glad to be here.", Assert.Single(chat.Replies).Text);

        chat.AnswerCommand.Execute(chat.Replies[0]);
        Assert.True(chat.Messages[6].IsCommander);
        Assert.Equal(3, chat.Replies.Count);
        var beforeChoice = chat.Messages.Count;

        // "A romance movie." takes branch 2; branch 3's lines stay hidden.
        chat.AnswerCommand.Execute(chat.Replies.Single(r => r.Text == "A romance movie."));
        Assert.True(chat.IsFinished);
        Assert.Contains(chat.Messages.Skip(beforeChoice), m => m.Text == "Let's watch a romance movie.");
        Assert.DoesNotContain(chat.Messages, m => m.Text == "A road trip movie sounds good. Let's watch that.");
        Assert.Contains(chat.Messages, m => m.Kind == ChatLineKind.Sticker);

        chat.RestartCommand.Execute(null);
        Assert.Equal(6, chat.Messages.Count);
        chat.AnswerCommand.Execute(chat.Replies[0]);
        await WaitUntil(() => chat.Messages.Where(m => m.IsThem).All(m => m.Avatar.HasImage && m.Avatar.Image is not null));
        Assert.All(chat.Messages.Where(m => m.IsThem), m => Assert.True(m.Avatar.HasImage, m.Name));
        await Task.Delay(300);
        Dispatcher.UIThread.RunJobs();
        SaveFrame(window, "fleetchat-RegistryBook-Dark.png");
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

    // Picking Chibi builds a viewer URL on the loopback server, which serves the page and the rig but nothing outside
    // the data folder. Needs the runtimes in <data>/runtimes and Enterprise's qiye_5 chibi extracted.
    [AvaloniaFact]
    public async Task Chibi_viewer_is_served_locally()
    {
        var dataFolder = Environment.GetEnvironmentVariable("AZLDL_DATA");
        Assert.SkipWhen(string.IsNullOrEmpty(dataFolder), "Set AZLDL_DATA to a data folder to render snapshots.");
        Assert.SkipUnless(new AzlDl.Core.Viewer.RuntimeStore(new HttpClient(), dataFolder!).IsInstalled, "Viewer runtimes aren't installed.");

        var ships = new ShipsViewModel(new AppSettings { DataFolder = dataFolder! });
        await ships.LoadAsync();
        Dispatcher.UIThread.RunJobs();
        ships.SelectedSkin = ships.SelectedShip!.Skins.Single(s => s.Painting == "qiye_5");
        ships.SelectedStage = ships.StageOptions.Single(o => o.Mode == StageMode.Chibi);
        for (var i = 0; i < 200 && ships.ViewerState == ViewerState.Loading; i++)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(ViewerState.Ready, ships.ViewerState);
        var url = ships.ViewerUrl!;
        Assert.Equal("127.0.0.1", url.Host);

        using var http = new HttpClient();
        var page = await http.GetAsync(url);
        Assert.True(page.IsSuccessStatusCode);
        Assert.Contains("showSkeletons", await page.Content.ReadAsStringAsync());

        var rig = await http.GetAsync(new Uri(url, "/art/char/qiye_5/qiye_5.skel"));
        Assert.True(rig.IsSuccessStatusCode);

        var escape = await http.GetAsync(new Uri($"http://127.0.0.1:{url.Port}/..%2F..%2FWindows%2Fwin.ini"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, escape.StatusCode);
    }

    // A dynamic skin is served as a layer list placing its skeletons; New Jersey's xinzexi_4 has a back and a front
    // layer. A skin without one says so. Needs the runtimes, and reaches the game's servers for skins not on disk.
    [AvaloniaFact]
    public async Task Dynamic_skin_is_served_as_layers()
    {
        var dataFolder = Environment.GetEnvironmentVariable("AZLDL_DATA");
        Assert.SkipWhen(string.IsNullOrEmpty(dataFolder), "Set AZLDL_DATA to a data folder to render snapshots.");
        Assert.SkipUnless(new AzlDl.Core.Viewer.RuntimeStore(new HttpClient(), dataFolder!).IsInstalled, "Viewer runtimes aren't installed.");

        var ships = new ShipsViewModel(new AppSettings { DataFolder = dataFolder! });
        await ships.LoadAsync();
        Dispatcher.UIThread.RunJobs();
        ships.SelectedShip = ships.Ships.First(s => s.Skins.Any(k => k.Painting == "xinzexi_4"));
        Dispatcher.UIThread.RunJobs();
        ships.SelectedSkin = ships.SelectedShip.Skins.Single(k => k.Painting == "xinzexi_4");
        ships.SelectedStage = ships.StageOptions.Single(o => o.Mode == StageMode.Dynamic);
        await WaitUntil(() => ships.ViewerState != ViewerState.Loading);
        Assert.Equal(ViewerState.Ready, ships.ViewerState);

        using var http = new HttpClient();
        var page = await http.GetAsync(ships.ViewerUrl);
        Assert.True(page.IsSuccessStatusCode);
        var layout = System.Web.HttpUtility.ParseQueryString(ships.ViewerUrl!.Query)["layout"]!;
        var layers = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync(new Uri(ships.ViewerUrl, layout))).RootElement;
        Assert.Equal(2, layers.GetArrayLength());

        // ship_skin_expression puts expression 1 on New Jersey's first main screen line.
        var labels = System.Text.Json.JsonDocument.Parse(System.Web.HttpUtility.ParseQueryString(ships.ViewerUrl.Query)["labels"]!).RootElement;
        Assert.Contains("Main screen 1", labels.GetProperty("1").GetString());
        foreach (var layer in layers.EnumerateArray())
        {
            var skeleton = await http.GetAsync(new Uri(ships.ViewerUrl, "/art/" + layer.GetProperty("skeleton").GetString()));
            Assert.True(skeleton.IsSuccessStatusCode);
        }

        ships.SelectedShip = ships.Ships.First(s => s.Skins.Any(k => k.Painting == "qiye_5"));
        Dispatcher.UIThread.RunJobs();
        ships.SelectedSkin = ships.SelectedShip.Skins.Single(k => k.Painting == "qiye_5");
        await WaitUntil(() => ships.ViewerState != ViewerState.Loading);
        Assert.Equal(ViewerState.Unavailable, ships.ViewerState);
        Assert.EndsWith("has no dynamic skin.", ships.ViewerMessage);
    }

    // Ulrich von Hutten's "What Comes After the Bell" is a background with the character as a second layer. A copy
    // saved before layers were stacked (no layers.json beside it) is rebuilt with both.
    [AvaloniaFact]
    public async Task Layered_painting_is_stacked()
    {
        var dataFolder = Environment.GetEnvironmentVariable("AZLDL_DATA");
        Assert.SkipWhen(string.IsNullOrEmpty(dataFolder), "Set AZLDL_DATA to a data folder to render snapshots.");

        var layout = Path.Combine(dataFolder!, "art", "painting", "wuerlixi_4.layout.json");
        File.Delete(layout);
        var ships = new ShipsViewModel(new AppSettings { DataFolder = dataFolder! });
        await ships.LoadAsync();
        Dispatcher.UIThread.RunJobs();
        ships.SelectedShip = ships.Ships.First(s => s.Skins.Any(k => k.Painting == "wuerlixi_4"));
        Dispatcher.UIThread.RunJobs();
        ships.SelectedSkin = ships.SelectedShip.Skins.Single(k => k.Painting == "wuerlixi_4");
        await WaitUntil(() => ships.PaintingState is PaintingState.Shown or PaintingState.Failed && File.Exists(layout));

        Assert.Equal(PaintingState.Shown, ships.PaintingState);
        var layers = System.Text.Json.JsonDocument.Parse(File.ReadAllText(layout)).RootElement.GetProperty("layers");
        Assert.Equal(["wuerlixi_4", "wuerlixi_4_rw"], layers.EnumerateArray().Select(l => l.GetString()));
    }

    // Enterprise's "Soaring Wings of Freedom" has two expression faces; picking one draws it in the painting's face slot.
    [AvaloniaFact]
    public async Task Painting_shows_expression_faces()
    {
        var dataFolder = Environment.GetEnvironmentVariable("AZLDL_DATA");
        Assert.SkipWhen(string.IsNullOrEmpty(dataFolder), "Set AZLDL_DATA to a data folder to render snapshots.");

        var app = Application.Current!;
        var settings = new AppSettings { Style = AppStyle.PortMenu, Theme = ThemeMode.Dark, DataFolder = dataFolder! };
        StyleManager.Apply(app, settings.Style, settings.Theme);
        var main = new MainViewModel(app, settings);
        var window = new MainWindow { DataContext = main, Width = 1360, Height = 880 };
        window.Show();
        var ships = main.Ships;
        await ships.LoadAsync();
        Dispatcher.UIThread.RunJobs();
        ships.SelectedShip = ships.Ships.First(s => s.Skins.Any(k => k.Painting == "qiye_5"));
        Dispatcher.UIThread.RunJobs();
        ships.SelectedSkin = ships.SelectedShip.Skins.Single(k => k.Painting == "qiye_5");
        await WaitUntil(() => ships.HasFaces || ships.PaintingState == PaintingState.Failed);

        Assert.True(ships.HasFaces, ships.PaintingMessage);
        Assert.Equal(["", "1", "2"], ships.Faces.Select(f => f.Name));
        ships.SelectedFace = ships.Faces.Single(f => f.Name == "2");
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(ships.Face);
        Assert.Equal(118, ships.FaceSlot.Width, 0);
        SaveFrame(window, "ships-face-PortMenu-Dark.png");
        window.Close();
    }

    // Enterprise's "Soaring Wings of Freedom" Live2D model gets its AnimationClips as motions, labelled by voice line.
    // A model extracted before motions were converted (no motions folder) is extracted again.
    [AvaloniaFact]
    public async Task Live2D_model_has_motions()
    {
        var dataFolder = Environment.GetEnvironmentVariable("AZLDL_DATA");
        Assert.SkipWhen(string.IsNullOrEmpty(dataFolder), "Set AZLDL_DATA to a data folder to render snapshots.");
        Assert.SkipUnless(new AzlDl.Core.Viewer.RuntimeStore(new HttpClient(), dataFolder!).IsInstalled, "Viewer runtimes aren't installed.");

        var motions = Path.Combine(dataFolder!, "art", "live2d", "qiye_7", "motions");
        if (Directory.Exists(motions))
        {
            Directory.Delete(motions, recursive: true);
        }

        var ships = new ShipsViewModel(new AppSettings { DataFolder = dataFolder! });
        await ships.LoadAsync();
        Dispatcher.UIThread.RunJobs();
        ships.SelectedShip = ships.Ships.First(s => s.Skins.Any(k => k.Painting == "qiye_7"));
        Dispatcher.UIThread.RunJobs();
        ships.SelectedSkin = ships.SelectedShip.Skins.Single(k => k.Painting == "qiye_7");
        ships.SelectedStage = ships.StageOptions.Single(o => o.Mode == StageMode.Live2D);
        await WaitUntil(() => ships.ViewerState != ViewerState.Loading);
        Assert.Equal(ViewerState.Ready, ships.ViewerState);

        using var http = new HttpClient();
        var model = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync(new Uri(ships.ViewerUrl!, "/art/live2d/qiye_7/qiye_7.model3.json"))).RootElement;
        var groups = model.GetProperty("FileReferences").GetProperty("Motions");
        Assert.True(groups.TryGetProperty("Idle", out _));
        var file = groups.GetProperty("main_1")[0].GetProperty("File").GetString()!;
        var motion = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync(new Uri(ships.ViewerUrl!, "/art/live2d/qiye_7/" + file))).RootElement;
        Assert.Contains(motion.GetProperty("Curves").EnumerateArray(), c => c.GetProperty("Id").GetString() == "ParamAngleX");

        var labels = System.Text.Json.JsonDocument.Parse(System.Web.HttpUtility.ParseQueryString(ships.ViewerUrl!.Query)["labels"]!).RootElement;
        Assert.Contains("Main screen 1", labels.GetProperty("main_1").GetString());
    }

    // Plymouth's interactive Live2D skin (pulimaosi_3): ship_l2d says which idle each action leaves her in.
    [Fact]
    public void Live2D_actions_name_their_next_idle()
    {
        var dataFolder = Environment.GetEnvironmentVariable("AZLDL_DATA");
        Assert.SkipWhen(string.IsNullOrEmpty(dataFolder), "Set AZLDL_DATA to a data folder to render snapshots.");

        var catalog = AzlDl.Core.Data.ShipCatalog.Load(dataFolder!);
        var skin = catalog.Ships.SelectMany(s => s.Skins).First(k => k.Painting == "pulimaosi_3");
        var idles = catalog.Live2DIdleAfter(skin);
        Assert.Equal("idle14", idles["touch_idle23"]);
        Assert.Equal("idle", idles["touch_idle4"]);
    }

    // Enterprise's fifth skin (group index 4) uses its own recordings (detail_4) where it has them, served to the viewer as WAV.
    [AvaloniaFact]
    public async Task Voice_lines_are_served_with_animations()
    {
        var dataFolder = Environment.GetEnvironmentVariable("AZLDL_DATA");
        Assert.SkipWhen(string.IsNullOrEmpty(dataFolder), "Set AZLDL_DATA to a data folder to render snapshots.");
        Assert.SkipUnless(new AzlDl.Core.Viewer.RuntimeStore(new HttpClient(), dataFolder!).IsInstalled, "Viewer runtimes aren't installed.");

        var library = new AzlDl.Core.Audio.VoiceLibrary(Path.Combine(dataFolder!, "AssetBundles"));
        Assert.Equal(new AzlDl.Core.Audio.VoiceCue("cv-10706", "detail_4"), library.Find(10706, 4, "detail"));
        Assert.Equal(new AzlDl.Core.Audio.VoiceCue("cv-10706-battle", "mvp_4"), library.Find(10706, 4, "mvp"));

        // The skin has no low-HP line of its own, so the default skin's is used.
        Assert.Equal(new AzlDl.Core.Audio.VoiceCue("cv-10706-battle", "hp"), library.Find(10706, 4, "hp"));

        var ships = new ShipsViewModel(new AppSettings { DataFolder = dataFolder! });
        await ships.LoadAsync();
        Dispatcher.UIThread.RunJobs();
        ships.SelectedShip = ships.Ships.First(s => s.Skins.Any(k => k.Painting == "qiye_5"));
        Dispatcher.UIThread.RunJobs();
        ships.SelectedSkin = ships.SelectedShip.Skins.Single(k => k.Painting == "qiye_5");
        ships.SelectedStage = ships.StageOptions.Single(o => o.Mode == StageMode.Chibi);
        await WaitUntil(() => ships.ViewerState != ViewerState.Loading);
        Assert.Equal(ViewerState.Ready, ships.ViewerState);

        var voices = System.Text.Json.JsonDocument.Parse(System.Web.HttpUtility.ParseQueryString(ships.ViewerUrl!.Query)["voices"]!).RootElement;
        var url = voices.EnumerateObject().First().Value.GetString()!;
        using var http = new HttpClient();
        var wav = await http.GetByteArrayAsync(new Uri(ships.ViewerUrl, url));
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.True(wav.Length > 10_000);

        var refused = await http.GetAsync(new Uri(ships.ViewerUrl, "/voice/..%2F..%2Fconfig/x.wav"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, refused.StatusCode);
    }

    // The browser lists bundles by folder, previews a texture, opens the encrypted scripts bundle, and saves an object.
    [AvaloniaFact]
    public async Task Asset_browser_previews_and_saves()
    {
        var dataFolder = Environment.GetEnvironmentVariable("AZLDL_DATA");
        Assert.SkipWhen(string.IsNullOrEmpty(dataFolder), "Set AZLDL_DATA to a data folder to render snapshots.");

        var app = Application.Current!;
        var settings = new AppSettings { Style = AppStyle.ChartRoom, Theme = ThemeMode.Dark, DataFolder = dataFolder! };
        StyleManager.Apply(app, settings.Style, settings.Theme);
        var main = new MainViewModel(app, settings);
        var window = new MainWindow { DataContext = main, Width = 1360, Height = 880 };
        window.Show();
        main.SelectedSection = main.Sections.Single(s => s.Page is AssetsViewModel);
        var assets = main.Assets;
        await WaitUntil(() => assets.IsReady || assets.HasProblem);
        Assert.True(assets.IsReady, assets.Message);

        assets.SelectedFolder = assets.Folders.Single(f => f.Name == "painting");
        assets.Filter = "qiye_5_tex";
        Dispatcher.UIThread.RunJobs();
        assets.SelectedBundle = assets.Bundles.Single();
        await WaitUntil(() => assets.ShowsImage);
        Assert.Equal("Texture2D", assets.SelectedAsset!.Type);
        Assert.NotNull(assets.PreviewImage);
        Dispatcher.UIThread.RunJobs();
        SaveFrame(window, "assets-ChartRoom-Dark.png");

        var target = Path.Combine(OutputFolder(), assets.SuggestedFileName);
        await assets.SaveAssetCommand.ExecuteAsync(target);
        Assert.True(new FileInfo(target).Length > 0, assets.ActionMessage);

        assets.SelectedFolder = assets.Folders.Single(f => f.Name == "(top level)");
        assets.Filter = "scripts64";
        Dispatcher.UIThread.RunJobs();
        assets.SelectedBundle = assets.Bundles.Single();
        await WaitUntil(() => assets.Assets.Count > 0 || assets.BundleMessage.StartsWith("Couldn't", StringComparison.Ordinal));
        Assert.Contains(assets.Assets, a => a.Type == "TextAsset");
        Assert.False(assets.ExtractBundleCommand.CanExecute(null));

        assets.SelectedAsset = assets.Assets.First(a => a.Type == "TextAsset");
        await WaitUntil(() => assets.Preview is not null || assets.PreviewMessage.StartsWith("Couldn't", StringComparison.Ordinal));
        Assert.NotNull(assets.Preview);
        window.Close();
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        for (var i = 0; i < 600 && !done(); i++)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
        }
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
