using System.Collections.ObjectModel;
using System.Globalization;
using AzlDl.App.Controls;
using AzlDl.App.Settings;
using AzlDl.Core;
using AzlDl.Core.Art;
using AzlDl.Core.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzlDl.App.ViewModels;

// One stat row of the detail pane: the value at +0 and at the chosen enhancement level.
public sealed record EquipmentStatRow(string Label, string Icon, string Base, string Value);

// The equipment gallery: every codex entry as a card, filters on the left and the selected piece's details on the right.
public sealed partial class EquipmentViewModel(AppSettings settings) : ObservableObject
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public const double CardWidth = 120;

    public const double CardSpacing = 10;

    private IReadOnlyList<EquipmentInfo> all = [];
    private readonly Dictionary<EquipmentInfo, EquipmentCard> cards = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsReady), nameof(HasProblem), nameof(HasFailed))]
    private LoadState state = LoadState.Loading;

    public bool IsLoading => State == LoadState.Loading;

    public bool IsReady => State == LoadState.Ready;

    public bool HasProblem => State is LoadState.MissingData or LoadState.Failed;

    // Reading data that is there went wrong, which is worth reporting; missing data only needs a download.
    public bool HasFailed => State == LoadState.Failed;

    [ObservableProperty]
    private string message = "";

    [ObservableProperty]
    private string filter = "";

    public ObservableCollection<FilterGroup> FilterGroups { get; } = [];

    public IReadOnlyList<SortOption<EquipmentInfo>> SortOptions { get; } =
    [
        new("Rarity, high to low", items => items.OrderByDescending(e => e.Rarity).ThenByDescending(e => e.Tech).ThenBy(e => e.Id)),
        new("Rarity, low to high", items => items.OrderBy(e => e.Rarity).ThenBy(e => e.Tech).ThenBy(e => e.Id)),
        new("Name", items => items.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenByDescending(e => e.Tech)),
        new("Type", items => items.OrderBy(e => e.TypeId).ThenByDescending(e => e.Rarity).ThenBy(e => e.Id)),
    ];

    [ObservableProperty]
    private SortOption<EquipmentInfo>? selectedSort;

    public ObservableCollection<EquipmentInfo> Shown { get; } = [];

    public ObservableCollection<EquipmentCardRow> CardRows { get; } = [];

    [ObservableProperty]
    private int columns = 6;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(MaxLevel), nameof(CanEnhance))]
    private EquipmentInfo? selectedEquipment;

    public bool HasSelection => SelectedEquipment is not null;

    // The enhancement level the stats are shown at, next to +0; it starts at the top level.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Stats), nameof(LevelTitle))]
    private int level;

    public int MaxLevel => SelectedEquipment?.MaxLevel ?? 0;

    public bool CanEnhance => MaxLevel > 0;

    public string LevelTitle => $"+{Level}";

    public IReadOnlyList<EquipmentStatRow> Stats => SelectedEquipment?.Stats
        .Select(s => new EquipmentStatRow(s.Label, s.Icon, s.Base, s.At(Level))).ToList() ?? [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadImages), nameof(ImageDownloadTitle))]
    private int imagesMissing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadImages))]
    private bool imagesBusy;

    [ObservableProperty]
    private string imageMessage = "";

    // Whether the game's icon backgrounds and tier badges (art/ui) are downloaded; without them cards are drawn.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadImages), nameof(ImageDownloadTitle))]
    private bool hasGameArt;

    public bool CanDownloadImages => (ImagesMissing > 0 || !HasGameArt) && !ImagesBusy;

    // Equipment icons are about 9 KB each.
    public string ImageDownloadTitle => ImagesMissing > 0
        ? $"Download {Count(ImagesMissing)} icons (about {Math.Max(1, ImagesMissing * 9 / 1024) + (HasGameArt ? 0 : 3)} MB)"
        : "Download the game's icon frames and stat icons (about 3 MB)";

    public bool NoMatches => IsReady && Shown.Count == 0;

    public string ShownTitle => Shown.Count == all.Count ? $"{Count(all.Count)} items" : $"{Count(Shown.Count)} of {Count(all.Count)} items";

    // The selected piece's card, for its icon in the detail pane.
    public EquipmentCard? SelectedCard => SelectedEquipment is { } e ? cards.GetValueOrDefault(e) : null;

    // Set by the shell; shows the Sync section.
    public Action? OpenSync { get; set; }

    [RelayCommand]
    private void GoToSync() => OpenSync?.Invoke();

    private static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public async Task LoadAsync()
    {
        State = LoadState.Loading;
        Message = "Reading equipment data…";
        var folder = settings.DataFolder;
        if (EquipmentCatalog.MissingFiles(folder).Count > 0)
        {
            all = [];
            Shown.Clear();
            CardRows.Clear();
            State = LoadState.MissingData;
            Message = $"There's no game data in {folder} yet. Update it from Sync, or pick a folder that already has it in Settings.";
            return;
        }

        try
        {
            all = (await Task.Run(() => EquipmentCatalog.Load(folder))).Equipment;
            BuildGallery();
            ApplyFilter();
            State = LoadState.Ready;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or FormatException)
        {
            State = LoadState.Failed;
            Message = $"Couldn't read the equipment data in {folder}: {e.Message}";
        }
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnSelectedSortChanged(SortOption<EquipmentInfo>? value) => ApplyFilter();

    partial void OnColumnsChanged(int value) => BuildRows();

    partial void OnSelectedEquipmentChanged(EquipmentInfo? oldValue, EquipmentInfo? newValue)
    {
        if (oldValue is not null && cards.TryGetValue(oldValue, out var old))
        {
            old.IsSelected = false;
        }

        if (newValue is not null && cards.TryGetValue(newValue, out var card))
        {
            card.IsSelected = true;
        }

        OnPropertyChanged(nameof(SelectedCard));
        Level = MaxLevel;
        OnPropertyChanged(nameof(Stats));
    }

    [RelayCommand]
    private void Select(EquipmentInfo? equipment) => SelectedEquipment = equipment;

    [RelayCommand]
    private void ClearFilters()
    {
        Filter = "";
        foreach (var group in FilterGroups)
        {
            group.Clear();
        }
    }

    private void BuildGallery()
    {
        var fetcher = Fetcher();
        cards.Clear();
        foreach (var item in all)
        {
            var path = fetcher.EquipmentImagePath(item.Icon);
            cards[item] = new EquipmentCard(item, item.Icon.Length > 0 && File.Exists(path) ? path : null);
        }

        ImagesMissing = MissingIcons().Count;
        GameArt.Use(fetcher.GameArtFolder);
        HasGameArt = fetcher.HasGameArt;
        SelectedSort ??= SortOptions[0];
        BuildFilters();
    }

    // Rebuilt after the game's art is downloaded so chips pick up their badges; picked chips stay picked.
    private void BuildFilters()
    {
        var picked = FilterGroups.SelectMany(g => g.Chips.Where(c => c.IsChecked).Select(c => (g.Title, c.Label))).ToHashSet();

        string[] names = ["Common", "Rare", "Elite", "Super Rare", "Ultra Rare"];
        string[] codes = ["N", "R", "SR", "SSR", "UR"];
        var rarities = all.GroupBy(e => e.Tier).OrderByDescending(g => g.Key)
            .Select(g => FilterChip.For<EquipmentInfo>(names[(int)g.Key], codes[(int)g.Key], e => e.Tier == g.Key, g.Key,
                $"rarity_{(int)g.Key + 1}"));
        var types = all.GroupBy(e => e.TypeName).OrderBy(g => g.Min(e => e.TypeId))
            .Select(g => FilterChip.For<EquipmentInfo>(g.Key, "", e => e.TypeName == g.Key));
        var techs = all.GroupBy(e => e.Tech).OrderByDescending(g => g.Key)
            .Select(g => FilterChip.For<EquipmentInfo>(g.Key == 0 ? "No tier" : $"Tier {g.Key}", $"T{g.Key}", e => e.Tech == g.Key,
                icon: g.Key > 0 ? $"tech_{g.Key}" : ""));
        var factions = all.GroupBy(e => e.NationName).OrderBy(g => g.Min(e => e.NationId))
            .Select(g => FilterChip.For<EquipmentInfo>(g.Key, "", e => e.NationName == g.Key, icon: $"nation_{g.Min(e => e.NationId)}"));

        FilterGroups.Clear();
        foreach (var (title, chips) in new[] { ("Rarity", rarities), ("Type", types), ("Tier", techs), ("Faction", factions) })
        {
            var group = new FilterGroup(title, chips, ApplyFilter);
            FilterGroups.Add(group);
            foreach (var chip in group.Chips.Where(c => picked.Contains((title, c.Label))))
            {
                chip.IsChecked = true;
            }
        }
    }

    private void ApplyFilter()
    {
        var shown = all.Where(e => (Filter.Length == 0 || e.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase))
                                   && FilterGroups.All(g => g.Matches(e)));
        Shown.Clear();
        foreach (var item in (SelectedSort ?? SortOptions[0]).Sort(shown))
        {
            Shown.Add(item);
        }

        BuildRows();
    }

    private void BuildRows()
    {
        CardRows.Clear();
        var columns = Math.Max(1, Columns);
        for (var i = 0; i < Shown.Count; i += columns)
        {
            CardRows.Add(new EquipmentCardRow(Shown.Skip(i).Take(columns).Select(e => cards[e]).ToList()));
        }

        OnPropertyChanged(nameof(ShownTitle));
        OnPropertyChanged(nameof(NoMatches));
    }

    // Icons are shared between tiers of the same gun, so each is downloaded once.
    private List<string> MissingIcons() =>
        cards.Values.Where(c => !c.HasImage && c.Equipment.Icon.Length > 0).Select(c => c.Equipment.Icon).Distinct().ToList();

    [RelayCommand]
    private async Task DownloadImagesAsync()
    {
        var icons = MissingIcons();
        if (icons.Count == 0 && HasGameArt)
        {
            return;
        }

        ImagesBusy = true;
        ImageMessage = $"Downloading {Count(icons.Count)} icons…";
        try
        {
            var fetcher = Fetcher();
            if (!HasGameArt)
            {
                await Task.Run(() => fetcher.FetchGameArtAsync());
                GameArt.Refresh();
                HasGameArt = fetcher.HasGameArt;
            }

            await Task.Run(() => fetcher.FetchImagesAsync(icons.Select(i => AssetFetcher.EquipmentImageFolder + i).ToList()));
            foreach (var card in cards.Values.Where(c => !c.HasImage))
            {
                var path = fetcher.EquipmentImagePath(card.Equipment.Icon);
                if (File.Exists(path))
                {
                    CardImages.Forget(path);
                    card.SetImagePath(path);
                }
            }

            ImagesMissing = MissingIcons().Count;
            // New row and chip objects bind again, now with the game's backgrounds and badges.
            BuildFilters();
            BuildRows();
            ImageMessage = ImagesMissing == 0 ? "" : $"The game has no icon for {Count(ImagesMissing)} items.";
            OnPropertyChanged(nameof(SelectedCard));
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException
                                      or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            ImageMessage = $"The icons didn't download: {e.Message}";
        }
        finally
        {
            ImagesBusy = false;
        }
    }

    private AssetFetcher Fetcher() => new(Http, ClientRegion.En, settings.DataFolder);
}
