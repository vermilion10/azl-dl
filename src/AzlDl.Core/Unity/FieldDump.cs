using System.Text;
using AssetsTools.NET;

namespace AzlDl.Core.Unity;

// Prints an asset's field tree as indented "name (type): value" lines. Long arrays and byte blobs are cut short.
public static class FieldDump
{
    private const int MaxArrayItems = 8;
    private const int MaxBytes = 32;

    public static void Write(TextWriter output, AssetTypeValueField field, int depth = 0)
    {
        var indent = new string(' ', depth * 2);
        var line = new StringBuilder($"{indent}{field.FieldName} ({field.TypeName})");

        if (field.Value is { } value && field.Children.Count == 0)
        {
            line.Append(": ").Append(Describe(value));
            output.WriteLine(line);
            return;
        }

        if (field.Value?.ValueType == AssetValueType.ByteArray)
        {
            line.Append(": ").Append(Describe(field.Value));
            output.WriteLine(line);
            return;
        }

        var isArray = field.TemplateField.IsArray;
        if (isArray)
        {
            line.Append($" [{field.Children.Count}]");
        }

        output.WriteLine(line);
        var shown = isArray ? field.Children.Take(MaxArrayItems) : field.Children;
        foreach (var child in shown)
        {
            Write(output, child, depth + 1);
        }

        if (isArray && field.Children.Count > MaxArrayItems)
        {
            output.WriteLine($"{indent}  ... {field.Children.Count - MaxArrayItems} more");
        }
    }

    private static string Describe(AssetTypeValue value) => value.ValueType switch
    {
        AssetValueType.ByteArray => $"{value.AsByteArray.Length} bytes {Convert.ToHexString(value.AsByteArray.AsSpan(0, Math.Min(MaxBytes, value.AsByteArray.Length)))}",
        AssetValueType.String => $"\"{value.AsString}\"",
        _ => Convert.ToString(value.AsObject, System.Globalization.CultureInfo.InvariantCulture) ?? "null",
    };
}
