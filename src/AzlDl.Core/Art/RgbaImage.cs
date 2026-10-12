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

    // Stretches a 9-slice image (Unity sprite borders: left, bottom, right, top) to a new size: corners stay as they are,
    // edges stretch along their length and the centre in both directions.
    public RgbaImage NineSlice(int left, int bottom, int right, int top, int width, int height)
    {
        var result = new RgbaImage(width, height);
        int[] sourceX = [0, left, Width - right, Width], targetX = [0, left, width - right, width];
        int[] sourceY = [0, bottom, Height - top, Height], targetY = [0, bottom, height - top, height];
        for (var ty = 0; ty < height; ty++)
        {
            var row = ty < targetY[1] ? 0 : ty < targetY[2] ? 1 : 2;
            var sy = Map(ty, targetY[row], targetY[row + 1], sourceY[row], sourceY[row + 1]);
            for (var tx = 0; tx < width; tx++)
            {
                var column = tx < targetX[1] ? 0 : tx < targetX[2] ? 1 : 2;
                var sx = Map(tx, targetX[column], targetX[column + 1], sourceX[column], sourceX[column + 1]);
                Buffer.BlockCopy(Pixels, ((sy * Width) + sx) * 4, result.Pixels, ((ty * width) + tx) * 4, 4);
            }
        }

        return result;

        static int Map(int value, int from0, int from1, int to0, int to1) =>
            from1 <= from0 ? to0 : Math.Clamp(to0 + (int)((long)(value - from0) * (to1 - to0) / (from1 - from0)), to0, Math.Max(to0, to1 - 1));
    }

    // Draws source stretched over the rectangle (x, y, width, height) with bilinear sampling, blended over what is
    // already here. Sampling and blending happen on premultiplied colour so transparent edges don't darken.
    public void DrawScaled(RgbaImage source, double x, double y, double width, double height)
    {
        var x0 = Math.Max(0, (int)Math.Floor(x));
        var y0 = Math.Max(0, (int)Math.Floor(y));
        var x1 = Math.Min(Width, (int)Math.Ceiling(x + width));
        var y1 = Math.Min(Height, (int)Math.Ceiling(y + height));
        if (x1 <= x0 || y1 <= y0 || width <= 0 || height <= 0)
        {
            return;
        }

        var stepX = source.Width / width;
        var stepY = source.Height / height;
        Parallel.For(y0, y1, row =>
        {
            Span<float> sample = stackalloc float[4];
            var sy = ((row + 0.5 - y) * stepY) - 0.5;
            if (sy < -0.5 || sy > source.Height - 0.5)
            {
                return;
            }

            for (var column = x0; column < x1; column++)
            {
                var sx = ((column + 0.5 - x) * stepX) - 0.5;
                if (sx < -0.5 || sx > source.Width - 0.5)
                {
                    continue;
                }

                source.SamplePremultiplied(sx, sy, sample);
                var alpha = sample[3];
                if (alpha <= 0)
                {
                    continue;
                }

                var at = ((row * Width) + column) * 4;
                var below = Pixels[at + 3] / 255f;
                var outAlpha = alpha + (below * (1 - alpha));
                for (var channel = 0; channel < 3; channel++)
                {
                    var value = sample[channel] + (Pixels[at + channel] / 255f * below * (1 - alpha));
                    Pixels[at + channel] = (byte)Math.Clamp(MathF.Round(value / outAlpha * 255), 0, 255);
                }

                Pixels[at + 3] = (byte)Math.Clamp(MathF.Round(outAlpha * 255), 0, 255);
            }
        });
    }

    private void SamplePremultiplied(double x, double y, Span<float> result)
    {
        var left = (int)Math.Floor(x);
        var bottom = (int)Math.Floor(y);
        var fx = (float)(x - left);
        var fy = (float)(y - bottom);
        result.Clear();
        for (var corner = 0; corner < 4; corner++)
        {
            var cx = Math.Clamp(left + (corner & 1), 0, Width - 1);
            var cy = Math.Clamp(bottom + (corner >> 1), 0, Height - 1);
            var weight = ((corner & 1) == 0 ? 1 - fx : fx) * ((corner >> 1) == 0 ? 1 - fy : fy);
            var at = ((cy * Width) + cx) * 4;
            var alpha = Pixels[at + 3] / 255f;
            for (var channel = 0; channel < 3; channel++)
            {
                result[channel] += weight * alpha * Pixels[at + channel] / 255f;
            }

            result[3] += weight * alpha;
        }
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
