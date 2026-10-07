using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using AzlDl.App.Settings;
using AzlDl.Core;
using AzlDl.Core.Art;
using AzlDl.Core.Data;
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

public enum PaintingState
{
    NotDownloaded,
    Downloading,
    Shown,
    Failed,
}

// The ship list and the selected ship's page.
public sealed partial class ShipsViewModel(AppSettings settings) : ObservableObject
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private IReadOnlyList<ShipInfo> all = [];
    private ShipCatalog? catalog;

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

    [ObservableProperty]
    private string paintingMessage = "";

    public ObservableCollection<ShipInfo> Ships { get; } = [];

    // The selected skin's voice lines (its own, with the default skin's for the rest).
    public ObservableCollection<VoiceLine> VoiceLines { get; } = [];

    public string DataFolder => settings.DataFolder;

    // Set by the shell; shows the Sync section.
    public Action? OpenSync { get; set; }

    [RelayCommand]
    private void GoToSync() => OpenSync?.Invoke();

    public async Task LoadAsync()
    {
        State = LoadState.Loading;
        Message = "Reading ship data…";
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

    // Posted so the skin list has the new ship's skins before one is selected; set earlier, the list would ignore it.
    partial void OnSelectedShipChanged(ShipInfo? value) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (SelectedShip == value)
            {
                SelectedSkin = value?.Skins.FirstOrDefault();
            }
        });

    partial void OnSelectedSkinChanged(SkinInfo? value)
    {
        // Swapping the skin list's items clears its selection; that null is ignored, the posted selection follows.
        if (value is null && SelectedShip?.Skins.Count > 0)
        {
            return;
        }

        ShowPainting(value);
    }

    private void ApplyFilter()
    {
        var keep = SelectedShip;
        Ships.Clear();
        foreach (var ship in all.Where(s => Filter.Length == 0
                                            || s.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                                            || s.EnglishName.Contains(Filter, StringComparison.OrdinalIgnoreCase)))
        {
            Ships.Add(ship);
        }

        if (keep is not null && Ships.Contains(keep))
        {
            SelectedShip = keep;
        }
    }

    private void ShowPainting(SkinInfo? skin)
    {
        VoiceLines.Clear();
        if (skin is not null && SelectedShip is { } ship && catalog is not null)
        {
            foreach (var line in catalog.VoiceLines(ship, skin))
            {
                VoiceLines.Add(line);
            }
        }

        Painting = null;
        if (skin is null)
        {
            PaintingState = PaintingState.NotDownloaded;
            return;
        }

        var path = Fetcher().PathFor(skin.Painting);
        if (File.Exists(path))
        {
            Painting = new Bitmap(path);
            PaintingState = PaintingState.Shown;
        }
        else
        {
            PaintingState = PaintingState.NotDownloaded;
            PaintingMessage = $"Painting {skin.Painting} isn't downloaded yet.";
        }
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
            await Task.Run(() => Fetcher().FetchAsync(skin.Painting));
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

    private PaintingFetcher Fetcher() => new(Http, ClientRegion.En, settings.DataFolder);
}
