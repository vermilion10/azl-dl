namespace AzlDl.Core.Config;

// A Lua table as plain .NET data. Keys are long, double, string or bool; values are those, null, or another LuaTable.
public sealed class LuaTable
{
    public Dictionary<object, object?> Entries { get; } = [];

    public object? this[object key]
    {
        get => Entries.GetValueOrDefault(Normalize(key));
        set
        {
            if (value is null)
            {
                Entries.Remove(Normalize(key));
            }
            else
            {
                Entries[Normalize(key)] = value;
            }
        }
    }

    public int Count => Entries.Count;

    // A table whose keys are exactly 1..n, which Lua code treats as an array.
    public bool IsSequence()
    {
        for (long i = 1; i <= Entries.Count; i++)
        {
            if (!Entries.ContainsKey(i))
            {
                return false;
            }
        }

        return true;
    }

    // Lua treats 3 and 3.0 as the same key, so integral doubles are stored as longs.
    private static object Normalize(object key) =>
        key is double d && d == Math.Floor(d) && Math.Abs(d) < 9.007199254740992e15 ? (long)d : key;
}
