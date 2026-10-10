using System.Collections.Concurrent;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AzlDl.Core.Unity;

namespace AzlDl.Core.Art;

public sealed record ExtractSummary(int Bundles, int Images, int Paintings, int Files, IReadOnlyList<string> Errors);

// Converts downloaded bundles into plain files under OutputDirectory:
// - painting/<name>_tex bundles become art/painting/<name>.png, reassembled from tiles when the painting has a mesh,
//   stacked with its other layers when painting/<name> is a layered prefab, and described by a <name>.layout.json
//   (image size, layers, expression face slot) whenever that prefab is downloaded;
// - every other Texture2D becomes <bundle path>/<texture name>.png;
// - TextAssets (Spine .skel and .atlas files, for example) are written as-is under <bundle path>/;
// - live2d/<name> bundles also get <name>.moc3 and a <name>.model3.json;
// - spinepainting/<name> prefabs become a layers.json placing the skeletons from spinepainting/<name>_res.
public sealed class ArtExtractor(string assetDirectory, string outputDirectory)
{
    private const string PaintingFolder = "painting/";
    private const string Live2DFolder = "live2d/";
    private const string TextureSuffix = "_tex";
    private const string MeshSuffix = "-mesh";
    public const string MotionFolder = "motions";

    public ExtractSummary Extract(IEnumerable<string> bundlePaths, int concurrency, IProgress<string>? log = null)
    {
        var errors = new ConcurrentBag<string>();
        int bundles = 0, images = 0, paintings = 0, files = 0;

        Parallel.ForEach(bundlePaths, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency) }, bundlePath =>
        {
            try
            {
                var result = ExtractBundle(bundlePath);
                Interlocked.Add(ref images, result.Images);
                Interlocked.Add(ref paintings, result.Paintings);
                Interlocked.Add(ref files, result.Files);
                if (Interlocked.Increment(ref bundles) % 500 == 0)
                {
                    log?.Report($"  {bundles} bundles");
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                errors.Add($"{bundlePath}: {e.Message}");
            }
        });

        return new ExtractSummary(bundles, images, paintings, files, errors.Order(StringComparer.Ordinal).ToList());
    }

    private (int Images, int Paintings, int Files) ExtractBundle(string bundlePath)
    {
        var manager = new AssetsManager();
        try
        {
            var (instance, _) = Load(manager, bundlePath);
            var isPainting = bundlePath.StartsWith(PaintingFolder, StringComparison.Ordinal);
            int images = 0, paintings = 0, files = 0;

            foreach (var info in instance.file.GetAssetsOfType(AssetClassID.Texture2D))
            {
                var field = manager.GetBaseField(instance, info);
                var name = field["m_Name"].AsString;
                var image = TextureReader.Read(field, instance);
                if (image is null)
                {
                    continue;
                }

                if (isPainting)
                {
                    // Newer paintings aren't tiled: there is no mesh and the texture is already the whole image.
                    var mesh = FindMesh(manager, instance, bundlePath, name + MeshSuffix);
                    var painting = mesh is null ? image : PaintingReconstructor.Rebuild(image, mesh);
                    var target = Target("painting", name + ".png");
                    var (placed, layout) = Place(name, painting);
                    placed.Image.WritePng(target);
                    var legacy = Path.ChangeExtension(target, ".layers.json");
                    if (File.Exists(legacy))
                    {
                        File.Delete(legacy);
                    }

                    // Without a prefab the painting is just its texture, with no face slot.
                    layout ??= new PaintingLayout([new PaintingLayer(name, 0, 0, painting.Width, painting.Height)], null);
                    PaintingComposer.WriteLayout(PaintingComposer.LayoutPath(target), placed, layout);

                    paintings++;
                }
                else
                {
                    image.WritePng(Target(bundlePath, name + ".png"));
                    images++;
                }
            }

            foreach (var info in instance.file.GetAssetsOfType(AssetClassID.TextAsset))
            {
                var field = manager.GetBaseField(instance, info);
                File.WriteAllBytes(Target(bundlePath, field["m_Name"].AsString), field["m_Script"].Value.AsByteArray);
                files++;
            }

            if (bundlePath.StartsWith(Live2DFolder, StringComparison.Ordinal))
            {
                files += ExtractLive2D(manager, instance, bundlePath);
            }

            if (DynamicSkinReader.IsPrefab(bundlePath))
            {
                files += DynamicSkinReader.WriteLayout(assetDirectory, bundlePath, outputDirectory);
            }

            return (images, paintings, files);
        }
        finally
        {
            manager.UnloadAll(true);
        }
    }

    // Places the painting using its prefab when painting/<name> is downloaded: layered paintings are stacked (when all
    // their layers are downloaded) and the face slot is located. Without the prefab the texture is used as it is, with
    // no layout.
    private (PlacedPainting Placed, PaintingLayout? Layout) Place(string name, RgbaImage own)
    {
        var alone = new PlacedPainting(own, 0, 0, 1);
        var prefab = Path.Combine(assetDirectory, PaintingFolder + name);
        if (!File.Exists(prefab) || !BundleInspector.IsUnityFs(prefab))
        {
            return (alone, null);
        }

        var layout = PaintingComposer.ReadLayout(prefab);
        if (layout.Layers.All(l => l.Name != name)
            || layout.Layers.Any(l => l.Name != name && !File.Exists(Path.Combine(assetDirectory, LayerBundle(l.Name)))))
        {
            return (alone, null);
        }

        var images = layout.Layers.Select(l => (l, l.Name == name ? own : ReadPainting(LayerBundle(l.Name), l.Name))).ToList();
        return images.Any(i => i.Item2 is null)
            ? (alone, null)
            : (PaintingComposer.Compose(images.Select(i => (i.l, i.Item2!)).ToList()), layout);
    }

    public static string LayerBundle(string layer) => PaintingFolder + layer + TextureSuffix;

    // Reads one painting texture from its own bundle, rebuilt from its tiles.
    private RgbaImage? ReadPainting(string bundlePath, string name)
    {
        var manager = new AssetsManager();
        try
        {
            var (instance, _) = Load(manager, bundlePath);
            var texture = FindByName(manager, instance, AssetClassID.Texture2D, name);
            var image = texture is null ? null : TextureReader.Read(texture, instance);
            if (image is null)
            {
                return null;
            }

            var mesh = FindMesh(manager, instance, bundlePath, name + MeshSuffix);
            return mesh is null ? image : PaintingReconstructor.Rebuild(image, mesh);
        }
        finally
        {
            manager.UnloadAll(true);
        }
    }

    // Live2D bundles are Cubism for Unity prefabs. The model itself is the byte array in the CubismMoc component;
    // written next to the textures and physics file with a model3.json that ties them together, it loads in standard
    // Cubism viewers. The prefab's AnimationClips are converted to motions/<clip>.motion3.json.
    private int ExtractLive2D(AssetsManager manager, AssetsFileInstance instance, string bundlePath)
    {
        var scripts = new Dictionary<long, string>();
        foreach (var info in instance.file.GetAssetsOfType(AssetClassID.MonoScript))
        {
            scripts[info.PathId] = manager.GetBaseField(instance, info)["m_Name"].AsString;
        }

        var mocPathIds = scripts.Where(s => s.Value == "CubismMoc").Select(s => s.Key).ToHashSet();
        if (mocPathIds.Count == 0)
        {
            return 0;
        }

        var name = Path.GetFileName(bundlePath);
        foreach (var info in instance.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
        {
            // Read the script pointer cheaply before deserializing the whole component.
            var header = manager.GetBaseField(instance, info, AssetReadFlags.SkipMonoBehaviourFields);
            if (!mocPathIds.Contains(header["m_Script.m_PathID"].AsLong))
            {
                continue;
            }

            var moc = manager.GetBaseField(instance, info)["_bytes.Array"];
            if (moc.IsDummy)
            {
                continue;
            }

            var mocFile = name + ".moc3";
            File.WriteAllBytes(Target(bundlePath, mocFile), moc.AsByteArray);

            var folder = Path.Combine(outputDirectory, bundlePath);
            var textures = Directory.EnumerateFiles(folder, "texture_*.png").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
            var physics = Directory.EnumerateFiles(folder, "*.physics3*").Select(Path.GetFileName).FirstOrDefault();

            // The folder is made even for a model without clips, which marks the model as converted with motions.
            Directory.CreateDirectory(Path.Combine(folder, MotionFolder));
            var motions = Live2DMotionConverter.Convert(manager, instance, scripts);
            foreach (var motion in motions)
            {
                File.WriteAllText(Target(bundlePath + "/" + MotionFolder, motion.Name + ".motion3.json"), motion.Json);
            }

            var groups = Live2DMotionConverter.Groups(manager, instance, scripts);
            WriteModelJson(Target(bundlePath, name + ".model3.json"), mocFile, textures!, physics, motions, groups);
            return 2 + motions.Count;
        }

        return 0;
    }

    // The game's idle clip is filed under the group name Live2D viewers play on their own between motions.
    public static string MotionGroup(string clip) => clip == "idle" ? "Idle" : clip;

    private static void WriteModelJson(string path, string moc, IReadOnlyList<string> textures, string? physics,
        IReadOnlyList<Live2DMotion> motions, (IReadOnlyList<string> EyeBlink, IReadOnlyList<string> LipSync) groups)
    {
        using var stream = File.Create(path);
        using var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("Version", 3);
        writer.WriteStartObject("FileReferences");
        writer.WriteString("Moc", moc);
        writer.WriteStartArray("Textures");
        foreach (var texture in textures)
        {
            writer.WriteStringValue(texture);
        }

        writer.WriteEndArray();
        if (physics is not null)
        {
            writer.WriteString("Physics", physics);
        }

        if (motions.Count > 0)
        {
            writer.WriteStartObject("Motions");
            foreach (var motion in motions.OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                writer.WriteStartArray(MotionGroup(motion.Name));
                writer.WriteStartObject();
                writer.WriteString("File", $"{MotionFolder}/{motion.Name}.motion3.json");
                writer.WriteNumber("FadeInTime", 0.5);
                writer.WriteNumber("FadeOutTime", 0.5);
                writer.WriteEndObject();
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteStartArray("Groups");
        foreach (var (name, ids) in new[] { ("EyeBlink", groups.EyeBlink), ("LipSync", groups.LipSync) })
        {
            writer.WriteStartObject();
            writer.WriteString("Target", "Parameter");
            writer.WriteString("Name", name);
            writer.WriteStartArray("Ids");
            foreach (var id in ids)
            {
                writer.WriteStringValue(id);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    // The mesh usually sits next to the texture in the _tex bundle; for some paintings it is in the sibling bundle.
    private AssetTypeValueField? FindMesh(AssetsManager manager, AssetsFileInstance instance, string bundlePath, string meshName)
    {
        if (FindByName(manager, instance, AssetClassID.Mesh, meshName) is { } local)
        {
            return local;
        }

        var sibling = bundlePath.EndsWith(TextureSuffix, StringComparison.Ordinal)
            ? bundlePath[..^TextureSuffix.Length]
            : bundlePath + TextureSuffix;
        if (!File.Exists(Path.Combine(assetDirectory, sibling)))
        {
            return null;
        }

        var (siblingInstance, _) = Load(manager, sibling);
        return FindByName(manager, siblingInstance, AssetClassID.Mesh, meshName);
    }

    private static AssetTypeValueField? FindByName(AssetsManager manager, AssetsFileInstance instance, AssetClassID type, string name)
    {
        foreach (var info in instance.file.GetAssetsOfType(type))
        {
            var field = manager.GetBaseField(instance, info);
            if (field["m_Name"].AsString == name)
            {
                return field;
            }
        }

        return null;
    }

    private (AssetsFileInstance Instance, BundleFileInstance Bundle) Load(AssetsManager manager, string bundlePath)
    {
        var bundle = manager.LoadBundleFile(Path.Combine(assetDirectory, bundlePath), unpackIfPacked: true);
        return (manager.LoadAssetsFileFromBundle(bundle, 0, loadDeps: false), bundle);
    }

    private string Target(string folder, string fileName)
    {
        var safe = string.Concat(fileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var directory = Path.Combine(outputDirectory, folder);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, safe);
    }
}
