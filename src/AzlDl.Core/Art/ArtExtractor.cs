using System.Collections.Concurrent;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace AzlDl.Core.Art;

public sealed record ExtractSummary(int Bundles, int Images, int Paintings, int Files, IReadOnlyList<string> Errors);

// Converts downloaded bundles into plain files under OutputDirectory:
// - painting/<name>_tex bundles become one reconstructed painting, art/painting/<name>.png;
// - every other Texture2D becomes <bundle path>/<texture name>.png;
// - TextAssets (Spine .skel and .atlas files, for example) are written as-is under <bundle path>/.
public sealed class ArtExtractor(string assetDirectory, string outputDirectory)
{
    private const string PaintingFolder = "painting/";
    private const string TextureSuffix = "_tex";
    private const string MeshSuffix = "-mesh";

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

                var mesh = isPainting ? FindMesh(manager, instance, bundlePath, name + MeshSuffix) : null;
                if (mesh is not null)
                {
                    PaintingReconstructor.Rebuild(image, mesh).WritePng(Target("painting", name + ".png"));
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

            return (images, paintings, files);
        }
        finally
        {
            manager.UnloadAll(true);
        }
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
