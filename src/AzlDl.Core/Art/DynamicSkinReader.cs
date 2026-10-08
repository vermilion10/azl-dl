using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace AzlDl.Core.Art;

// One Spine skeleton of a dynamic skin, placed the way the game's prefab places it. X and Y are in the prefab's UI
// pixels (y up), Scale turns skeleton units into those pixels, and layers are listed back to front.
public sealed record SpineLayer(string Skeleton, string Atlas, double X, double Y, double Scale, string Animation);

// Reads where a dynamic skin's skeletons sit. A spinepainting/<name> bundle is a UI prefab whose SkeletonGraphic
// components point at SkeletonDataAssets in a resource bundle, usually spinepainting/<name>_res. Variants such as
// <name>_hx share their base skin's resources. Most skins have one skeleton; some are split into a back and a front
// layer at different scales.
public static class DynamicSkinReader
{
    public const string Folder = "spinepainting/";
    public const string ResourceSuffix = "_res";
    public const string LayoutFileName = "layers.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static bool IsPrefab(string bundlePath) =>
        bundlePath.StartsWith(Folder, StringComparison.Ordinal) && !bundlePath.EndsWith(ResourceSuffix, StringComparison.Ordinal);

    // Writes <output>/spinepainting/<name>/layers.json with skeleton and atlas paths relative to the data's art folder.
    public static int WriteLayout(string assetDirectory, string bundlePath, string outputDirectory)
    {
        var layers = ReadLayers(assetDirectory, bundlePath);
        if (layers.Count == 0)
        {
            return 0;
        }

        var folder = Path.Combine(outputDirectory, bundlePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, LayoutFileName), JsonSerializer.Serialize(layers, Json));
        return 1;
    }

    // Resource bundles to try, most specific first: spinepainting/a_b_res, then spinepainting/a_res.
    public static IEnumerable<string> ResourceCandidates(string bundlePath)
    {
        var path = bundlePath;
        while (true)
        {
            yield return path + ResourceSuffix;
            var cut = path.LastIndexOf('_');
            if (cut <= Folder.Length)
            {
                yield break;
            }

            path = path[..cut];
        }
    }

    public static IReadOnlyList<SpineLayer> ReadLayers(string assetDirectory, string bundlePath)
    {
        var resourcePath = ResourceCandidates(bundlePath).FirstOrDefault(p => File.Exists(Path.Combine(assetDirectory, p)))
                           ?? throw new FileNotFoundException($"{bundlePath + ResourceSuffix} isn't downloaded; it holds the skeletons.");

        var manager = new AssetsManager();
        try
        {
            var prefab = Load(manager, Path.Combine(assetDirectory, bundlePath));
            var resources = Load(manager, Path.Combine(assetDirectory, resourcePath));
            var scripts = ScriptNames(manager, prefab);
            var resourceFolder = resourcePath;

            // Skeletons by the GameObject that carries them.
            var graphics = new Dictionary<long, AssetTypeValueField>();
            foreach (var info in prefab.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
            {
                var header = manager.GetBaseField(prefab, info, AssetReadFlags.SkipMonoBehaviourFields);
                if (header["m_Script.m_FileID"].AsInt == 0 && scripts.GetValueOrDefault(header["m_Script.m_PathID"].AsLong) == "SkeletonGraphic"
                    && header["m_Enabled"].AsByte != 0)
                {
                    graphics[header["m_GameObject.m_PathID"].AsLong] = manager.GetBaseField(prefab, info);
                }
            }

            var layers = new List<SpineLayer>();
            var transforms = prefab.file.GetAssetsOfType(AssetClassID.RectTransform)
                .Concat(prefab.file.GetAssetsOfType(AssetClassID.Transform))
                .ToDictionary(i => i.PathId, i => manager.GetBaseField(prefab, i));

            void Visit(AssetTypeValueField transform, double x, double y, double scaleX, double scaleY)
            {
                var gameObjectId = transform["m_GameObject.m_PathID"].AsLong;
                var gameObject = manager.GetBaseField(prefab, prefab.file.GetAssetInfo(gameObjectId));
                if (gameObject["m_IsActive"].AsBool == false)
                {
                    return;
                }

                var position = transform["m_LocalPosition"];
                var scale = transform["m_LocalScale"];
                x += scaleX * position["x"].AsFloat;
                y += scaleY * position["y"].AsFloat;
                scaleX *= scale["x"].AsFloat;
                scaleY *= scale["y"].AsFloat;

                if (graphics.TryGetValue(gameObjectId, out var graphic) && Layer(manager, resources, graphic, resourceFolder, x, y, scaleX) is { } layer)
                {
                    layers.Add(layer);
                }

                foreach (var child in transform["m_Children.Array"].Children)
                {
                    if (transforms.TryGetValue(child["m_PathID"].AsLong, out var next))
                    {
                        Visit(next, x, y, scaleX, scaleY);
                    }
                }
            }

            foreach (var root in transforms.Values.Where(t => t["m_Father.m_PathID"].AsLong == 0))
            {
                Visit(root, 0, 0, 1, 1);
            }

            return layers;
        }
        finally
        {
            manager.UnloadAll(true);
        }
    }

    private static SpineLayer? Layer(AssetsManager manager, AssetsFileInstance resources, AssetTypeValueField graphic,
        string resourceFolder, double x, double y, double scale)
    {
        // The data asset lives in the _res bundle, so its pointer is external; path IDs are unique across the two.
        var data = Find(manager, resources, graphic["skeletonDataAsset.m_PathID"].AsLong);
        if (data is null)
        {
            return null;
        }

        var skeleton = Find(manager, resources, data["skeletonJSON.m_PathID"].AsLong);
        var atlasAsset = data["atlasAssets.Array"].Children.Select(a => Find(manager, resources, a["m_PathID"].AsLong)).FirstOrDefault(a => a is not null);
        var atlas = atlasAsset is null ? null : Find(manager, resources, atlasAsset["atlasFile.m_PathID"].AsLong);
        if (skeleton is null || atlas is null)
        {
            return null;
        }

        // SkeletonGraphic draws one skeleton unit as dataScale * referencePixelsPerUnit (100) UI pixels.
        var dataScale = data["scale"].IsDummy ? 0.01 : data["scale"].AsFloat;
        return new SpineLayer(
            $"{resourceFolder}/{skeleton["m_Name"].AsString}",
            $"{resourceFolder}/{atlas["m_Name"].AsString}",
            Math.Round(x, 3), Math.Round(y, 3), Math.Round(scale * dataScale * 100, 5),
            graphic["startingAnimation"].IsDummy ? "" : graphic["startingAnimation"].AsString);
    }

    private static AssetTypeValueField? Find(AssetsManager manager, AssetsFileInstance instance, long pathId) =>
        pathId != 0 && instance.file.GetAssetInfo(pathId) is { } info ? manager.GetBaseField(instance, info) : null;

    private static Dictionary<long, string> ScriptNames(AssetsManager manager, AssetsFileInstance instance) =>
        instance.file.GetAssetsOfType(AssetClassID.MonoScript)
            .ToDictionary(i => i.PathId, i => manager.GetBaseField(instance, i)["m_Name"].AsString);

    private static AssetsFileInstance Load(AssetsManager manager, string path)
    {
        var bundle = manager.LoadBundleFile(path, unpackIfPacked: true);
        return manager.LoadAssetsFileFromBundle(bundle, 0, loadDeps: false);
    }
}
