using System.Text;

namespace AzlDl.Core.Config;

// One decoded prototype: instructions with operands already de-obfuscated, plus its constants.
public sealed record ConfigPrototype(
    IReadOnlyList<Instruction> Instructions,
    IReadOnlyList<object> NumericConstants,
    IReadOnlyList<object> GcConstants);

// A, B and C are 8-bit; D is the 16-bit C|B<<8 used by instructions that take one wide operand.
public readonly record struct Instruction(Opcode Op, int A, int B, int C)
{
    public int D => C | B << 8;
}

// LuaJIT 2.1 opcodes that occur in the sharecfgdata files. The game remaps opcode numbers; these are its values.
public enum Opcode : byte
{
    Kstr = 0x06,
    Kshort = 0x08,
    Knum = 0x09,
    Unm = 0x2E,
    Ret0 = 0x32,
    Tnew = 0x4C,
    Tdup = 0x4D,
    Gget = 0x4E,
    Tgets = 0x51,
    Tsetv = 0x55,
    Tsets = 0x56,
    Tsetb = 0x57,
}

// Reads sharecfgdata files: LuaJIT 2.1 bytecode prototypes laid end to end with no file header, each one a single
// "pg.base.<table>[key] = {...}" statement. Differences from stock LuaJIT dumps:
// - opcode bytes are remapped (see Opcode);
// - operand A is stored XOR 0xFF, and the B/high-D byte is stored XOR the instruction's index;
// - the prototype header lists the numeric constant count before the GC constant count,
//   and numeric constants are written before GC constants;
// - table templates list the hash count before the array count;
// - string bytes are XORed with 0xFF minus their position.
public static class ConfigChunkReader
{
    private const int KgcTable = 1;
    private const int KgcString = 5;

    private const int KtabNil = 0;
    private const int KtabFalse = 1;
    private const int KtabTrue = 2;
    private const int KtabInt = 3;
    private const int KtabNum = 4;
    private const int KtabString = 5;

    public static IEnumerable<ConfigPrototype> ReadAll(byte[] data)
    {
        var position = 0;
        while (position < data.Length)
        {
            var size = (int)ReadUleb(data, ref position);
            if (size == 0)
            {
                yield break;
            }

            if (position + size > data.Length)
            {
                throw new InvalidDataException($"Prototype at {position} runs past the end of the file.");
            }

            yield return ReadPrototype(data.AsSpan(position, size).ToArray());
            position += size;
        }
    }

    private static ConfigPrototype ReadPrototype(byte[] data)
    {
        // Flags, frame size, parameter count and upvalue count; none are needed to run a data statement.
        var position = 4;
        var numericCount = (int)ReadUleb(data, ref position);
        var gcCount = (int)ReadUleb(data, ref position);
        var instructionCount = (int)ReadUleb(data, ref position);

        var instructions = new Instruction[instructionCount];
        for (var i = 0; i < instructionCount; i++)
        {
            var at = position + i * 4;
            var op = (Opcode)data[at];
            if (!Enum.IsDefined(op))
            {
                throw new InvalidDataException($"Unknown opcode 0x{data[at]:X2} at instruction {i}.");
            }

            instructions[i] = new Instruction(op, data[at + 1] ^ 0xFF, data[at + 3] ^ (i & 0xFF), data[at + 2]);
        }

        position += instructionCount * 4;

        var numbers = new object[numericCount];
        for (var i = 0; i < numericCount; i++)
        {
            numbers[i] = ReadNumber(data, ref position);
        }

        // Dumped in reverse, so constant index 0 is the last one written.
        var gc = new object[gcCount];
        for (var i = gcCount - 1; i >= 0; i--)
        {
            gc[i] = ReadGcConstant(data, ref position);
        }

        if (position != data.Length)
        {
            throw new InvalidDataException($"Prototype has {data.Length - position} unread bytes.");
        }

        return new ConfigPrototype(instructions, numbers, gc);
    }

    private static object ReadGcConstant(byte[] data, ref int position)
    {
        var type = (int)ReadUleb(data, ref position);
        if (type >= KgcString)
        {
            return ReadString(data, ref position, type - KgcString);
        }

        if (type != KgcTable)
        {
            throw new InvalidDataException($"Unsupported constant type {type}; data files only hold strings and tables.");
        }

        var hashCount = (int)ReadUleb(data, ref position);
        var arrayCount = (int)ReadUleb(data, ref position);
        var table = new LuaTable();
        for (var i = 0; i < arrayCount; i++)
        {
            var value = ReadTableValue(data, ref position);
            if (value is not null)
            {
                table[(long)i] = value;
            }
        }

        for (var i = 0; i < hashCount; i++)
        {
            var key = ReadTableValue(data, ref position)
                      ?? throw new InvalidDataException("Table template has a nil key.");
            table[key] = ReadTableValue(data, ref position);
        }

        return table;
    }

    private static object? ReadTableValue(byte[] data, ref int position)
    {
        var type = (int)ReadUleb(data, ref position);
        return type switch
        {
            KtabNil => null,
            KtabFalse => false,
            KtabTrue => true,
            KtabInt => (long)(int)(uint)ReadUleb(data, ref position),
            KtabNum => BitConverter.UInt64BitsToDouble(ReadUleb(data, ref position) | ReadUleb(data, ref position) << 32),
            >= KtabString => ReadString(data, ref position, type - KtabString),
            _ => throw new InvalidDataException($"Unsupported table value type {type}."),
        };
    }

    // ULEB128 with the low bit of the first byte flagging a double (two 32-bit halves) instead of an int32.
    private static object ReadNumber(byte[] data, ref int position)
    {
        var first = data[position];
        var isDouble = (first & 1) != 0;
        ulong low = (uint)(first >> 1);
        position++;
        if (first >= 0x80)
        {
            low &= 0x3F;
            var shift = 6;
            byte b;
            do
            {
                b = data[position++];
                low |= (ulong)(b & 0x7F) << shift;
                shift += 7;
            }
            while (b >= 0x80);
        }

        if (!isDouble)
        {
            return (long)(int)(uint)low;
        }

        var high = ReadUleb(data, ref position);
        return BitConverter.UInt64BitsToDouble((uint)low | high << 32);
    }

    private static string ReadString(byte[] data, ref int position, int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(data[position + i] ^ (0xFF - i));
        }

        position += length;
        return Encoding.UTF8.GetString(bytes);
    }

    private static ulong ReadUleb(byte[] data, ref int position)
    {
        ulong result = 0;
        var shift = 0;
        byte b;
        do
        {
            b = data[position++];
            result |= (ulong)(b & 0x7F) << shift;
            shift += 7;
        }
        while (b >= 0x80);

        return result;
    }
}
