using AzlDl.Core.Download;
using AzlDl.Core.Gate;
using AzlDl.Core.Versioning;

namespace AzlDl.Core.Art;

// Gets one skin's painting onto disk: downloads painting/<name> and painting/<name>_tex if needed, then extracts the
// image to <data>/art/painting/<name>.png. Uses the same folder layout as the command-line tool.
public sealed class PaintingFetcher(HttpClient http, ClientRegion region, string dataFolder)
{
    public string AssetFolder => Path.Combine(dataFolder, "AssetBundles");

    public string ArtFolder => Path.Combine(dataFolder, "art");

    public string PathFor(string painting) => Path.Combine(ArtFolder, "painting", painting + ".png");

    public async Task<string> FetchAsync(string painting, CancellationToken cancellationToken = default)
    {
        var target = PathFor(painting);
        if (File.Exists(target))
        {
            return target;
        }

        var reply = await GateClient.QueryVersionsAsync(region, cancellationToken: cancellationToken);
        var version = reply.Versions.Select(AssetVersion.TryParse).FirstOrDefault(v => v?.Category == AssetCategory.Painting)
                      ?? throw new InvalidDataException("The gate didn't report a painting version.");

        var cdn = new CdnClient(http, region, Path.Combine(dataFolder, "cache"));
        var rows = await cdn.GetHashListAsync(version, cancellationToken);
        var wanted = new HashSet<string>(StringComparer.Ordinal) { $"painting/{painting}", $"painting/{painting}_tex" };
        var selected = rows.Where(r => wanted.Contains(r.Path)).ToList();
        if (selected.Count == 0)
        {
            throw new FileNotFoundException($"The game has no painting named {painting}.");
        }

        var summary = await new AssetDownloader(http, cdn).DownloadAsync(selected, new DownloadOptions(AssetFolder), cancellationToken: cancellationToken);
        if (summary.Failed > 0)
        {
            throw new IOException($"{summary.Failed} of the painting's files didn't download.");
        }

        var bundle = selected.Any(r => r.Path.EndsWith("_tex", StringComparison.Ordinal)) ? $"painting/{painting}_tex" : $"painting/{painting}";
        var result = new ArtExtractor(AssetFolder, ArtFolder).Extract([bundle], 1);
        if (result.Errors.Count > 0)
        {
            throw new InvalidDataException(result.Errors[0]);
        }

        return File.Exists(target) ? target : throw new FileNotFoundException($"Extracting {bundle} produced no painting.");
    }
}
