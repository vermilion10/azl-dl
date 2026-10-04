namespace AzlDl.Core.Config;

// Runs sharecfgdata prototypes. They only build constant tables and store them under _G.pg.base.<table>,
// so twelve opcodes cover them. Reads of missing tables on the way down create them, standing in for the
// pg.base namespace the game sets up before loading these files.
public sealed class ConfigInterpreter
{
    private const int RegisterCount = 256;

    public LuaTable Globals { get; } = new();

    public ConfigInterpreter()
    {
        Globals["_G"] = Globals;
    }

    // Everything the loaded files stored under pg.base, keyed by table name.
    public LuaTable Base => GetOrCreate(GetOrCreate(Globals, "pg"), "base");

    public void Run(ConfigPrototype prototype)
    {
        var registers = new object?[RegisterCount];
        var gc = prototype.GcConstants;

        foreach (var ins in prototype.Instructions)
        {
            switch (ins.Op)
            {
                case Opcode.Gget:
                    registers[ins.A] = GetOrCreate(Globals, (string)gc[ins.D]);
                    break;
                case Opcode.Tgets:
                    registers[ins.A] = GetOrCreate(AsTable(registers[ins.B]), (string)gc[ins.C]);
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
                case Opcode.Ret0:
                    return;
                default:
                    throw new InvalidDataException($"Opcode {ins.Op} isn't supported.");
            }
        }
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
