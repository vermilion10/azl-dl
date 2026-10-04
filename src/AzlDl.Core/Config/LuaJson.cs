using System.Globalization;
using System.Text.Json;

namespace AzlDl.Core.Config;

// Writes Lua tables as JSON: sequences (keys 1..n, including empty tables) become arrays, everything else an
// object with string keys.
// Entries keep the order the game assigned them in.
public static class LuaJson
{
    public static void Write(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d when double.IsFinite(d):
                writer.WriteNumberValue(d);
                break;
            case double d:
                writer.WriteStringValue(d.ToString(CultureInfo.InvariantCulture));
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            // Lua can't tell an empty list from an empty map; most empty fields in these tables are lists.
            case LuaTable table when table.IsSequence():
                writer.WriteStartArray();
                for (long i = 1; i <= table.Count; i++)
                {
                    Write(writer, table[i]);
                }

                writer.WriteEndArray();
                break;
            case LuaTable table:
                writer.WriteStartObject();
                foreach (var (key, item) in table.Entries)
                {
                    writer.WritePropertyName(KeyText(key));
                    Write(writer, item);
                }

                writer.WriteEndObject();
                break;
            default:
                throw new InvalidOperationException($"Can't write {value.GetType().Name} as JSON.");
        }
    }

    private static string KeyText(object key) => key switch
    {
        string s => s,
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => key.ToString() ?? "",
    };
}
