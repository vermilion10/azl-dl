using AzlDl.Core.Versioning;

namespace AzlDl.Core.Download;

// The region's asset CDN. Hash lists live at android/hash/<raw version string>, assets at android/resource/<md5>.
public sealed class CdnClient(HttpClient http, ClientRegion region, string cacheDirectory)
{
    private const string Platform = "android";

    public string HashListUrl(AssetVersion version) => $"{region.CdnUrl}/{Platform}/hash/{version.Raw}";

    public string AssetUrl(string md5) => $"{region.CdnUrl}/{Platform}/resource/{md5}";

    // The newest hash list already cached for a category, for listing what exists without asking the server; empty
    // when none has been fetched yet.
    public IReadOnlyList<HashRow> CachedHashList(AssetCategory category)
    {
        var folder = Path.Combine(cacheDirectory, region.Name, "hash");
        var newest = Directory.Exists(folder)
            ? new DirectoryInfo(folder).GetFiles($"_{AssetVersion.ListName(category)}_*.csv").MaxBy(f => f.LastWriteTimeUtc)
            : null;
        return newest is null ? [] : HashList.Parse(File.ReadAllText(newest.FullName));
    }

    // Hash lists are immutable per version string, so a cached copy is reused instead of fetched again.
    public async Task<IReadOnlyList<HashRow>> GetHashListAsync(AssetVersion version, CancellationToken cancellationToken = default)
    {
        var cachePath = Path.Combine(cacheDirectory, region.Name, "hash", version.Raw.Replace('$', '_') + ".csv");
        if (!File.Exists(cachePath))
        {
            var text = await http.GetStringAsync(HashListUrl(version), cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var temporary = cachePath + ".part";
            await File.WriteAllTextAsync(temporary, text, cancellationToken);
            File.Move(temporary, cachePath, overwrite: true);
        }

        return HashList.Parse(await File.ReadAllTextAsync(cachePath, cancellationToken));
    }
}
