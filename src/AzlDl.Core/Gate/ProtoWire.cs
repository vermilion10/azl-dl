using System.Text;

namespace AzlDl.Core.Gate;

// Just enough protobuf wire format for the gate's two messages: varints and length-delimited strings.
public static class ProtoWire
{
    private const int Varint = 0;
    private const int Fixed64 = 1;
    private const int LengthDelimited = 2;
    private const int Fixed32 = 5;

    public static void WriteVarintField(Stream output, int field, ulong value)
    {
        WriteVarint(output, (ulong)(field << 3 | Varint));
        WriteVarint(output, value);
    }

    public static void WriteStringField(Stream output, int field, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarint(output, (ulong)(field << 3 | LengthDelimited));
        WriteVarint(output, (ulong)bytes.Length);
        output.Write(bytes);
    }

    // Yields (field, varint value or null, bytes or null) for every field; unknown wire types end the read.
    public static IEnumerable<ProtoField> ReadFields(byte[] data)
    {
        var position = 0;
        while (position < data.Length)
        {
            var key = ReadVarint(data, ref position);
            var field = (int)(key >> 3);
            switch ((int)(key & 7))
            {
                case Varint:
                    yield return new ProtoField(field, ReadVarint(data, ref position), null);
                    break;
                case LengthDelimited:
                    var length = (int)ReadVarint(data, ref position);
                    yield return new ProtoField(field, null, data[position..(position + length)]);
                    position += length;
                    break;
                case Fixed64:
                    position += 8;
                    break;
                case Fixed32:
                    position += 4;
                    break;
                default:
                    throw new InvalidDataException($"Unsupported protobuf wire type in field {field}.");
            }
        }
    }

    private static void WriteVarint(Stream output, ulong value)
    {
        while (value >= 0x80)
        {
            output.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        output.WriteByte((byte)value);
    }

    private static ulong ReadVarint(byte[] data, ref int position)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            var b = data[position++];
            result |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                return result;
            }
        }

        throw new InvalidDataException("Varint longer than 64 bits.");
    }
}

public sealed record ProtoField(int Number, ulong? Value, byte[]? Bytes)
{
    public string AsString() => Encoding.UTF8.GetString(Bytes ?? []);
}
