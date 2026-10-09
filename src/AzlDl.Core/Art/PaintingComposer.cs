using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace AzlDl.Core.Art;

// One image of a layered painting: the painting texture it shows, and its rectangle in the prefab's root space
// (UI units, y up, X/Y the lower-left corner). Layers are listed back to front.
public sealed record PaintingLayer(string Name, double X, double Y, double Width, double Height);

// A painting prefab's images, back to front, and the rectangle its expression faces are drawn in (null when the
// painting has no face slot). Faces live in paintingface/<name> as textures named after the expression.
public sealed record PaintingLayout(IReadOnlyList<PaintingLayer> Layers, PaintingLayer? Face);

// A rectangle in the written PNG's pixels, measured from its top-left corner.
public sealed record PixelRect(double X, double Y, double Width, double Height);

// What is written next to a painting PNG as <name>.layout.json: the image size, the layers stacked into it, and
// where the face slot sits in it.
public sealed record PaintingLayoutFile(int Width, int Height, double PixelsPerUnit, IReadOnlyList<string> Layers, PixelRect? Face);

// The stacked image and how prefab units map onto it.
public sealed record PlacedPainting(RgbaImage Image, double MinX, double MinY, double PixelsPerUnit)
{
    public PixelRect ToPixels(PaintingLayer rect) => new(
        Math.Round((rect.X - MinX) * PixelsPerUnit, 2),
        Math.Round(Image.Height - ((rect.Y - MinY + rect.Height) * PixelsPerUnit), 2),
        Math.Round(rect.Width * PixelsPerUnit, 2),
        Math.Round(rect.Height * PixelsPerUnit, 2));
}

// Some paintings are several images stacked by their prefab: a background, the character (<name>_rw), and sometimes
// foreground or extra background pieces (<name>_front, <name>_bj). painting/<name> holds the prefab; each layer is a
// MeshImage on a RectTransform, named after the painting texture it shows (painting/<layer>_tex). The composer reads
// that layout and stacks the images the way the game draws them.
public static class PaintingComposer
{
    // Composites are drawn at the sharpest layer's resolution, but no larger than this on either side.
    public const int MaxSide = 4096;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public const string FaceObjectName = "face";

    public static string LayoutPath(string paintingPng) => Path.ChangeExtension(paintingPng, ".layout.json");

    public static IReadOnlyList<PaintingLayer> ReadLayers(string prefabBundle) => ReadLayout(prefabBundle).Layers;

    public static PaintingLayout ReadLayout(string prefabBundle)
    {
        var manager = new AssetsManager();
        try
        {
            var bundle = manager.LoadBundleFile(prefabBundle, unpackIfPacked: true);
            var prefab = manager.LoadAssetsFileFromBundle(bundle, 0, loadDeps: false);
            var meshImage = prefab.file.GetAssetsOfType(AssetClassID.MonoScript)
                .Where(i => manager.GetBaseField(prefab, i)["m_Name"].AsString == "MeshImage")
                .Select(i => i.PathId).FirstOrDefault();
            if (meshImage == 0)
            {
                return new PaintingLayout([], null);
            }

            // GameObjects that draw a painting image.
            var drawn = new HashSet<long>();
            foreach (var info in prefab.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
            {
                var header = manager.GetBaseField(prefab, info, AssetReadFlags.SkipMonoBehaviourFields);
                if (header["m_Script.m_FileID"].AsInt != 0 || header["m_Script.m_PathID"].AsLong != meshImage || header["m_Enabled"].AsByte == 0)
                {
                    continue;
                }

                if (manager.GetBaseField(prefab, info)["m_Sprite.m_PathID"].AsLong != 0)
                {
                    drawn.Add(header["m_GameObject.m_PathID"].AsLong);
                }
            }

            var transforms = prefab.file.GetAssetsOfType(AssetClassID.RectTransform)
                .ToDictionary(i => i.PathId, i => manager.GetBaseField(prefab, i));
            var layers = new List<PaintingLayer>();
            PaintingLayer? face = null;

            // parent: the parent's rect in its own space (pivot at the origin); offset and scale map that space to the root's.
            void Visit(AssetTypeValueField node, (double X, double Y, double W, double H)? parent, double offsetX, double offsetY, double scale)
            {
                var gameObjectId = node["m_GameObject.m_PathID"].AsLong;
                var gameObject = manager.GetBaseField(prefab, prefab.file.GetAssetInfo(gameObjectId));
                var name = gameObject["m_Name"].AsString;

                // The face slot is switched off in the prefab; the game turns it on when it shows an expression.
                var isFace = name == FaceObjectName && parent is not null;
                if (!gameObject["m_IsActive"].AsBool && !isFace)
                {
                    return;
                }

                var pivot = Vec(node["m_Pivot"]);
                var sizeDelta = Vec(node["m_SizeDelta"]);
                (double X, double Y) size, pivotAt;
                if (parent is { } p)
                {
                    var min = Vec(node["m_AnchorMin"]);
                    var max = Vec(node["m_AnchorMax"]);
                    var span = ((max.X - min.X) * p.W, (max.Y - min.Y) * p.H);
                    size = (span.Item1 + sizeDelta.X, span.Item2 + sizeDelta.Y);
                    var anchored = Vec(node["m_AnchoredPosition"]);
                    pivotAt = (p.X + (min.X * p.W) + (span.Item1 * pivot.X) + anchored.X,
                               p.Y + (min.Y * p.H) + (span.Item2 * pivot.Y) + anchored.Y);
                }
                else
                {
                    // The root is the painting's own frame; its scale only fits it to the screen.
                    size = (sizeDelta.X, sizeDelta.Y);
                    pivotAt = (0, 0);
                }

                var nodeScale = parent is null ? 1 : node["m_LocalScale"]["x"].AsFloat;
                offsetX += scale * pivotAt.X;
                offsetY += scale * pivotAt.Y;
                scale *= nodeScale;
                var rect = (X: -pivot.X * size.X, Y: -pivot.Y * size.Y, W: size.X, H: size.Y);

                var placed = new PaintingLayer(name, offsetX + (scale * rect.X), offsetY + (scale * rect.Y), scale * rect.W, scale * rect.H);
                if (isFace)
                {
                    face ??= placed;
                    return;
                }

                if (drawn.Contains(gameObjectId))
                {
                    layers.Add(placed);
                }

                foreach (var child in node["m_Children.Array"].Children)
                {
                    if (transforms.TryGetValue(child["m_PathID"].AsLong, out var next))
                    {
                        Visit(next, rect, offsetX, offsetY, scale);
                    }
                }
            }

            foreach (var root in transforms.Values.Where(t => t["m_Father.m_PathID"].AsLong == 0))
            {
                Visit(root, null, 0, 0, 1);
            }

            return new PaintingLayout(layers, face);
        }
        finally
        {
            manager.UnloadAll(true);
        }
    }

    // Stacks the layers onto one canvas covering all of them. A single layer is used as it is.
    public static PlacedPainting Compose(IReadOnlyList<(PaintingLayer Layer, RgbaImage Image)> layers)
    {
        if (layers.Count == 1)
        {
            var (only, image) = layers[0];
            return new PlacedPainting(image, only.X, only.Y, image.Width / only.Width);
        }

        var minX = layers.Min(l => l.Layer.X);
        var minY = layers.Min(l => l.Layer.Y);
        var width = layers.Max(l => l.Layer.X + l.Layer.Width) - minX;
        var height = layers.Max(l => l.Layer.Y + l.Layer.Height) - minY;

        var pixelsPerUnit = layers.Max(l => l.Image.Width / l.Layer.Width);
        pixelsPerUnit = Math.Min(pixelsPerUnit, MaxSide / Math.Max(width, height));
        var canvas = new RgbaImage((int)Math.Ceiling(width * pixelsPerUnit), (int)Math.Ceiling(height * pixelsPerUnit));

        foreach (var (layer, image) in layers)
        {
            canvas.DrawScaled(image, (layer.X - minX) * pixelsPerUnit, (layer.Y - minY) * pixelsPerUnit,
                layer.Width * pixelsPerUnit, layer.Height * pixelsPerUnit);
        }

        return new PlacedPainting(canvas, minX, minY, pixelsPerUnit);
    }

    public static void WriteLayout(string path, PlacedPainting placed, PaintingLayout layout) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new PaintingLayoutFile(
            placed.Image.Width, placed.Image.Height, Math.Round(placed.PixelsPerUnit, 5),
            layout.Layers.Select(l => l.Name).ToList(),
            layout.Face is null ? null : placed.ToPixels(layout.Face)), Json));

    public static PaintingLayoutFile? ReadLayoutFile(string paintingPng)
    {
        var path = LayoutPath(paintingPng);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<PaintingLayoutFile>(File.ReadAllText(path), Json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (double X, double Y) Vec(AssetTypeValueField field) =>
        field.IsDummy ? (0, 0) : (field["x"].AsFloat, field["y"].AsFloat);
}
