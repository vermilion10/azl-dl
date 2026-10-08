using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AzlDl.App.Settings;
using AzlDl.Core.Art;
using AzlDl.Core.Download;
using AzlDl.Core.Unity;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzlDl.App.ViewModels;

public sealed record BundleFolder(string Name, int Count)
{
    public override string ToString() => $"{Name} ({Count})";
}

public sealed record BundleEntry(string Path, string Name, long Size)
{
    public string SizeText => Format.Size(Size);
}

// Browses the bundles on disk: the folders under AssetBundles, the bundles in one folder, the objects in one bundle,
// and a preview of one object that can be saved. A bundle can also be extracted whole into the art folder, the same
// way the command-line extract does it.
public sealed partial class AssetsViewModel(AppSettings settings) : ObservableObject
{
    private List<BundleEntry> all = [];
    private int bundleRequest;
    private int previewRequest;

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
    private BundleFolder? selectedFolder;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExtractBundleCommand))]
    private BundleEntry? selectedBundle;

    [ObservableProperty]
    private string bundleMessage = "Pick a bundle to list what's inside.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAssetCommand))]
    private InspectedAsset? selectedAsset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsImage), nameof(ShowsText), nameof(HasPreview))]
    [NotifyCanExecuteChangedFor(nameof(SaveAssetCommand))]
    private AssetPreview? preview;

    [ObservableProperty]
    private Bitmap? previewImage;

    [ObservableProperty]
    private string previewMessage = "";

    [ObservableProperty]
    private bool previewBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExtractBundleCommand))]
    private bool extracting;

    [ObservableProperty]
    private string actionMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultFolder))]
    private string? resultFolder;

    public bool HasResultFolder => ResultFolder is not null;

    public bool HasPreview => Preview is not null;

    public bool ShowsImage => Preview?.Kind == PreviewKind.Image;

    public bool ShowsText => Preview is { Kind: not PreviewKind.Image };

    public ObservableCollection<BundleFolder> Folders { get; } = [];

    public ObservableCollection<BundleEntry> Bundles { get; } = [];

    public string BundleCountText => Bundles.Count == 1 ? "1 bundle" : $"{Format.Count(Bundles.Count)} bundles";

    public ObservableCollection<InspectedAsset> Assets { get; } = [];

    public Action? OpenSync { get; set; }

    private string AssetFolder => Path.Combine(settings.DataFolder, "AssetBundles");

    private string ArtFolder => Path.Combine(settings.DataFolder, "art");

    [RelayCommand]
    private void GoToSync() => OpenSync?.Invoke();

    public async Task LoadAsync()
    {
        State = LoadState.Loading;
        Message = "Listing downloaded bundles…";
        var root = AssetFolder;
        try
        {
            all = await Task.Run(() => ListBundles(root));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            State = LoadState.Failed;
            Message = $"Couldn't list {root}: {e.Message}";
            return;
        }

        if (all.Count == 0)
        {
            State = LoadState.MissingData;
            Message = $"No bundles are downloaded in {root} yet. Download a category from Sync, or open a painting or chibi on a ship page.";
            return;
        }

        Folders.Clear();
        foreach (var group in all.GroupBy(b => FolderOf(b.Path)).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Folders.Add(new BundleFolder(group.Key, group.Count()));
        }

        SelectedFolder = Folders.FirstOrDefault(f => f.Name == SelectedFolder?.Name) ?? Folders[0];
        State = LoadState.Ready;
    }

    // Every file under AssetBundles except the downloader's own bookkeeping.
    private static List<BundleEntry> ListBundles(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".part", StringComparison.Ordinal) && !f.EndsWith(".tmp", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(p => p != AssetDownloader.StateFileName)
            .Select(p => new BundleEntry(p, p[(p.LastIndexOf('/') + 1)..], new FileInfo(Path.Combine(root, p)).Length))
            .OrderBy(b => b.Path, StringComparer.Ordinal)
            .ToList();
    }

    private static string FolderOf(string path) => path.Contains('/') ? path[..path.IndexOf('/')] : "(top level)";

    partial void OnSelectedFolderChanged(BundleFolder? value) => ApplyFilter();

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var keep = SelectedBundle;
        Bundles.Clear();
        foreach (var bundle in all.Where(b => FolderOf(b.Path) == SelectedFolder?.Name
                                              && b.Name.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            Bundles.Add(bundle);
        }

        OnPropertyChanged(nameof(BundleCountText));
        if (keep is not null && Bundles.Contains(keep))
        {
            SelectedBundle = keep;
        }
    }

    partial void OnSelectedBundleChanged(BundleEntry? value)
    {
        ActionMessage = "";
        ResultFolder = null;
        if (value is null)
        {
            return;
        }

        _ = InspectAsync(value);
    }

    private async Task InspectAsync(BundleEntry bundle)
    {
        var request = ++bundleRequest;
        Assets.Clear();
        ClearPreview("");
        BundleMessage = $"Reading {bundle.Name}…";
        var path = Path.Combine(AssetFolder, bundle.Path);

        BundleReport report;
        try
        {
            report = await Task.Run(() => BundleInspector.CanOpen(path)
                ? BundleInspector.Inspect(path)
                : throw new InvalidDataException("This file isn't a Unity bundle."));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (request == bundleRequest)
            {
                BundleMessage = $"Couldn't read {bundle.Name}: {e.Message}";
            }

            return;
        }

        if (request != bundleRequest)
        {
            return;
        }

        foreach (var asset in report.Assets.OrderBy(a => a.Type, StringComparer.Ordinal).ThenBy(a => a.Name, StringComparer.Ordinal))
        {
            Assets.Add(asset);
        }

        BundleMessage = $"{Format.Count(report.Assets.Count)} objects · Unity {report.UnityVersion}";

        // Open on the first picture when there is one, since that's usually what the bundle is for.
        var first = Assets.FirstOrDefault(a => a.Type == "Texture2D");
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (request == bundleRequest && SelectedAsset is null)
            {
                SelectedAsset = first;
            }
        });
    }

    partial void OnSelectedAssetChanged(InspectedAsset? value)
    {
        if (value is null || SelectedBundle is null)
        {
            ClearPreview(Assets.Count > 0 ? "Pick an object to preview it." : "");
            return;
        }

        _ = PreviewAsync(SelectedBundle, value);
    }

    private async Task PreviewAsync(BundleEntry bundle, InspectedAsset asset)
    {
        var request = ++previewRequest;
        ClearPreview($"Reading {asset.Name}…");
        PreviewBusy = true;
        var path = Path.Combine(AssetFolder, bundle.Path);

        try
        {
            var read = await Task.Run(() =>
            {
                var result = AssetPreviewer.Read(path, asset);
                return (result, Bitmap: result.Image is null ? null : ToBitmap(result.Image));
            });

            if (request != previewRequest)
            {
                read.Bitmap?.Dispose();
                return;
            }

            Preview = read.result;
            PreviewImage = read.Bitmap;
            PreviewMessage = read.result.Kind switch
            {
                PreviewKind.Image when Assets.Any(a => a.Type == "Mesh" && a.Name == asset.Name + "-mesh") =>
                    $"{read.result.Text} pixels. This is the painting cut into tiles; Extract bundle puts it back together.",
                PreviewKind.Image => $"{read.result.Text} pixels",
                PreviewKind.Text => $"Text, {Format.Size(read.result.Bytes.Length)}",
                PreviewKind.Binary => $"Binary, {Format.Size(read.result.Bytes.Length)}. The first bytes are shown.",
                _ => "Field values. Long arrays are cut short here and in the saved file.",
            };
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (request == previewRequest)
            {
                PreviewMessage = $"Couldn't read {asset.Name}: {e.Message}";
            }
        }
        finally
        {
            if (request == previewRequest)
            {
                PreviewBusy = false;
            }
        }
    }

    private void ClearPreview(string text)
    {
        var old = PreviewImage;
        PreviewImage = null;
        old?.Dispose();
        Preview = null;
        PreviewMessage = text;
        PreviewBusy = false;
    }

    // RgbaImage keeps the bottom row first; the bitmap wants the top row first.
    private static Bitmap ToBitmap(RgbaImage image)
    {
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96),
            PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using var buffer = bitmap.Lock();
        var stride = image.Width * 4;
        for (var row = 0; row < image.Height; row++)
        {
            System.Runtime.InteropServices.Marshal.Copy(image.Pixels, (image.Height - 1 - row) * stride,
                buffer.Address + (row * buffer.RowBytes), stride);
        }

        return bitmap;
    }

    public string SuggestedFileName => Preview?.FileName ?? "asset";

    private bool CanSaveAsset() => Preview is not null;

    // The view asks for the target file and passes its path here.
    [RelayCommand(CanExecute = nameof(CanSaveAsset))]
    private async Task SaveAssetAsync(string path)
    {
        if (Preview is not { } current)
        {
            return;
        }

        try
        {
            await Task.Run(() => current.Save(path));
            ActionMessage = $"Saved {Path.GetFileName(path)}.";
            ResultFolder = Path.GetDirectoryName(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ActionMessage = $"Couldn't save {Path.GetFileName(path)}: {e.Message}";
            ResultFolder = null;
        }
    }

    private bool CanExtractBundle() => SelectedBundle is not null && !Extracting
                                       && BundleInspector.IsUnityFs(Path.Combine(AssetFolder, SelectedBundle.Path));

    // Extracts every picture and text file in the bundle (paintings reassembled) into the art folder.
    [RelayCommand(CanExecute = nameof(CanExtractBundle))]
    private async Task ExtractBundleAsync()
    {
        if (SelectedBundle is not { } bundle)
        {
            return;
        }

        Extracting = true;
        ActionMessage = $"Extracting {bundle.Name}…";
        ResultFolder = null;
        try
        {
            var summary = await Task.Run(() => new ArtExtractor(AssetFolder, ArtFolder).Extract([bundle.Path], 1));
            var written = summary.Images + summary.Paintings + summary.Files;
            if (summary.Errors.Count > 0)
            {
                ActionMessage = $"Couldn't extract {bundle.Name}: {summary.Errors[0]}";
            }
            else if (written == 0)
            {
                ActionMessage = $"{bundle.Name} has no pictures or text files to extract.";
            }
            else
            {
                var folder = bundle.Path.StartsWith("painting/", StringComparison.Ordinal)
                    ? Path.Combine(ArtFolder, "painting")
                    : Path.Combine(ArtFolder, bundle.Path.Replace('/', Path.DirectorySeparatorChar));
                ActionMessage = $"Extracted {Format.Count(written)} files.";
                ResultFolder = folder;
            }
        }
        finally
        {
            Extracting = false;
        }
    }
}
