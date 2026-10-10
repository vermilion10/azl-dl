using System.Buffers.Binary;
using System.Text;

namespace AzlDl.Core.Audio;

// A CRI @UTF table: the column-store format ACB files and their sub-tables are written in. Values are big-endian.
// Each column is stored per row, once as a constant in the schema, or not at all; strings live in a string pool and
// byte blobs in a data pool. Blobs are often nested @UTF tables.
public sealed class CriUtfTable
{
    private const byte StorageMask = 0xF0;
    private const byte StorageConstant = 0x30;
    private const byte StoragePerRow = 0x50;

    private readonly List<Dictionary<string, object?>> rows = [];

    private CriUtfTable(string name) => Name = name;

    public string Name { get; }

    // A table with no rows, for sub-tables a bank leaves out.
    public static CriUtfTable Empty { get; } = new("");

    public int RowCount => rows.Count;

    public object? this[int row, string column] => rows[row].GetValueOrDefault(column);

    public long Number(int row, string column) => this[row, column] switch
    {
        byte v => v,
        sbyte v => v,
        ushort v => v,
        short v => v,
        uint v => v,
        int v => v,
        ulong v => (long)v,
        long v => v,
        _ => throw new InvalidDataException($"{Name}.{column} isn't a number."),
    };

    public string Text(int row, string column) => this[row, column] as string ?? "";

    public byte[] Data(int row, string column) => this[row, column] as byte[] ?? [];

    public static bool IsTable(ReadOnlySpan<byte> data) => data.StartsWith("@UTF"u8);

    public static CriUtfTable Read(byte[] data)
    {
        if (!IsTable(data))
        {
            throw new InvalidDataException("Not a CRI @UTF table.");
        }

        var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
        var table = data.AsSpan(8, size).ToArray();
        var rowsAt = BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(2));
        var stringsAt = (int)BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(4));
        var dataAt = (int)BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(8));
        var nameAt = (int)BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(12));
        var columnCount = BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(16));
        var rowWidth = BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(18));
        var rowCount = (int)BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(20));

        string String(int offset)
        {
            var start = stringsAt + offset;
            var end = Array.IndexOf(table, (byte)0, start);
            return Encoding.UTF8.GetString(table, start, (end < 0 ? table.Length : end) - start);
        }

        (object? Value, int Size) Value(int kind, int at) => kind switch
        {
            0 => (table[at], 1),
            1 => ((sbyte)table[at], 1),
            2 => (BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(at)), 2),
            3 => (BinaryPrimitives.ReadInt16BigEndian(table.AsSpan(at)), 2),
            4 => (BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(at)), 4),
            5 => (BinaryPrimitives.ReadInt32BigEndian(table.AsSpan(at)), 4),
            6 => (BinaryPrimitives.ReadUInt64BigEndian(table.AsSpan(at)), 8),
            7 => (BinaryPrimitives.ReadInt64BigEndian(table.AsSpan(at)), 8),
            8 => (BinaryPrimitives.ReadSingleBigEndian(table.AsSpan(at)), 4),
            9 => (BinaryPrimitives.ReadDoubleBigEndian(table.AsSpan(at)), 8),
            0xA => (String((int)BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(at))), 4),
            0xB => (table.AsSpan(dataAt + (int)BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(at)),
                (int)BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(at + 4))).ToArray(), 8),
            _ => throw new InvalidDataException($"Unknown @UTF column type {kind}."),
        };

        var columns = new List<(string Name, int Storage, int Kind, object? Constant)>();
        var position = 24;
        for (var i = 0; i < columnCount; i++)
        {
            var flags = table[position];
            var name = String((int)BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(position + 1)));
            position += 5;
            object? constant = null;
            if ((flags & StorageMask) == StorageConstant)
            {
                (constant, var width) = Value(flags & 0x0F, position);
                position += width;
            }

            columns.Add((name, flags & StorageMask, flags & 0x0F, constant));
        }

        var result = new CriUtfTable(String(nameAt));
        for (var row = 0; row < rowCount; row++)
        {
            var at = rowsAt + (row * rowWidth);
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, storage, kind, constant) in columns)
            {
                if (storage == StoragePerRow)
                {
                    (values[name], var width) = Value(kind, at);
                    at += width;
                }
                else
                {
                    values[name] = storage == StorageConstant ? constant : null;
                }
            }

            result.rows.Add(values);
        }

        return result;
    }
}
