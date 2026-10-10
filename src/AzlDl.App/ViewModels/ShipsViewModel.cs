using System.Collections.ObjectModel;
using Avalonia;
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
        var (what, bundle) = mode switch
        {
            StageMode.Chibi => ("chibi", $"char/{skin.Painting}"),
            StageMode.Dynamic => ("dynamic skin", $"{DynamicSkinReader.Folder}{skin.Painting}"),
            _ => ("Live2D model", $"live2d/{skin.Painting}"),
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
