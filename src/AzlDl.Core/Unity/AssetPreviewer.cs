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

    // Sprites point at a region of a texture; only regions of a texture in the same file are cut out.
    private static RgbaImage? SpriteImage(AssetsManager manager, AssetsFileInstance instance, AssetTypeValueField sprite)
    {
        var texture = sprite["m_RD"]["texture"];
        if (texture.IsDummy || texture["m_FileID"].AsInt != 0 || instance.file.GetAssetInfo(texture["m_PathID"].AsLong) is not { } info)
        {
            return null;
        }

        var image = TextureReader.Read(manager.GetBaseField(instance, info), instance);
        var rect = sprite["m_RD"]["textureRect"];
        if (image is null || rect.IsDummy)
        {
            return null;
        }

        int x = (int)rect["x"].AsFloat, y = (int)rect["y"].AsFloat;
        int width = (int)MathF.Round(rect["width"].AsFloat), height = (int)MathF.Round(rect["height"].AsFloat);
        return width <= 0 || height <= 0 ? null : image.Crop(x, y, width, height);
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
