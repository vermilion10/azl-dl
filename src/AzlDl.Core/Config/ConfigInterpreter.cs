namespace AzlDl.Core.Config;

// A small LuaJIT bytecode VM for the game's data scripts. They build constant tables and store them under
// _G.pg / _G.pg.base, or return them (story and stage scripts); sharecfg files wrap that in a little setup code
// (pg = pg or {}, setmetatable, a function that fills the table and is called at once).
//
// Shortcuts that hold for data scripts but not Lua in general:
// - reading a missing field of a table creates it, standing in for the namespaces the game sets up first;
// - undefined globals become LuaGlobalRef values (nil in conditions), and calls to them become LuaCall values;
// - only the opcodes these scripts execute are implemented; anything else throws when reached.
public sealed class ConfigInterpreter
{
    private const int RegisterCount = 256;

    // With LuaJIT's two-slot frames (FR2), call arguments start two registers above the function.
    private const int CallArgumentOffset = 2;

    private const int JumpBias = 0x8000;
    private const int UpvalueIsLocal = 0x8000;
    private const int UpvalueSlotMask = 0x3FFF;

    public LuaTable Globals { get; } = new();

    public ConfigInterpreter()
    {
        Globals["_G"] = Globals;
        Globals["rawget"] = new LuaBuiltin("rawget", args => Argument(args, 0) is LuaTable t && Argument(args, 1) is { } k ? t[k] : null);
        Globals["setmetatable"] = new LuaBuiltin("setmetatable", args => Argument(args, 0));
    }

    public LuaTable Pg => GetOrCreate(Globals, "pg");

    // Everything the loaded files stored under pg.base, keyed by table name.
    public LuaTable Base => GetOrCreate(Pg, "base");

    // Runs a top-level prototype and returns its first result, or null if it returns nothing.
    public object? Run(ConfigPrototype prototype)
    {
        var results = Execute(new LuaClosure(prototype, []), []);
        return results.Length > 0 ? results[0] : null;
    }

    private object?[] Execute(LuaClosure closure, object?[] arguments)
    {
        var prototype = closure.Prototype;
        var code = prototype.Instructions;
        var gc = prototype.GcConstants;
        var registers = new object?[RegisterCount];
        Array.Copy(arguments, registers, Math.Min(arguments.Length, RegisterCount));

        // One past the last register holding values from a call with a variable number of results.
        var top = 0;

        for (var pc = 0; pc < code.Count; pc++)
        {
            var ins = code[pc];
            switch (ins.Op)
            {
                case Opcode.Ist:
                    // The next instruction is a JMP, taken only when the test passes.
                    if (!IsTruthy(registers[ins.D]))
                    {
                        pc++;
                    }

                    break;
                case Opcode.Jmp:
                // Closing upvalues needs no work: captured variables already live in this frame's registers.
                case Opcode.Uclo:
                    pc += ins.D - JumpBias;
                    break;
                case Opcode.Kpri:
                    registers[ins.A] = ins.D switch
                    {
                        0 => null,
                        1 => false,
                        _ => true,
                    };
                    break;
                case Opcode.Uget:
                    registers[ins.A] = closure.Upvalues[ins.D].Value;
                    break;
                case Opcode.Tgetv:
                    var field = registers[ins.C] ?? throw new InvalidDataException("Table key is nil.");
                    registers[ins.A] = registers[ins.B] is LuaGlobalRef owner
                        ? new LuaGlobalRef($"{owner.Name}.{field}")
                        : field is string text ? GetOrCreate(AsTable(registers[ins.B]), text) : AsTable(registers[ins.B])[field];
                    break;
                case Opcode.Gget:
                    var name = (string)gc[ins.D];
                    registers[ins.A] = Globals[name] ?? new LuaGlobalRef(name);
                    break;
                case Opcode.Gset:
                    Globals[(string)gc[ins.D]] = registers[ins.A];
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
                case Opcode.Fnew:
                    registers[ins.A] = Capture((ConfigPrototype)gc[ins.D], closure, registers);
                    break;
                case Opcode.Call:
                    // C is the argument count plus one, or 0 for "everything up to top";
                    // B is the result count plus one, or 0 for "keep them all".
                    var first = ins.A + CallArgumentOffset;
                    var last = ins.C == 0 ? top : ins.A + ins.C + 1;
                    var results = Call(registers[ins.A], registers[first..last]);
                    var wanted = ins.B == 0 ? results.Length : ins.B - 1;
                    for (var i = 0; i < wanted; i++)
                    {
                        registers[ins.A + i] = i < results.Length ? results[i] : null;
                    }

                    top = ins.A + wanted;
                    break;
                case Opcode.Ret0:
                    return [];
                case Opcode.Ret1:
                    return [registers[ins.A]];
                default:
                    throw new InvalidDataException($"Opcode 0x{(byte)ins.Op:X2} at instruction {pc} isn't supported.");
            }
        }

        return [];
    }

    private object?[] Call(object? function, object?[] arguments) => function switch
    {
        LuaClosure closure => Execute(closure, arguments),
        LuaBuiltin builtin => [builtin.Invoke(arguments)],
        _ => [new LuaCall(function, arguments)],
    };

    private static LuaClosure Capture(ConfigPrototype prototype, LuaClosure parent, object?[] registers)
    {
        var upvalues = new LuaUpvalue[prototype.Upvalues.Count];
        for (var i = 0; i < upvalues.Length; i++)
        {
            var descriptor = prototype.Upvalues[i];
            upvalues[i] = (descriptor & UpvalueIsLocal) != 0
                ? new LuaUpvalue(registers, descriptor & UpvalueSlotMask)
                : parent.Upvalues[descriptor];
        }

        return new LuaClosure(prototype, upvalues);
    }

    private static bool IsTruthy(object? value) => value is not (null or false or LuaGlobalRef);

    private static object? Argument(object?[] arguments, int index) => index < arguments.Length ? arguments[index] : null;

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
