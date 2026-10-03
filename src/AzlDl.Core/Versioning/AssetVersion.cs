namespace AzlDl.Core.Versioning;

// Asset categories the gate reports, keyed by the name in its version strings. Each has its own hash list.
public enum AssetCategory
{
    Azl,
    Cv,
    Live2D,
    Pic,
    Bgm,
    Cipher,
    Manga,
    Painting,
    Dorm,
    Map,
}

// One gate version string, e.g. "$azhash$9$3$125$<hash>" (main client) or "$cvhash$412$<hash>".
// The whole raw string is the hash list's file name on the CDN.
public sealed record AssetVersion(AssetCategory Category, string Version, string Hash, string Raw)
{
    private static readonly Dictionary<string, AssetCategory> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["azhash"] = AssetCategory.Azl,
        ["cvhash"] = AssetCategory.Cv,
        ["l2dhash"] = AssetCategory.Live2D,
        ["pichash"] = AssetCategory.Pic,
        ["bgmhash"] = AssetCategory.Bgm,
        ["cipherhash"] = AssetCategory.Cipher,
        ["mangahash"] = AssetCategory.Manga,
        ["paintinghash"] = AssetCategory.Painting,
        ["dormhash"] = AssetCategory.Dorm,
        ["maphash"] = AssetCategory.Map,
    };

    // Returns null for strings that aren't asset versions (the gate sends a few other entries) or unknown categories.
    public static AssetVersion? TryParse(string raw)
    {
        var parts = raw.Split('$');
        if (parts.Length < 4 || parts[0].Length != 0 || !Names.TryGetValue(parts[1], out var category))
        {
            return null;
        }

        var version = string.Join('.', parts[2..^1]);
        return new AssetVersion(category, version, parts[^1], raw);
    }

    public static AssetCategory? ParseCategory(string name) =>
        Enum.TryParse<AssetCategory>(name, ignoreCase: true, out var category) ? category : null;
}
