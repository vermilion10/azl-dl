namespace AzlDl.Core.Config;

// Runs the game's data scripts. They only build constant tables, either storing them under _G.pg.base.<table>
// (sharecfgdata) or returning them (story and stage scripts). Reads of missing table fields on the way down create
// them, standing in for the pg.base namespace the game sets up first. Globals the game would provide, like Vector3,
// and calls to them are kept as LuaGlobalRef and LuaCall values instead of being run.
public sealed class ConfigInterpreter
{
    private const int RegisterCount = 256;

    // With LuaJIT's two-slot frames (FR2), call arguments start two registers above the function.
    private const int CallArgumentOffset = 2;

    public LuaTable Globals { get; } = new();

    public ConfigInterpreter()
    {
        Globals["_G"] = Globals;
    }

    // Everything the loaded files stored under pg.base, keyed by table name.
    public LuaTable Base => GetOrCreate(GetOrCreate(Globals, "pg"), "base");

    // Returns the value of a RET1, or null for RET0.
    public object? Run(ConfigPrototype prototype)
    {
        var registers = new object?[RegisterCount];
        var gc = prototype.GcConstants;

        // One past the last register holding values from a call with a variable number of results.
        var top = 0;

        foreach (var ins in prototype.Instructions)
        {
            switch (ins.Op)
            {
                case Opcode.Gget:
                    var name = (string)gc[ins.D];
                    registers[ins.A] = Globals[name] ?? new LuaGlobalRef(name);
                    break;
                case Opcode.Tgets:
                    var key = (string)gc[ins.C];
                    registers[ins.A] = registers[ins.B] is LuaGlobalRef global
                        ? new LuaGlobalRef($"{global.Name}.{key}")
                        : GetOrCreate(AsTable(registers[ins.B]), key);
                    break;
                case Opcode.Kstr:
                    registers[ins.A] = (string)gc[ins.D];
                    break;
                case Opcode.Kshort:
                    registers[ins.A] = (long)(short)ins.D;
                    break;
                case Opcode.Knum:
                    registers[ins.A] = prototype.NumericConstants[ins.D];
                    break;
                case Opcode.Unm:
                    registers[ins.A] = registers[ins.D] switch
                    {
                        // Lua 5.1 numbers are doubles, so -0 stays a distinct value.
                        0L => -0.0,
                        long l => -l,
                        double d => -d,
                        var other => throw new InvalidDataException($"Can't negate {other ?? "nil"}."),
                    };
                    break;
                case Opcode.Tnew:
                    registers[ins.A] = new LuaTable();
                    break;
                case Opcode.Tdup:
                    registers[ins.A] = Copy((LuaTable)gc[ins.D]);
                    break;
                case Opcode.Tsets:
                    AsTable(registers[ins.B])[(string)gc[ins.C]] = registers[ins.A];
                    break;
                case Opcode.Tsetb:
                    AsTable(registers[ins.B])[(long)ins.C] = registers[ins.A];
                    break;
                case Opcode.Tsetv:
                    AsTable(registers[ins.B])[registers[ins.C] ?? throw new InvalidDataException("Table key is nil.")] =
                        registers[ins.A];
                    break;
                case Opcode.Call:
                    // C is the argument count plus one, or 0 for "everything up to top".
                    var first = ins.A + CallArgumentOffset;
                    var last = ins.C == 0 ? top : ins.A + ins.C + 1;
                    var arguments = registers[first..last];
                    registers[ins.A] = new LuaCall(registers[ins.A], arguments);
                    top = ins.A + 1;
                    break;
                case Opcode.Tsetm:
                    // Stores registers A..top-1 into the table in A-1. The start index sits in the low 32 bits of a
                    // numeric constant that LuaJIT writes as the double 2^52 + index.
                    var table = AsTable(registers[ins.A - 1]);
                    var start = (long)(uint)BitConverter.DoubleToInt64Bits(Convert.ToDouble(prototype.NumericConstants[ins.D]));
                    for (var r = ins.A; r < top; r++)
                    {
                        table[start + r - ins.A] = registers[r];
                    }

                    break;
                case Opcode.Ret0:
                    return null;
                case Opcode.Ret1:
                    return registers[ins.A];
                default:
                    throw new InvalidDataException($"Opcode {ins.Op} isn't supported.");
            }
        }

        return null;
    }

    private static LuaTable GetOrCreate(LuaTable table, string key)
    {
        if (table[key] is LuaTable existing)
        {
            return existing;
        }

        var created = new LuaTable();
        table[key] = created;
        return created;
    }

    private static LuaTable AsTable(object? value) =>
        value as LuaTable ?? throw new InvalidDataException($"Expected a table, found {value ?? "nil"}.");

    // Templates hold only scalars, so a shallow copy is a full copy.
    private static LuaTable Copy(LuaTable template)
    {
        var copy = new LuaTable();
        foreach (var (key, value) in template.Entries)
        {
            copy[key] = value;
        }

        return copy;
    }
}
