using System.Globalization;

namespace AzlDl.Core.Versioning;

// One asset in a hash list: its bundle path under AssetBundles/, its size, and the MD5 the CDN serves it under.
public sealed record HashRow(string Path, long Size, string Md5)
{
    // First path segment ("painting", "char", ...), or "" for files at the root.
    public string TopDirectory => Path.IndexOf('/') is var slash and > 0 ? Path[..slash] : "";
}

// Hash lists are CSV with one "path,size,md5" row per line.
public static class HashList
{
    public static IReadOnlyList<HashRow> Parse(string text)
    {
        var rows = new List<HashRow>();
        var lineNumber = 0;
        foreach (var line in text.Split('\n'))
        {
            lineNumber++;
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0)
            {
                continue;
            }

            var fields = trimmed.Split(',');
            if (fields.Length != 3 || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
            {
                throw new InvalidDataException($"Hash list line {lineNumber} isn't 'path,size,md5': {trimmed}");
            }

            rows.Add(new HashRow(fields[0], size, fields[2]));
        }

        return rows;
    }
}
