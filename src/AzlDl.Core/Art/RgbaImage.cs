using StbImageWriteSharp;

namespace AzlDl.Core.Art;

// An RGBA image stored bottom row first, the way Unity stores textures, so mesh positions and UVs map onto it without
// flipping. It is flipped once, when written out.
public sealed class RgbaImage(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public byte[] Pixels { get; } = pixels;

    public RgbaImage(int width, int height)
        : this(width, height, new byte[width * height * 4])
    {
    }

    // Copies a width x height block from (sourceX, sourceY) in source to (targetX, targetY) here, clipped to both.
    public void Blit(RgbaImage source, int sourceX, int sourceY, int targetX, int targetY, int width, int height)
    {
        for (var row = 0; row < height; row++)
        {
            var sy = sourceY + row;
            var ty = targetY + row;
            if (sy < 0 || sy >= source.Height || ty < 0 || ty >= Height)
            {
                continue;
            }

            var x0 = Math.Max(0, Math.Max(-sourceX, -targetX));
            var x1 = Math.Min(width, Math.Min(source.Width - sourceX, Width - targetX));
            if (x1 <= x0)
            {
                continue;
            }

            Buffer.BlockCopy(
                source.Pixels, ((sy * source.Width) + sourceX + x0) * 4,
                Pixels, ((ty * Width) + targetX + x0) * 4,
                (x1 - x0) * 4);
        }
    }

    public RgbaImage Crop(int x, int y, int width, int height)
    {
        var cropped = new RgbaImage(width, height);
        cropped.Blit(this, x, y, 0, 0, width, height);
        return cropped;
    }

    public void WritePng(string path)
    {
        var flipped = new byte[Pixels.Length];
        var stride = Width * 4;
        for (var row = 0; row < Height; row++)
        {
            Buffer.BlockCopy(Pixels, row * stride, flipped, (Height - 1 - row) * stride, stride);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var output = File.Create(path);
        new ImageWriter().WritePng(flipped, Width, Height, ColorComponents.RedGreenBlueAlpha, output);
    }
}
