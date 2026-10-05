using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace AzlDl.Core.Unity;

public sealed record InspectedAsset(string File, long PathId, string Type, string Name);

public sealed record BundleReport(string UnityVersion, IReadOnlyList<string> Files, IReadOnlyList<InspectedAsset> Assets);

// Opens a UnityFS bundle and lists every object in its serialized files, by type and m_Name.
public static class BundleInspector
{
    private static readonly byte[] Signature = "UnityFS"u8.ToArray();

    public static bool IsUnityFs(string path)
    {
        Span<byte> head = stackalloc byte[7];
        using var stream = File.OpenRead(path);
        return stream.Read(head) == head.Length && head.SequenceEqual(Signature);
    }

    public static bool IsUnityFsBytes(ReadOnlySpan<byte> data) => data.StartsWith(Signature);

    public static BundleReport Inspect(string path)
    {
        var manager = new AssetsManager();
        try
        {
            var bundle = manager.LoadBundleFile(path, unpackIfPacked: true);
            var files = new List<string>();
            var assets = new List<InspectedAsset>();

            var directory = bundle.file.BlockAndDirInfo.DirectoryInfos;
            for (var index = 0; index < directory.Count; index++)
            {
                files.Add(directory[index].Name);
                if (!bundle.file.IsAssetsFile(index))
                {
                    continue;
                }

                var instance = manager.LoadAssetsFileFromBundle(bundle, index, loadDeps: false);
                foreach (var info in instance.file.AssetInfos)
                {
                    var type = ((AssetClassID)info.TypeId).ToString();
                    var name = "";
                    try
                    {
                        var field = manager.GetBaseField(instance, info, AssetReadFlags.None);
                        if (field is not null && !field.IsDummy && !field["m_Name"].IsDummy)
                        {
                            name = field["m_Name"].AsString;
                        }
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        name = $"<unreadable: {e.Message}>";
                    }

                    assets.Add(new InspectedAsset(directory[index].Name, info.PathId, type, name));
                }
            }

            return new BundleReport(bundle.file.Header.EngineVersion, files, assets);
        }
        finally
        {
            manager.UnloadAll(true);
        }
    }

    // Writes the field tree of every asset whose m_Name matches; returns how many matched.
    public static int Dump(string path, string assetName, TextWriter output)
    {
        var manager = new AssetsManager();
        try
        {
            var bundle = manager.LoadBundleFile(path, unpackIfPacked: true);
            var matched = 0;
            for (var index = 0; index < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; index++)
            {
                if (!bundle.file.IsAssetsFile(index))
                {
                    continue;
                }

                var instance = manager.LoadAssetsFileFromBundle(bundle, index, loadDeps: false);
                foreach (var info in instance.file.AssetInfos)
                {
                    var field = manager.GetBaseField(instance, info, AssetReadFlags.None);
                    if (field is null || field["m_Name"].IsDummy || field["m_Name"].AsString != assetName)
                    {
                        continue;
                    }

                    output.WriteLine($"# {(AssetClassID)info.TypeId} {info.PathId}");
                    FieldDump.Write(output, field);
                    matched++;
                }
            }

            return matched;
        }
        finally
        {
            manager.UnloadAll(true);
        }
    }
}
