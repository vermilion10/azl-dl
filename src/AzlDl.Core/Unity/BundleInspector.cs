using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AzlDl.Core.Scripts;

namespace AzlDl.Core.Unity;

public sealed record InspectedAsset(string File, long PathId, string Type, string Name);

public sealed record BundleReport(string UnityVersion, IReadOnlyList<string> Files, IReadOnlyList<InspectedAsset> Assets);

// Opens a UnityFS bundle and lists every object in its serialized files, by type and m_Name. Encrypted scripts bundles
// are decrypted in memory first.
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

    // True for a file the inspector can open: a plain bundle or an encrypted scripts bundle.
    public static bool CanOpen(string path)
    {
        if (IsUnityFs(path))
        {
            return true;
        }

        Span<byte> head = stackalloc byte[16];
        using var stream = File.OpenRead(path);
        return stream.Read(head) == head.Length && ScriptBundleCipher.LooksEncrypted(head);
    }

    internal static BundleFileInstance Open(AssetsManager manager, string path)
    {
        if (IsUnityFs(path))
        {
            return manager.LoadBundleFile(path, unpackIfPacked: true);
        }

        var data = File.ReadAllBytes(path);
        if (!ScriptBundleCipher.LooksEncrypted(data))
        {
            throw new InvalidDataException("Not a Unity bundle.");
        }

        return manager.LoadBundleFile(new MemoryStream(ScriptBundleCipher.Decrypt(data)), path, unpackIfPacked: true);
    }

    // Whether a bundle entry is a serialized file (Unity flags those with 4 in the bundle directory). AssetsTools'
    // IsAssetsFile guesses from the first bytes instead, and some raw texture blobs (.resS) happen to pass that guess.
    internal static bool IsSerializedFile(BundleFileInstance bundle, int index) =>
        (bundle.file.BlockAndDirInfo.DirectoryInfos[index].Flags & 4) != 0;

    public static BundleReport Inspect(string path)
    {
        var manager = new AssetsManager();
        try
        {
            var bundle = Open(manager, path);
            var files = new List<string>();
            var assets = new List<InspectedAsset>();

            var directory = bundle.file.BlockAndDirInfo.DirectoryInfos;
            for (var index = 0; index < directory.Count; index++)
            {
                files.Add(directory[index].Name);
                if (!IsSerializedFile(bundle, index))
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

    // Writes the field tree of every asset whose m_Name or path ID matches; returns how many matched.
    public static int Dump(string path, string assetName, TextWriter output)
    {
        var manager = new AssetsManager();
        try
        {
            var bundle = Open(manager, path);
            var matched = 0;
            for (var index = 0; index < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; index++)
            {
                if (!IsSerializedFile(bundle, index))
                {
                    continue;
                }

                var instance = manager.LoadAssetsFileFromBundle(bundle, index, loadDeps: false);
                foreach (var info in instance.file.AssetInfos)
                {
                    var byId = info.PathId.ToString(System.Globalization.CultureInfo.InvariantCulture) == assetName;
                    var field = manager.GetBaseField(instance, info, AssetReadFlags.None);
                    if (field is null || (!byId && (field["m_Name"].IsDummy || field["m_Name"].AsString != assetName)))
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
