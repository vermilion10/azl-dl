using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using AzlDl.App.Settings;
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
    private LocalFileServer? server;

    public IReadOnlyList<StageOption> StageOptions { get; } =
    [
        new(StageMode.Painting, "Painting"),
        new(StageMode.Chibi, "Chibi"),
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

    // Loads the chibi or Live2D model for the selected skin into the viewer page, downloading it if needed.
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
        ViewerMessage = mode == StageMode.Chibi ? $"Getting the chibi for {skin.Name}…" : $"Getting the Live2D model for {skin.Name}…";
        var (category, bundle) = mode == StageMode.Chibi
            ? (AssetCategory.Azl, $"char/{skin.Painting}")
            : (AssetCategory.Live2D, $"live2d/{skin.Painting}");
        try
        {
            var folder = await Task.Run(() => Fetcher().FetchBundleAsync(category, bundle));
            if (SelectedSkin != skin || SelectedStage?.Mode != mode)
            {
                return;
            }

            if (folder is null)
            {
                ViewerState = ViewerState.Unavailable;
                ViewerMessage = mode == StageMode.Chibi ? $"{skin.Name} has no chibi." : $"{skin.Name} has no Live2D model.";
                return;
            }

            var query = mode == StageMode.Chibi
                ? $"kind=spine&skel=/art/{bundle}/{skin.Painting}.skel&atlas=/art/{bundle}/{skin.Painting}.atlas"
                : $"kind=live2d&model=/art/{bundle}/{skin.Painting}.model3.json";
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

    private LocalFileServer Server()
    {
        if (server is null || !string.Equals(server.Root, Path.GetFullPath(settings.DataFolder), StringComparison.OrdinalIgnoreCase))
        {
            server?.Dispose();
            server = new LocalFileServer(settings.DataFolder);
        }

        return server;
    }

    // The viewer page takes the current style's stage, ink and muted colors so it matches the window.
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

        return $"bg={Hex("StageBrush")}&ink={Hex("InkBrush")}&muted={Hex("MutedBrush")}";
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

        var path = Fetcher().PaintingPath(skin.Painting);
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
