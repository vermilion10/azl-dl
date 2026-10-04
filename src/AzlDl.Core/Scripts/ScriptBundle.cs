using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace AzlDl.Core.Scripts;

public sealed record ScriptFile(string Path, byte[] Data);

// Lists the Lua scripts inside a decrypted scripts bundle. Each script is a TextAsset; its path comes from the
// bundle's container map (e.g. "assets/.../sharecfg/ship_data_trans.lua.bytes"), falling back to the asset name.
public static class ScriptBundle
{
    public static IEnumerable<ScriptFile> ReadScripts(byte[] bundleData)
    {
        var manager = new AssetsManager();
        try
        {
            var bundle = manager.LoadBundleFile(new MemoryStream(bundleData), "scripts");
            var instance = manager.LoadAssetsFileFromBundle(bundle, 0, loadDeps: false);
            var paths = ReadContainer(manager, instance);

            foreach (var info in instance.file.GetAssetsOfType(AssetClassID.TextAsset))
            {
                var field = manager.GetBaseField(instance, info);
                var script = field["m_Script"];
                if (script.IsDummy)
                {
                    continue;
                }

                // Strings are kept as raw bytes, so binary bytecode survives untouched.
                var data = script.Value.AsByteArray;
                var path = paths.GetValueOrDefault(info.PathId) ?? field["m_Name"].AsString;
                yield return new ScriptFile(path, data);
            }
        }
        finally
        {
            manager.UnloadAll(true);
        }
    }

    private static Dictionary<long, string> ReadContainer(AssetsManager manager, AssetsFileInstance instance)
    {
        var paths = new Dictionary<long, string>();
        foreach (var info in instance.file.GetAssetsOfType(AssetClassID.AssetBundle))
        {
            var container = manager.GetBaseField(instance, info)["m_Container.Array"];
            foreach (var entry in container.Children)
            {
                paths.TryAdd(entry["second"]["asset"]["m_PathID"].AsLong, entry["first"].AsString);
            }
        }

        return paths;
    }
}
