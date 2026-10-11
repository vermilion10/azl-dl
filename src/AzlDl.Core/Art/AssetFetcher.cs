using AzlDl.Core.Download;
using AzlDl.Core.Gate;
using AzlDl.Core.Versioning;

namespace AzlDl.Core.Art;

// Gets single assets onto disk on demand: downloads a bundle (and a painting's _tex partner) when it isn't current,
// then extracts it under <data>/art. Uses the same folder layout as the command-line tool.
// Bundle paths on the CDN are all lower case, while some painting names aren't (U101, HDN101), so paths are lowered
// before they are looked up or used as folders.
public sealed class AssetFetcher(HttpClient http, ClientRegion region, string dataFolder)
{
    public string AssetFolder => Path.Combine(dataFolder, "AssetBundles");

    public string ArtFolder => Path.Combine(dataFolder, "art");

    public string PaintingPath(string painting) => Path.Combine(ArtFolder, "painting", painting + ".png");

    // Folder an extracted bundle's files land in (art/char/qiye_5, art/live2d/qiye_7, ...).
    public string FolderFor(string bundlePath) =>
        Path.Combine(ArtFolder, bundlePath.ToLowerInvariant().Replace('/', Path.DirectorySeparatorChar));

    public async Task<string> FetchPaintingAsync(string painting, CancellationToken cancellationToken = default)
    {
        var target = PaintingPath(painting);
        if (IsPaintingCurrent(painting))
        {
            return target;
        }

        // A layered painting's prefab names its other layers, which are downloaded before the painting is extracted.
        var bundle = $"painting/{painting.ToLowerInvariant()}";
        var found = await FetchAsync(AssetCategory.Painting, [bundle, bundle + "_tex"], cancellationToken, extract: false);
        if (found.Count == 0)
        {
            throw new FileNotFoundException($"The game has no painting named {painting}.");
        }

        var prefab = Path.Combine(AssetFolder, "painting", painting.ToLowerInvariant());
        var layers = File.Exists(prefab) ? PaintingComposer.ReadLayers(prefab).Where(l => l.Name != painting).ToList() : [];
        if (layers.Count > 0)
        {
            await FetchAsync(AssetCategory.Painting, layers.Select(l => ArtExtractor.LayerBundle(l.Name)).ToList(), cancellationToken,
                extract: false);
        }

        var source = found.Any(r => r.Path == bundle + "_tex") ? bundle + "_tex" : bundle;
        Extract([source]);
        return File.Exists(target) ? target : throw new FileNotFoundException($"Extracting {painting} produced no painting.");
    }

    // False when the painting isn't on disk, or was extracted without its prefab's layout (before layers were stacked
    // and faces placed). Such paintings are extracted again, which also downloads the prefab and any layers.
    public bool IsPaintingCurrent(string painting)
    {
        var target = PaintingPath(painting);
        return File.Exists(target) && File.Exists(PaintingComposer.LayoutPath(target));
    }

    // Live2D models extracted before motions were converted have no motions folder.
    private static bool NeedsMotions(string bundlePath, string folder) =>
        bundlePath.StartsWith("live2d/", StringComparison.Ordinal) && !Directory.Exists(Path.Combine(folder, ArtExtractor.MotionFolder));

    // Downloads a ship group's voice banks (cue/cv-<group>.b and its -battle and -gift banks) when they aren't current.
    // Returns how many banks the game has for the group.
    public async Task<int> FetchVoiceBanksAsync(long shipGroup, CancellationToken cancellationToken = default)
    {
        var found = await FetchAsync(AssetCategory.Cv, AzlDl.Core.Audio.VoiceLibrary.BankPaths(shipGroup), cancellationToken, extract: false);
        return found.Count;
    }

    public string FaceFolder(string painting) => FolderFor("paintingface/" + painting);

    // Downloads a painting's expression faces. Returns the folder, or null when the painting has none. A painting
    // without faces gets an empty folder, so the game's servers aren't asked again.
    public async Task<string?> FetchFacesAsync(string painting, CancellationToken cancellationToken = default)
    {
        var folder = FaceFolder(painting);
        if (Directory.Exists(folder))
        {
            return Directory.EnumerateFiles(folder, "*.png").Any() ? folder : null;
        }

        var found = await FetchBundleAsync(AssetCategory.Azl, "paintingface/" + painting, cancellationToken);
        if (found is null)
        {
            Directory.CreateDirectory(folder);
        }

        return found;
    }

    // The card image of a skin (shipyardicon/<painting>), or of a piece of equipment (equips/<icon>), once extracted.
    public string ShipImagePath(string painting) => ImageIn(FolderFor(ShipImageFolder + painting), painting);

    public string EquipmentImagePath(string icon) => ImageIn(FolderFor(EquipmentImageFolder + icon), icon);

    // An icon bundle holds one texture, usually named like the bundle but not always in the same case.
    private static string ImageIn(string folder, string name)
    {
        var expected = Path.Combine(folder, name + ".png");
        return File.Exists(expected) || !Directory.Exists(folder)
            ? expected
            : Directory.EnumerateFiles(folder, "*.png").FirstOrDefault() ?? expected;
    }

    // Chibi heads used as avatars in Juustagram and Fleet Chat.
    public string AvatarPath(string name) => ImageIn(FolderFor(AvatarFolder + name), name);

    public const string AvatarFolder = "qicon/";

    // Juustagram pictures aren't in bundles; the game loads them from its own image host, so only that host is used.
    public const string PictureHost = "blhxusstatic.yo-star.com";

    public string? PicturePath(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != PictureHost)
        {
            return null;
        }

        var name = string.Concat(Uri.UnescapeDataString(uri.Segments[^1]).Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_'));
        return name.Length == 0 ? null : Path.Combine(dataFolder, "juustagram", name);
    }

    // Downloads a Juustagram picture once. Returns null for addresses outside the game's image host.
    public async Task<string?> FetchPictureAsync(string url, CancellationToken cancellationToken = default)
    {
        if (PicturePath(url) is not { } path)
        {
            return null;
        }

        if (!File.Exists(path))
        {
            var bytes = await http.GetByteArrayAsync(url, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var partial = path + ".part";
            await File.WriteAllBytesAsync(partial, bytes, cancellationToken);
            File.Move(partial, path, overwrite: true);
        }

        return path;
    }

    public const string ShipImageFolder = "shipyardicon/";

    public const string EquipmentImageFolder = "equips/";

    // Downloads and extracts the given card images (bundle paths such as shipyardicon/qiye) that aren't on disk yet.
    // Names the game doesn't have are skipped. Returns how many were fetched.
    public async Task<int> FetchImagesAsync(IReadOnlyCollection<string> bundlePaths, CancellationToken cancellationToken = default)
    {
        var missing = bundlePaths.Where(b => !Directory.Exists(FolderFor(b)) || !Directory.EnumerateFiles(FolderFor(b), "*.png").Any())
            .ToList();
        if (missing.Count == 0)
        {
            return 0;
        }

        var found = await FetchAsync(AssetCategory.Azl, missing, cancellationToken, extract: false);
        var result = new ArtExtractor(AssetFolder, ArtFolder).Extract(found.Select(r => r.Path), Environment.ProcessorCount);
        if (result.Errors.Count > 0)
        {
            throw new InvalidDataException(result.Errors[0]);
        }

        return found.Count;
    }

    // Returns the extracted folder, or null when the game has no such bundle (a skin without a Live2D model, say).
    public async Task<string?> FetchBundleAsync(AssetCategory category, string bundlePath, CancellationToken cancellationToken = default)
    {
        var folder = FolderFor(bundlePath);
        if (Directory.Exists(folder) && Directory.EnumerateFiles(folder).Any() && !NeedsMotions(bundlePath, folder))
        {
            return folder;
        }

        var found = await FetchAsync(category, [bundlePath], cancellationToken);
        return found.Count == 0 ? null : folder;
    }

    // Returns the folder holding layers.json, or null when the skin isn't dynamic. Downloads the prefab and the first
    // resource bundle the game has for it (spinepainting/<name>_res, or a shorter name's for variants).
    public async Task<string?> FetchDynamicSkinAsync(string painting, CancellationToken cancellationToken = default)
    {
        var prefab = DynamicSkinReader.Folder + painting.ToLowerInvariant();
        var folder = FolderFor(prefab);
        if (File.Exists(Path.Combine(folder, DynamicSkinReader.LayoutFileName)))
        {
            return folder;
        }

        var found = await FetchAsync(AssetCategory.Azl, rows =>
        {
            var paths = rows.Select(r => r.Path).ToHashSet(StringComparer.Ordinal);
            var resources = DynamicSkinReader.ResourceCandidates(prefab).FirstOrDefault(paths.Contains);
            return paths.Contains(prefab) && resources is not null ? [prefab, resources] : [];
        }, cancellationToken);

        if (found.Count == 0)
        {
            return null;
        }

        return File.Exists(Path.Combine(folder, DynamicSkinReader.LayoutFileName))
            ? folder
            : throw new InvalidDataException($"{prefab} has no skeletons to show.");
    }

    private Task<IReadOnlyList<HashRow>> FetchAsync(AssetCategory category, IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken, bool extract = true) =>
        FetchAsync(category, _ => paths, cancellationToken, extract);

    private async Task<IReadOnlyList<HashRow>> FetchAsync(AssetCategory category,
        Func<IReadOnlyList<HashRow>, IReadOnlyCollection<string>> choose, CancellationToken cancellationToken, bool extract = true)
    {
        var reply = await GateClient.QueryVersionsAsync(region, cancellationToken: cancellationToken);
        var version = reply.Versions.Select(AssetVersion.TryParse).FirstOrDefault(v => v?.Category == category)
                      ?? throw new InvalidDataException($"The gate didn't report a {category} version.");

        var cdn = new CdnClient(http, region, Path.Combine(dataFolder, "cache"));
        var all = await cdn.GetHashListAsync(version, cancellationToken);
        var wanted = new HashSet<string>(choose(all).Select(p => p.ToLowerInvariant()), StringComparer.Ordinal);
        var rows = all.Where(r => wanted.Contains(r.Path)).ToList();
        if (rows.Count == 0)
        {
            return rows;
        }

        var summary = await new AssetDownloader(http, cdn).DownloadAsync(rows, new DownloadOptions(AssetFolder), cancellationToken: cancellationToken);
        if (summary.Failed > 0)
        {
            throw new IOException($"{summary.Failed} of the files didn't download.");
        }

        if (extract)
        {
            Extract(rows.Select(r => r.Path).ToList());
        }

        return rows;
    }

    private void Extract(IReadOnlyList<string> bundlePaths)
    {
        var result = new ArtExtractor(AssetFolder, ArtFolder).Extract(bundlePaths, 1);
        if (result.Errors.Count > 0)
        {
            throw new InvalidDataException(result.Errors[0]);
        }
    }
}
