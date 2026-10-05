using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;

namespace AzlDl.Core.Art;

// Decodes a Texture2D (including pixel data streamed from the bundle's .resS file) into an RgbaImage.
public static class TextureReader
{
    public static RgbaImage? Read(AssetTypeValueField baseField, AssetsFileInstance instance)
    {
        var texture = TextureFile.ReadTextureFile(baseField);
        if (texture.m_Width <= 0 || texture.m_Height <= 0)
        {
            return null;
        }

        texture.FillPictureData(instance);
        if (texture.pictureData is null || texture.pictureData.Length == 0)
        {
            return null;
        }

        var bgra = texture.DecodeTextureRaw(texture.pictureData)
                   ?? throw new InvalidDataException($"Texture format {(TextureFormat)texture.m_TextureFormat} isn't supported.");

        // The decoder returns BGRA; swap to RGBA.
        for (var i = 0; i < bgra.Length; i += 4)
        {
            (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        }

        return new RgbaImage(texture.m_Width, texture.m_Height, bgra);
    }
}
