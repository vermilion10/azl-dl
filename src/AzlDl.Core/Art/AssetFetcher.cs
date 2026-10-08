using AzlDl.Core.Download;
using AzlDl.Core.Gate;
using AzlDl.Core.Versioning;

namespace AzlDl.Core.Art;

// Gets single assets onto disk on demand: downloads a bundle (and a painting's _tex partner) when it isn't current,
// then extracts it under <data>/art. Uses the same folder layout as the command-line tool.
public sealed class AssetFetcher(HttpClient http, ClientRegion region, string dataFolder)
{
    public string AssetFolder => Path.Combine(dataFolder, "AssetBundles");

    public string ArtFolder => Path.Combine(dataFolder, "art");

    public string PaintingPath(string painting) => Path.Combine(ArtFolder, "painting", painting + ".png");

    // Folder an extracted bundle's files land in (art/char/qiye_5, art/live2d/qiye_7, ...).
    public string FolderFor(string bundlePath) => Path.Combine(ArtFolder, bundlePath.Replace('/', Path.DirectorySeparatorChar));

    public async Task<string> FetchPaintingAsync(string painting, CancellationToken cancellationToken = default)
    {
        var target = PaintingPath(painting);
        if (File.Exists(target))
        {
            return target;
        }

        var found = await FetchAsync(AssetCategory.Painting, [$"painting/{painting}", $"painting/{painting}_tex"], cancellationToken);
        if (found.Count == 0)
        {
            throw new FileNotFoundException($"The game has no painting named {painting}.");
        }

        return File.Exists(target) ? target : throw new FileNotFoundException($"Extracting {painting} produced no painting.");
    }

    // Returns the extracted folder, or null when the game has no such bundle (a skin without a Live2D model, say).
    public async Task<string?> FetchBundleAsync(AssetCategory category, string bundlePath, CancellationToken cancellationToken = default)
    {
        var folder = FolderFor(bundlePath);
        if (Directory.Exists(folder) && Directory.EnumerateFiles(folder).Any())
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
        var prefab = DynamicSkinReader.Folder + painting;
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
        CancellationToken cancellationToken) =>
        FetchAsync(category, _ => paths, cancellationToken);

    private async Task<IReadOnlyList<HashRow>> FetchAsync(AssetCategory category,
        Func<IReadOnlyList<HashRow>, IReadOnlyCollection<string>> choose, CancellationToken cancellationToken)
    {
        var reply = await GateClient.QueryVersionsAsync(region, cancellationToken: cancellationToken);
        var version = reply.Versions.Select(AssetVersion.TryParse).FirstOrDefault(v => v?.Category == category)
                      ?? throw new InvalidDataException($"The gate didn't report a {category} version.");

        var cdn = new CdnClient(http, region, Path.Combine(dataFolder, "cache"));
        var all = await cdn.GetHashListAsync(version, cancellationToken);
        var wanted = new HashSet<string>(choose(all), StringComparer.Ordinal);
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

        // A painting is extracted from its _tex bundle when it has one; everything else from the bundle itself.
        var toExtract = rows.Select(r => r.Path)
            .Where(p => !(p.StartsWith("painting/", StringComparison.Ordinal) && !p.EndsWith("_tex", StringComparison.Ordinal)
                          && wanted.Contains(p + "_tex") && rows.Any(r => r.Path == p + "_tex")))
            .ToList();
        var result = new ArtExtractor(AssetFolder, ArtFolder).Extract(toExtract, 1);
        if (result.Errors.Count > 0)
        {
            throw new InvalidDataException(result.Errors[0]);
        }

        return rows;
    }
}
