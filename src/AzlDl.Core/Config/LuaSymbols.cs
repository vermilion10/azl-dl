namespace AzlDl.Core.Config;

// A global the data scripts read but never define, such as Vector3. Field reads extend the name ("UnityEngine.Color").
public sealed record LuaGlobalRef(string Name);

// A call the data scripts make while building a table, kept as data instead of run: Vector3(-105, 0, 58).
public sealed record LuaCall(object? Function, IReadOnlyList<object?> Arguments);
