using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media.Imaging;
using AzlDl.App.Audio;
using AzlDl.App.Controls;
using AzlDl.App.Settings;
using AzlDl.Core.Audio;
using AzlDl.Core;
using AzlDl.Core.Art;
using AzlDl.App.Viewer;
using AzlDl.Core.Data;
using AzlDl.Core.Versioning;
using AzlDl.Core.Viewer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzlDl.App.ViewModels;

public enum LoadState
{
    Loading,
    Ready,
    MissingData,
    Failed,
}

public enum StageMode
{
    Painting,
    Chibi,
    Dynamic,
    Live2D,
}

public enum ViewerState
{
    NeedsRuntimes,
    Loading,
    Ready,
    Unavailable,
    Failed,
}

public sealed record StageOption(StageMode Mode, string Title);

// An expression face for the painting; Name is empty for the painting without a face.
public sealed record FaceOption(string Name, string Title);

public enum PaintingState
{
    NotDownloaded,
    Downloading,
    Shown,
    Failed,
}

// The ship gallery and the selected ship's page.
public sealed partial class ShipsViewModel(AppSettings settings) : ObservableObject
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private IReadOnlyList<ShipInfo> all = [];
    private ShipCatalog? catalog;
    private LocalFileServer? server;

    public IReadOnlyList<StageOption> StageOptions { get; } =
    [
        new(StageMode.Painting, "Painting"),
        new(StageMode.Chibi, "Chibi"),
        new(StageMode.Dynamic, "Dynamic"),
        new(StageMode.Live2D, "Live2D"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPainting), nameof(ShowsViewer))]
    private StageOption? selectedStage;

    public bool ShowsPainting => SelectedStage is null || SelectedStage.Mode == StageMode.Painting;

    public bool ShowsViewer => !ShowsPainting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewerReady), nameof(NeedsRuntimes), nameof(ViewerBusy), nameof(ViewerMessageShown))]
    private ViewerState viewerState = ViewerState.Loading;

    public bool ViewerReady => ViewerState == ViewerState.Ready;

    public bool NeedsRuntimes => ViewerState == ViewerState.NeedsRuntimes;

    public bool ViewerBusy => ViewerState == ViewerState.Loading;

    public bool ViewerMessageShown => ViewerState is ViewerState.Unavailable or ViewerState.Failed or ViewerState.Loading;

    [ObservableProperty]
    private Uri? viewerUrl;

    [ObservableProperty]
    private string viewerMessage = "";

    // What the runtimes are and their terms, shown before anything is downloaded.
    public IReadOnlyList<RuntimeFile> RuntimeFiles => RuntimeStore.Files;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsReady), nameof(HasProblem))]
    private LoadState state = LoadState.Loading;

    public bool IsLoading => State == LoadState.Loading;

    public bool IsReady => State == LoadState.Ready;

    public bool HasProblem => State is LoadState.MissingData or LoadState.Failed;

    [ObservableProperty]
    private string message = "";

    [ObservableProperty]
    private string filter = "";

    // The gallery shows every ship as a card; opening one shows its page until Back.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGallery))]
    private bool isDetail;

    public bool IsGallery => !IsDetail;

    public ObservableCollection<FilterGroup> FilterGroups { get; } = [];

    public IReadOnlyList<SortOption<ShipInfo>> SortOptions { get; } =
    [
        new("Rarity, high to low", ships => ships.OrderByDescending(s => s.Rarity).ThenBy(s => s.GroupId)),
        new("Rarity, low to high", ships => ships.OrderBy(s => s.Rarity).ThenBy(s => s.GroupId)),
        new("Name", ships => ships.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)),
        new("Hull type", ships => ships.OrderBy(s => s.TypeId).ThenByDescending(s => s.Rarity).ThenBy(s => s.GroupId)),
        new("Ship number", ships => ships.OrderBy(s => s.GroupId)),
    ];

    [ObservableProperty]
    private SortOption<ShipInfo>? selectedSort;

    public ObservableCollection<ShipCardRow> CardRows { get; } = [];

    // Cards per row, set by the view from its width.
    [ObservableProperty]
    private int columns = 6;

    private readonly Dictionary<ShipInfo, ShipCard> cards = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadImages), nameof(ImageDownloadTitle))]
    private int imagesMissing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadImages))]
    private bool imagesBusy;

    [ObservableProperty]
    private string imageMessage = "";

    // Whether the game's card frames, backgrounds and badges (art/ui) are downloaded; without them cards are drawn.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadImages), nameof(ImageDownloadTitle))]
    private bool hasGameArt;

    public bool CanDownloadImages => (ImagesMissing > 0 || !HasGameArt) && !ImagesBusy;

    // Card images are about 30 KB each.
    // Card images are about 30 KB each; the frames and badges about 1.4 MB together.
    public string ImageDownloadTitle => ImagesMissing > 0
        ? $"Download {Count(ImagesMissing)} card images (about {Math.Max(1, ImagesMissing * 30 / 1024) + (HasGameArt ? 0 : 1)} MB)"
        : "Download the game's card frames (about 1 MB)";

    private static string Count(int value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    // Cards keep the game's 232 x 324 proportions.
    public const double CardWidth = 132;

    public const double CardHeight = 184;

    public const double CardSpacing = 12;

    public bool NoMatches => IsReady && Ships.Count == 0;

    public string ShownTitle => Ships.Count == all.Count ? $"{Count(all.Count)} ships" : $"{Count(Ships.Count)} of {Count(all.Count)} ships";

    [ObservableProperty]
    private ShipInfo? selectedShip;

    [ObservableProperty]
    private SkinInfo? selectedSkin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaintingShown), nameof(CanDownloadPainting), nameof(PaintingBusy))]
    private PaintingState paintingState = PaintingState.NotDownloaded;

    public bool PaintingShown => PaintingState == PaintingState.Shown;

    public bool CanDownloadPainting => PaintingState is PaintingState.NotDownloaded or PaintingState.Failed;

    public bool PaintingBusy => PaintingState == PaintingState.Downloading;

    [ObservableProperty]
    private Bitmap? painting;

    // The painting's pixel size and its face slot, from the layout written next to it.
    [ObservableProperty]
    private double paintingWidth;

    [ObservableProperty]
    private double paintingHeight;

    [ObservableProperty]
    private Rect faceSlot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFaces))]
    private string? faceFolder;

    public bool HasFaces => FaceFolder is not null && Faces.Count > 1;

    public ObservableCollection<FaceOption> Faces { get; } = [];

    [ObservableProperty]
    private FaceOption? selectedFace;

    [ObservableProperty]
    private Bitmap? face;

    [ObservableProperty]
    private string paintingMessage = "";

    public ObservableCollection<ShipInfo> Ships { get; } = [];

    // The selected skin's voice lines (its own, with the default skin's for the rest).
    public ObservableCollection<VoiceLine> VoiceLines { get; } = [];

    public bool CanPlayVoices => VoicePlayer.IsSupported;

    // Downloading, missing recording or decode problems for the voice line buttons; empty when there is nothing to say.
    [ObservableProperty]
    private string voiceMessage = "";

    private VoiceLibrary? voiceLibrary;
    private string? voiceLibraryFolder;

    private VoiceLibrary Voices()
    {
        var folder = Fetcher().AssetFolder;
        if (voiceLibrary is null || voiceLibraryFolder != folder)
        {
            voiceLibrary = new VoiceLibrary(folder);
            voiceLibraryFolder = folder;
        }

        return voiceLibrary;
    }

    // Downloads the ship's voice banks the first time; later calls use what is on disk.
    private async Task EnsureVoiceBanksAsync(ShipInfo ship)
    {
        var fetcher = Fetcher();
        if (!File.Exists(Path.Combine(fetcher.AssetFolder, VoiceLibrary.BankPaths(ship.GroupId)[0])))
        {
            await Task.Run(() => fetcher.FetchVoiceBanksAsync(ship.GroupId));
        }
    }

    [RelayCommand]
    private async Task PlayVoiceAsync(VoiceLine? line)
    {
        if (line is null || SelectedShip is not { } ship || SelectedSkin is not { } skin)
        {
            return;
        }

        VoiceMessage = "";
        try
        {
            if (!File.Exists(Path.Combine(Fetcher().AssetFolder, VoiceLibrary.BankPaths(ship.GroupId)[0])))
            {
                VoiceMessage = $"Downloading {ship.Name}'s voice lines…";
                await EnsureVoiceBanksAsync(ship);
                VoiceMessage = "";
            }

            var library = Voices();
            var wav = await Task.Run(() => library.Find(ship.GroupId, skin.GroupIndex, line.ResourceKey) is { } cue ? library.ReadWav(cue) : null);
            if (wav is null)
            {
                VoiceMessage = $"The game has no recording of \"{line.Label}\" for this skin.";
                return;
            }

            VoicePlayer.Play(wav);
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException
                                      or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            VoiceMessage = $"Couldn't play \"{line.Label}\": {e.Message}";
        }
    }

    public string DataFolder => settings.DataFolder;

    // Set by the shell; shows the Sync section.
    public Action? OpenSync { get; set; }

    [RelayCommand]
    private void GoToSync() => OpenSync?.Invoke();

    public async Task LoadAsync()
    {
        State = LoadState.Loading;
        Message = "Reading ship data…";
        SelectedStage ??= StageOptions[0];
        var folder = settings.DataFolder;

        var missing = ShipCatalog.MissingFiles(folder);
        if (missing.Count > 0)
        {
            all = [];
            Ships.Clear();
            State = LoadState.MissingData;
            Message = $"There's no game data in {folder} yet. Update it from Sync (about 150 MB to download), " +
                      "or pick a folder that already has it in Settings.";
            OnPropertyChanged(nameof(DataFolder));
            return;
        }

        try
        {
            catalog = await Task.Run(() => ShipCatalog.Load(folder));
            all = catalog.Ships;
            BuildGallery();
            ApplyFilter();
            SelectedShip = Ships.FirstOrDefault(s => s.EnglishName.Contains("Enterprise", StringComparison.Ordinal)) ?? Ships.FirstOrDefault();
            State = LoadState.Ready;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or FormatException)
        {
            State = LoadState.Failed;
            Message = $"Couldn't read the ship data in {folder}: {e.Message}";
        }

        OnPropertyChanged(nameof(DataFolder));
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnSelectedSortChanged(SortOption<ShipInfo>? value) => ApplyFilter();

    partial void OnColumnsChanged(int value) => BuildRows();

    [RelayCommand]
    private void OpenShip(ShipInfo? ship)
    {
        if (ship is null)
        {
            return;
        }

        SelectedShip = ship;
        IsDetail = true;
    }

    [RelayCommand]
    private void BackToGallery()
    {
        IsDetail = false;
        VoicePlayer.Stop();
    }

    [RelayCommand]
    private void ClearFilters()
    {
        Filter = "";
        foreach (var group in FilterGroups)
        {
            group.Clear();
        }
    }

    // Filter chips come from the ships themselves, so only values some ship has are offered.
    private void BuildGallery()
    {
        var fetcher = Fetcher();
        cards.Clear();
        foreach (var ship in all)
        {
            var path = fetcher.ShipImagePath(ship.Painting);
            cards[ship] = new ShipCard(ship, ship.Painting.Length > 0 && File.Exists(path) ? path : null);
        }

        ImagesMissing = cards.Values.Count(c => !c.HasImage && c.Ship.Painting.Length > 0);
        GameArt.Use(fetcher.GameArtFolder);
        HasGameArt = fetcher.HasGameArt;
        SelectedSort ??= SortOptions[0];
        BuildFilters();
    }

    // Rebuilt after the game's art is downloaded so chips pick up their badges; picked chips stay picked.
    private void BuildFilters()
    {
        var picked = FilterGroups.SelectMany(g => g.Chips.Where(c => c.IsChecked).Select(c => (g.Title, c.Label))).ToHashSet();
        string[] tierCodes = ["N", "R", "SR", "SSR", "UR"];
        var rarities = all.GroupBy(s => s.Rarity).OrderBy(g => g.Key)
            .Select(g => FilterChip.For<ShipInfo>(g.First().RarityName, tierCodes[g.Key - 2], s => s.Rarity == g.Key, (RarityTier)(g.Key - 2)));

        var collections = new (ShipCollection Collection, string Title)[]
        {
            (ShipCollection.Standard, "Standard"), (ShipCollection.Research, "Research"), (ShipCollection.Meta, "META"),
            (ShipCollection.Collab, "Collab"),
        }.Where(c => all.Any(s => s.Collection == c.Collection))
            .Select(c => FilterChip.For<ShipInfo>(c.Title, "", s => s.Collection == c.Collection,
                c.Collection switch { ShipCollection.Research => RarityTier.Priority, ShipCollection.Meta => RarityTier.Meta, _ => null }));

        var roles = new (string Team, string Title)[] { ("vanguard", "Vanguard"), ("main", "Main fleet"), ("submarine", "Submarine") }
            .Where(r => all.Any(s => s.TeamType == r.Team))
            .Select(r => FilterChip.For<ShipInfo>(r.Title, "", s => s.TeamType == r.Team));

        // Some hull names repeat across fleet roles (DDG, Sailing Frigate); those say which fleet they sail in.
        var types = all.GroupBy(s => s.TypeId).OrderBy(g => g.Key).ToList();
        var typeNames = types.GroupBy(g => g.First().TypeName.Trim()).Where(n => n.Count() > 1).Select(n => n.Key).ToHashSet();
        var hulls = types.Select(g =>
        {
            var name = g.First().TypeName.Trim();
            var team = g.First().TeamType switch { "vanguard" => "Vanguard", "main" => "Main fleet", "submarine" => "Submarine", var t => t };
            return FilterChip.For<ShipInfo>(typeNames.Contains(name) ? $"{name} ({team})" : name, g.First().HullCode, s => s.TypeId == g.Key,
                icon: $"hull_{g.Key}");
        });

        var factions = all.GroupBy(s => s.NationName).OrderBy(g => g.Min(s => s.NationId))
            .Select(g => FilterChip.For<ShipInfo>(g.Key, "", s => s.NationName == g.Key, icon: $"nation_{g.Min(s => s.NationId)}"));

        FilterGroups.Clear();
        foreach (var (title, chips) in new[] { ("Rarity", rarities), ("Collection", collections), ("Fleet", roles), ("Hull type", hulls), ("Faction", factions) })
        {
            var group = new FilterGroup(title, chips, ApplyFilter);
            FilterGroups.Add(group);
            foreach (var chip in group.Chips.Where(c => picked.Contains((title, c.Label))))
            {
                chip.IsChecked = true;
            }
        }
    }

    private void BuildRows()
    {
        CardRows.Clear();
        var columns = Math.Max(1, Columns);
        for (var i = 0; i < Ships.Count; i += columns)
        {
            CardRows.Add(new ShipCardRow(Ships.Skip(i).Take(columns).Select(s => cards[s]).ToList()));
        }

        OnPropertyChanged(nameof(ShownTitle));
        OnPropertyChanged(nameof(NoMatches));
    }

    [RelayCommand]
    private async Task DownloadShipImagesAsync()
    {
        var missing = cards.Values.Where(c => !c.HasImage && c.Ship.Painting.Length > 0).ToList();
        if (missing.Count == 0 && HasGameArt)
        {
            return;
        }

        ImagesBusy = true;
        ImageMessage = $"Downloading {Count(missing.Count)} card images…";
        try
        {
            var fetcher = Fetcher();
            if (!HasGameArt)
            {
                await Task.Run(() => fetcher.FetchGameArtAsync());
                GameArt.Refresh();
                HasGameArt = fetcher.HasGameArt;
            }

            await Task.Run(() => fetcher.FetchImagesAsync(
                missing.Select(c => AssetFetcher.ShipImageFolder + c.Ship.Painting).Distinct().ToList()));
            foreach (var card in missing)
            {
                var path = fetcher.ShipImagePath(card.Ship.Painting);
                if (File.Exists(path))
                {
                    CardImages.Forget(path);
                    card.SetImagePath(path);
                }
            }

            ImagesMissing = cards.Values.Count(c => !c.HasImage && c.Ship.Painting.Length > 0);
            ImageMessage = ImagesMissing == 0 ? "" : $"The game has no card image for {Count(ImagesMissing)} ships.";

            // New row and chip objects bind again, now with the game's frames and badges.
            BuildFilters();
            BuildRows();
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException
                                      or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            ImageMessage = $"The card images didn't download: {e.Message}";
        }
        finally
        {
            ImagesBusy = false;
        }
    }

    // Posted so the skin list has the new ship's skins before one is selected; set earlier, the list would ignore it.
    partial void OnSelectedShipChanged(ShipInfo? value) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (SelectedShip == value)
            {
                SelectedSkin = value?.Skins.FirstOrDefault();
            }
        });

    partial void OnSelectedStageChanged(StageOption? value) => _ = ShowViewerAsync();

    partial void OnSelectedSkinChanged(SkinInfo? value)
    {
        // Swapping the skin list's items clears its selection; that null is ignored, the posted selection follows.
        if (value is null && SelectedShip?.Skins.Count > 0)
        {
            return;
        }

        ShowPainting(value);
        _ = ShowViewerAsync();
    }

    [RelayCommand]
    private async Task InstallRuntimesAsync()
    {
        ViewerState = ViewerState.Loading;
        ViewerMessage = "Downloading the viewer runtimes…";
        try
        {
            await new RuntimeStore(Http, settings.DataFolder).InstallAsync();
            await ShowViewerAsync();
        }
        catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException)
        {
            ViewerState = ViewerState.Failed;
            ViewerMessage = $"The runtimes didn't download: {e.Message}";
        }
    }

    // Loads the chibi, dynamic skin or Live2D model for the selected skin into the viewer page, downloading it if needed.
    private async Task ShowViewerAsync()
    {
        if (ShowsPainting || SelectedSkin is not { } skin)
        {
            return;
        }

        var mode = SelectedStage!.Mode;
        ViewerUrl = null;
        if (!new RuntimeStore(Http, settings.DataFolder).IsInstalled)
        {
            ViewerState = ViewerState.NeedsRuntimes;
            return;
        }

        ViewerState = ViewerState.Loading;
        // Bundle paths are lower case on the CDN and on disk.
        var (what, bundle) = mode switch
        {
            StageMode.Chibi => ("chibi", $"char/{skin.Painting.ToLowerInvariant()}"),
            StageMode.Dynamic => ("dynamic skin", $"{DynamicSkinReader.Folder}{skin.Painting.ToLowerInvariant()}"),
            _ => ("Live2D model", $"live2d/{skin.Painting.ToLowerInvariant()}"),
        };
        ViewerMessage = $"Getting the {what} for {skin.Name}…";
        try
        {
            var folder = await Task.Run(() => mode switch
            {
                StageMode.Chibi => Fetcher().FetchBundleAsync(AssetCategory.Azl, bundle),
                StageMode.Dynamic => Fetcher().FetchDynamicSkinAsync(skin.Painting),
                _ => Fetcher().FetchBundleAsync(AssetCategory.Live2D, bundle),
            });
            if (SelectedSkin != skin || SelectedStage?.Mode != mode)
            {
                return;
            }

            if (folder is null)
            {
                ViewerState = ViewerState.Unavailable;
                ViewerMessage = $"{skin.Name} has no {what}.";
                return;
            }

            var query = mode switch
            {
                StageMode.Chibi => $"kind=spine&skel=/art/{bundle}/{skin.Painting}.skel&atlas=/art/{bundle}/{skin.Painting}.atlas",
                StageMode.Dynamic => $"kind=layers&layout=/art/{bundle}/{DynamicSkinReader.LayoutFileName}",
                _ => $"kind=live2d&model=/art/{bundle}/{skin.Painting}.model3.json",
            };
            if (catalog is not null && SelectedShip is { } ship)
            {
                query += await VoiceQueryAsync(ship, skin, mode);
                if (SelectedSkin != skin || SelectedStage?.Mode != mode)
                {
                    return;
                }

                var set = mode switch { StageMode.Chibi => AnimationSet.Chibi, StageMode.Dynamic => AnimationSet.Expression, _ => AnimationSet.Live2D };
                var labels = catalog.AnimationLabels(ship, skin, set);
                query += "&labels=" + Uri.EscapeDataString(System.Text.Json.JsonSerializer.Serialize(labels));
                if (mode == StageMode.Live2D && catalog.Live2DIdleAfter(skin) is { Count: > 0 } idles)
                {
                    query += "&idles=" + Uri.EscapeDataString(System.Text.Json.JsonSerializer.Serialize(idles));
                }
            }

            ViewerUrl = Server().UrlFor($"/viewer.html?{query}&{ThemeColors()}");
            ViewerState = ViewerState.Ready;
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException
                                      or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            ViewerState = ViewerState.Failed;
            ViewerMessage = $"Couldn't get {bundle}: {e.Message}";
        }
    }

    // "&voices=" with each animation's first voice line that has a recording, as a URL the viewer page can play.
    // Voice lines are optional: without them (no connection, no banks) the viewer still plays the animations.
    private async Task<string> VoiceQueryAsync(ShipInfo ship, SkinInfo skin, StageMode mode)
    {
        if (catalog is null)
        {
            return "";
        }

        try
        {
            await EnsureVoiceBanksAsync(ship);
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException
                                      or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            return "";
        }

        var set = mode switch { StageMode.Chibi => AnimationSet.Chibi, StageMode.Dynamic => AnimationSet.Expression, _ => AnimationSet.Live2D };
        var library = Voices();
        var lines = catalog.AnimationVoiceLines(ship, skin, set);
        var urls = await Task.Run(() => lines
            .Select(p => (p.Key, Cue: p.Value.Select(l => library.Find(ship.GroupId, skin.GroupIndex, l.ResourceKey)).FirstOrDefault(c => c is not null)))
            .Where(p => p.Cue is not null)
            .ToDictionary(p => p.Key, p => LocalFileServer.VoicePath(p.Cue!), StringComparer.Ordinal));
        return urls.Count == 0 ? "" : "&voices=" + Uri.EscapeDataString(System.Text.Json.JsonSerializer.Serialize(urls));
    }

    private LocalFileServer Server()
    {
        if (server is null || !string.Equals(server.Root, Path.GetFullPath(settings.DataFolder), StringComparison.OrdinalIgnoreCase))
        {
            server?.Dispose();
            server = new LocalFileServer(settings.DataFolder);
        }

        return server;
    }

    // The viewer page takes the current style's stage, ink, muted and accent colors so it matches the window.
    private static string ThemeColors()
    {
        string Hex(string key)
        {
            var app = Avalonia.Application.Current;
            return app is not null && Avalonia.Controls.ResourceNodeExtensions.TryFindResource(app, key, app.ActualThemeVariant, out var value)
                   && value is Avalonia.Media.ISolidColorBrush brush
                ? $"{brush.Color.R:x2}{brush.Color.G:x2}{brush.Color.B:x2}"
                : "";
        }

        return $"bg={Hex("StageBrush")}&ink={Hex("InkBrush")}&muted={Hex("MutedBrush")}&accent={Hex("AccentBrush")}&accentInk={Hex("AccentInkBrush")}";
    }

    private void ApplyFilter()
    {
        var shown = all.Where(s => (Filter.Length == 0
                                    || s.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                                    || s.EnglishName.Contains(Filter, StringComparison.OrdinalIgnoreCase))
                                   && FilterGroups.All(g => g.Matches(s)));
        Ships.Clear();
        foreach (var ship in (SelectedSort ?? SortOptions[0]).Sort(shown))
        {
            Ships.Add(ship);
        }

        BuildRows();
    }

    private void ShowPainting(SkinInfo? skin)
    {
        VoiceLines.Clear();
        VoiceMessage = "";
        VoicePlayer.Stop();
        if (skin is not null && SelectedShip is { } ship && catalog is not null)
        {
            foreach (var line in catalog.VoiceLines(ship, skin))
            {
                VoiceLines.Add(line);
            }
        }

        Painting = null;
        ClearFaces();
        if (skin is null)
        {
            PaintingState = PaintingState.NotDownloaded;
            return;
        }

        var fetcher = Fetcher();
        var path = fetcher.PaintingPath(skin.Painting);
        if (fetcher.IsPaintingCurrent(skin.Painting))
        {
            var bitmap = new Bitmap(path);
            var layout = PaintingComposer.ReadLayoutFile(path);
            PaintingWidth = bitmap.Size.Width;
            PaintingHeight = bitmap.Size.Height;
            Painting = bitmap;
            PaintingState = PaintingState.Shown;
            if (layout?.Face is { } slot)
            {
                FaceSlot = new Rect(slot.X, slot.Y, slot.Width, slot.Height);
                _ = LoadFacesAsync(skin);
            }
        }
        else if (File.Exists(path))
        {
            // Saved before its other layers were stacked onto it; rebuild it from the bundles.
            _ = DownloadPaintingAsync();
        }
        else
        {
            PaintingState = PaintingState.NotDownloaded;
            PaintingMessage = $"Painting {skin.Painting} isn't downloaded yet.";
        }
    }

    private void ClearFaces()
    {
        FaceFolder = null;
        Faces.Clear();
        SelectedFace = null;
        Face = null;
    }

    // Lists the painting's expression faces, downloading them the first time. Each is labelled with the voice lines
    // the game shows it with (ship_skin_expression). Without faces the picker stays hidden.
    private async Task LoadFacesAsync(SkinInfo skin)
    {
        string? folder;
        try
        {
            folder = await Task.Run(() => Fetcher().FetchFacesAsync(skin.Painting));
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException
                                      or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            folder = null;
        }

        if (SelectedSkin != skin || folder is null || SelectedShip is not { } ship || catalog is null)
        {
            return;
        }

        var labels = catalog.AnimationLabels(ship, skin, AnimationSet.Expression);
        var names = Directory.EnumerateFiles(folder, "*.png").Select(Path.GetFileNameWithoutExtension).OfType<string>()
            .OrderBy(n => int.TryParse(n, out var number) ? number : int.MaxValue).ThenBy(n => n, StringComparer.Ordinal);
        Faces.Clear();
        Faces.Add(new FaceOption("", "Default"));
        foreach (var name in names)
        {
            Faces.Add(new FaceOption(name, labels.TryGetValue(name, out var label) ? $"{name} · {label}" : name));
        }

        FaceFolder = folder;
        OnPropertyChanged(nameof(HasFaces));
        SelectedFace = Faces[0];
    }

    partial void OnSelectedFaceChanged(FaceOption? value)
    {
        Face = value is { Name.Length: > 0 } && FaceFolder is { } folder && File.Exists(Path.Combine(folder, value.Name + ".png"))
            ? new Bitmap(Path.Combine(folder, value.Name + ".png"))
            : null;
    }

    [RelayCommand]
    private async Task DownloadPaintingAsync()
    {
        if (SelectedSkin is not { } skin)
        {
            return;
        }

        PaintingState = PaintingState.Downloading;
        PaintingMessage = $"Downloading {skin.Painting}…";
        try
        {
            await Task.Run(() => Fetcher().FetchPaintingAsync(skin.Painting));
            if (SelectedSkin == skin)
            {
                ShowPainting(skin);
            }
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException
                                      or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            PaintingState = PaintingState.Failed;
            PaintingMessage = $"Couldn't download {skin.Painting}: {e.Message}";
        }
    }

    private AssetFetcher Fetcher() => new(Http, ClientRegion.En, settings.DataFolder);
}
