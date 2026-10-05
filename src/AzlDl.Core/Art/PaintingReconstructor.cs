using AssetsTools.NET;

namespace AzlDl.Core.Art;

// Rebuilds a full painting from its packed texture. Paintings ship as an atlas of rectangular tiles plus a "<name>-mesh"
// Mesh with one quad per tile: each quad's UVs say where the tile is in the atlas, its positions say where it goes on
// the canvas. Copying every tile back gives the original image.
public static class PaintingReconstructor
{
    private const int ChannelPosition = 0;
    private const int ChannelUv0 = 4;
    private const int FormatFloat32 = 0;
    private const int VerticesPerQuad = 4;

    public static RgbaImage Rebuild(RgbaImage atlas, AssetTypeValueField mesh)
    {
        var (positions, uvs) = ReadVertices(mesh);
        if (positions.Length % VerticesPerQuad != 0)
        {
            throw new InvalidDataException($"Painting mesh has {positions.Length} vertices, not a multiple of {VerticesPerQuad}.");
        }

        // Positions normally start at 0; anything else is shifted so the canvas starts at the first tile.
        var originX = (int)Math.Round(Math.Min(0, positions.Min(p => p.X)));
        var originY = (int)Math.Round(Math.Min(0, positions.Min(p => p.Y)));
        var width = (int)Math.Round(positions.Max(p => p.X)) - originX;
        var height = (int)Math.Round(positions.Max(p => p.Y)) - originY;
        var canvas = new RgbaImage(width, height);

        for (var quad = 0; quad < positions.Length; quad += VerticesPerQuad)
        {
            var corners = positions.AsSpan(quad, VerticesPerQuad);
            var coords = uvs.AsSpan(quad, VerticesPerQuad);

            var targetX = (int)Math.Round(Min(corners, p => p.X));
            var targetY = (int)Math.Round(Min(corners, p => p.Y));
            var tileWidth = (int)Math.Round(Max(corners, p => p.X)) - targetX;
            var tileHeight = (int)Math.Round(Max(corners, p => p.Y)) - targetY;

            var sourceX = (int)Math.Round(Min(coords, c => c.X) * atlas.Width);
            var sourceY = (int)Math.Round(Min(coords, c => c.Y) * atlas.Height);

            canvas.Blit(atlas, sourceX, sourceY, targetX - originX, targetY - originY, tileWidth, tileHeight);
        }

        return canvas;
    }

    private static (Vector2[] Positions, Vector2[] Uvs) ReadVertices(AssetTypeValueField mesh)
    {
        var vertexData = mesh["m_VertexData"];
        var count = (int)vertexData["m_VertexCount"].AsUInt;
        var channels = vertexData["m_Channels.Array"].Children
            .Select(c => (Stream: c["stream"].AsByte, Offset: c["offset"].AsByte, Format: c["format"].AsByte, Dimension: c["dimension"].AsByte))
            .ToArray();
        var data = vertexData["m_DataSize"].AsByteArray;

        var position = channels[ChannelPosition];
        var uv = channels[ChannelUv0];
        if (position.Dimension < 2 || uv.Dimension < 2 || position.Format != FormatFloat32 || uv.Format != FormatFloat32
            || position.Stream != 0 || uv.Stream != 0)
        {
            throw new InvalidDataException("Painting mesh doesn't use the expected float position and UV layout.");
        }

        // Every channel in stream 0 is interleaved; the stride is the sum of their sizes.
        var stride = channels.Where(c => c.Stream == 0 && c.Dimension > 0).Sum(c => ComponentSize(c.Format) * (c.Dimension & 0xF));

        var positions = new Vector2[count];
        var uvs = new Vector2[count];
        for (var i = 0; i < count; i++)
        {
            var at = i * stride;
            positions[i] = new Vector2(
                BitConverter.ToSingle(data, at + position.Offset),
                BitConverter.ToSingle(data, at + position.Offset + 4));
            uvs[i] = new Vector2(
                BitConverter.ToSingle(data, at + uv.Offset),
                BitConverter.ToSingle(data, at + uv.Offset + 4));
        }

        return (positions, uvs);
    }

    // Vertex formats: 0 float32, 1 float16, 2 unorm8, 3 snorm8, 4 unorm16, 5 snorm16, then 8/16/32-bit integers.
    private static int ComponentSize(int format) => format switch
    {
        0 => 4,
        1 => 2,
        2 or 3 => 1,
        4 or 5 => 2,
        6 or 7 => 1,
        8 or 9 => 2,
        _ => 4,
    };

    private static float Min(ReadOnlySpan<Vector2> points, Func<Vector2, float> pick)
    {
        var value = float.MaxValue;
        foreach (var p in points)
        {
            value = Math.Min(value, pick(p));
        }

        return value;
    }

    private static float Max(ReadOnlySpan<Vector2> points, Func<Vector2, float> pick)
    {
        var value = float.MinValue;
        foreach (var p in points)
        {
            value = Math.Max(value, pick(p));
        }

        return value;
    }

    private readonly record struct Vector2(float X, float Y);
}
