namespace AzlDl.Core.Config;

// A global the scripts read but never define, such as Vector3. Field reads extend the name ("UnityEngine.Color").
// It behaves as nil in conditions, as the real global would be nil here.
public sealed record LuaGlobalRef(string Name);

// A call to something the interpreter doesn't run, kept as data: Vector3(-105, 0, 58).
public sealed record LuaCall(object? Function, IReadOnlyList<object?> Arguments);

// A Lua function: a prototype plus the variables it captured.
public sealed class LuaClosure(ConfigPrototype prototype, LuaUpvalue[] upvalues)
{
    public ConfigPrototype Prototype { get; } = prototype;

    public LuaUpvalue[] Upvalues { get; } = upvalues;
}

// A captured variable. It points into the frame that declared it, so the closure sees later writes.
public sealed class LuaUpvalue(object?[] registers, int index)
{
    public object? Value
    {
        get => registers[index];
        set => registers[index] = value;
    }
}

// A standard library function the setup code calls, implemented in C#.
public sealed record LuaBuiltin(string Name, Func<object?[], object?> Invoke);
