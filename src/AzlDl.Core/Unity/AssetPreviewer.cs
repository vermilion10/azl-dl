using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AzlDl.Core.Art;

namespace AzlDl.Core.Unity;

public enum PreviewKind
{
    Image,
    Text,
    Binary,
    Fields,
}

// One asset ready to show and to save. Image previews save as PNG; text and binary TextAssets save as their raw
// bytes; everything else saves as its field tree.
public sealed record AssetPreview(PreviewKind Kind, string FileName, RgbaImage? Image, string Text, byte[] Bytes)
{
    public void Save(string path)
    {
        if (Image is not null)
        {
            Image.WritePng(path);
        }
        else
        {
            File.WriteAllBytes(path, Bytes);
        }
    }
}

// Reads a single asset out of a bundle for the asset browser.
public static class AssetPreviewer
{
    private const int MaxTextChars = 200_000;
    private const int HexBytes = 512;

    public static AssetPreview Read(string bundlePath, InspectedAsset asset)
    {
        var manager = new AssetsManager();
        try
        {
            var bundle = BundleInspector.Open(manager, bundlePath);
            var index = bundle.file.BlockAndDirInfo.DirectoryInfos.FindIndex(d => d.Name == asset.File);
            if (index < 0 || !bundle.file.IsAssetsFile(index))
            {
                throw new InvalidDataException($"The bundle has no serialized file named {asset.File}.");
            }

            var instance = manager.LoadAssetsFileFromBundle(bundle, index, loadDeps: false);
            var info = instance.file.GetAssetInfo(asset.PathId)
                       ?? throw new InvalidDataException($"The bundle has no asset with path ID {asset.PathId}.");
            var field = manager.GetBaseField(instance, info);
            var name = SafeName(asset.Name.Length > 0 ? asset.Name : asset.PathId.ToString(System.Globalization.CultureInfo.InvariantCulture));

            return (AssetClassID)info.TypeId switch
            {
                AssetClassID.Texture2D => ImagePreview(name, TextureReader.Read(field, instance)),
                AssetClassID.Sprite when SpriteImage(manager, instance, field) is { } sprite => ImagePreview(name, sprite),
                AssetClassID.TextAsset => TextAssetPreview(name, field["m_Script"].Value.AsByteArray),
                _ => FieldsPreview(name, field),
            };
        }
        finally
        {
            manager.UnloadAll(true);
        }
    }

    private static AssetPreview ImagePreview(string name, RgbaImage? image) => image is null
        ? new AssetPreview(PreviewKind.Binary, name, null, "The texture has no pixel data.", [])
        : new AssetPreview(PreviewKind.Image, name + ".png", image, $"{image.Width} × {image.Height}", []);

    // Sprites point at a region of a texture in the same file: their own (m_RD), or, for packed sprites, the region their
    // SpriteAtlas records under the sprite's render data key.
    public static RgbaImage? SpriteImage(AssetsManager manager, AssetsFileInstance instance, AssetTypeValueField sprite)
    {
        var data = sprite["m_RD"];
        if (IsNull(data["texture"]) && AtlasData(manager, instance, sprite) is { } packed)
        {
            data = packed;
        }

        var texture = data["texture"];
        if (IsNull(texture) || texture["m_FileID"].AsInt != 0 || instance.file.GetAssetInfo(texture["m_PathID"].AsLong) is not { } info)
        {
            return null;
        }

        var image = TextureReader.Read(manager.GetBaseField(instance, info), instance);
        var rect = data["textureRect"];
        if (image is null || rect.IsDummy)
        {
            return null;
        }

        int x = (int)MathF.Round(rect["x"].AsFloat), y = (int)MathF.Round(rect["y"].AsFloat);
        int width = (int)MathF.Round(rect["width"].AsFloat), height = (int)MathF.Round(rect["height"].AsFloat);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var cropped = image.Crop(x, y, width, height);
        // settingsRaw bits 2-5 hold how the packer turned the sprite; turn it back.
        var rotation = data["settingsRaw"].IsDummy ? 0u : (data["settingsRaw"].AsUInt >> 2) & 0xF;
        return rotation == 0 ? cropped : Unrotate(cropped, rotation);
    }

    // A sprite's 9-slice borders in pixels (left, bottom, right, top); all zero when it isn't sliced.
    public static (int Left, int Bottom, int Right, int Top) SpriteBorder(string bundlePath, InspectedAsset asset)
    {
        var manager = new AssetsManager();
        try
        {
            var bundle = BundleInspector.Open(manager, bundlePath);
            var index = bundle.file.BlockAndDirInfo.DirectoryInfos.FindIndex(d => d.Name == asset.File);
            var instance = manager.LoadAssetsFileFromBundle(bundle, index, loadDeps: false);
            var border = manager.GetBaseField(instance, instance.file.GetAssetInfo(asset.PathId))["m_Border"];
            return border.IsDummy
                ? (0, 0, 0, 0)
                : ((int)MathF.Round(border["x"].AsFloat), (int)MathF.Round(border["y"].AsFloat),
                    (int)MathF.Round(border["z"].AsFloat), (int)MathF.Round(border["w"].AsFloat));
        }
        finally
        {
            manager.UnloadAll(true);
        }
    }

    private static bool IsNull(AssetTypeValueField pointer) => pointer.IsDummy || pointer["m_PathID"].AsLong == 0;

    private static AssetTypeValueField? AtlasData(AssetsManager manager, AssetsFileInstance instance, AssetTypeValueField sprite)
    {
        var atlasPointer = sprite["m_SpriteAtlas"];
        if (IsNull(atlasPointer) || atlasPointer["m_FileID"].AsInt != 0
            || instance.file.GetAssetInfo(atlasPointer["m_PathID"].AsLong) is not { } atlasInfo)
        {
            return null;
        }

        var key = Key(sprite["m_RenderDataKey"]);
        var atlas = manager.GetBaseField(instance, atlasInfo);
        foreach (var entry in atlas["m_RenderDataMap"]["Array"].Children)
        {
            if (Key(entry["first"]) == key)
            {
                return entry["second"];
            }
        }

        return null;
    }

    // The render data key is a GUID and a long; compared as text so the GUID's field layout doesn't matter.
    private static string Key(AssetTypeValueField field)
    {
        var builder = new StringBuilder();
        void Walk(AssetTypeValueField f)
        {
            if (f.Children.Count == 0)
            {
                builder.Append(f.AsString).Append(',');
            }

            foreach (var child in f.Children)
            {
                Walk(child);
            }
        }

        Walk(field);
        return builder.ToString();
    }

    // 1 flipped horizontally, 2 vertically, 3 turned 180 degrees, 4 turned 90 degrees.
    private static RgbaImage Unrotate(RgbaImage image, uint rotation)
    {
        var turned = rotation == 4;
        var result = new RgbaImage(turned ? image.Height : image.Width, turned ? image.Width : image.Height);
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var (tx, ty) = rotation switch
                {
                    1 => (image.Width - 1 - x, y),
                    2 => (x, image.Height - 1 - y),
                    3 => (image.Width - 1 - x, image.Height - 1 - y),
                    _ => (y, image.Width - 1 - x),
                };
                Buffer.BlockCopy(image.Pixels, (y * image.Width + x) * 4, result.Pixels, (ty * result.Width + tx) * 4, 4);
            }
        }

        return result;
    }

    private static AssetPreview TextAssetPreview(string name, byte[] bytes)
    {
        if (LooksLikeText(bytes))
        {
            var text = Encoding.UTF8.GetString(bytes);
            if (text.Length > MaxTextChars)
            {
                text = text[..MaxTextChars] + $"\n… {Count(text.Length - MaxTextChars)} more characters in the saved file";
            }

            return new AssetPreview(PreviewKind.Text, name, null, text, bytes);
        }

        return new AssetPreview(PreviewKind.Binary, name, null, HexDump(bytes), bytes);
    }

    private static AssetPreview FieldsPreview(string name, AssetTypeValueField field)
    {
        using var writer = new StringWriter();
        FieldDump.Write(writer, field);
        var text = writer.ToString();
        return new AssetPreview(PreviewKind.Fields, name + ".txt", null, text, Encoding.UTF8.GetBytes(text));
    }

    // Valid UTF-8 with no control characters other than whitespace.
    private static bool LooksLikeText(byte[] bytes)
    {
        try
        {
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return !text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'));
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string HexDump(byte[] bytes)
    {
        var builder = new StringBuilder();
        var shown = Math.Min(bytes.Length, HexBytes);
        for (var offset = 0; offset < shown; offset += 16)
        {
            var row = bytes.AsSpan(offset, Math.Min(16, shown - offset));
            builder.Append(offset.ToString("X6", System.Globalization.CultureInfo.InvariantCulture)).Append("  ");
            builder.Append(Convert.ToHexString(row).Chunk(2).Select(p => new string(p)).Aggregate((a, b) => a + " " + b).PadRight(48));
            builder.Append("  ");
            foreach (var b in row)
            {
                builder.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }

            builder.AppendLine();
        }

        if (bytes.Length > shown)
        {
            builder.Append($"… {Count(bytes.Length - shown)} more bytes in the saved file");
        }

        return builder.ToString();
    }

    private static string Count(int value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    private static string SafeName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
