using System.Text;
using System.Text.Json;
using AzlDl.Core.Audio;
using AzlDl.Core.Config;

namespace AzlDl.Core.Unity;

// Files in the asset folders that aren't Unity bundles: CRI sound banks (cue/*.b, one entry per cue), the game's
// obfuscated Lua data tables (sharecfgdata, shown as JSON), plain text, and other formats, shown as bytes under the
// format's name. Entries carry File = Marker so callers can tell them from objects inside a bundle.
public static class LooseFileReader
{
    public const string Marker = "(file)";

    private const int MaxJsonBytes = 4 * 1024 * 1024;

    public static BundleReport Inspect(string path)
    {
        var data = File.ReadAllBytes(path);
        var name = Path.GetFileName(path);
        if (IsSoundBank(data))
        {
            var bank = AcbFile.Read(data);
            var cues = bank.CueNames.Order(StringComparer.Ordinal)
                .Select((cue, i) => new InspectedAsset(Marker, i, "Sound cue", cue))
                .ToList();
            return new BundleReport("CRI sound bank", [name], cues);
        }

        var format = Format(data);
        return new BundleReport(format, [name], [new InspectedAsset(Marker, 0, format, name)]);
    }

    public static AssetPreview Read(string path, InspectedAsset asset)
    {
        var data = File.ReadAllBytes(path);
        var name = string.Concat(asset.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (asset.Type == "Sound cue")
        {
            var wav = AcbFile.Read(data).ReadWav(asset.Name)
                      ?? throw new InvalidDataException($"The bank has no recording for {asset.Name}.");
            return new AssetPreview(PreviewKind.Audio, name + ".wav", null, Duration(wav), wav);
        }

        if (asset.Type == LuaTable && DecodeTables(data) is { } json)
        {
            var text = json.Length > MaxJsonBytes ? Encoding.UTF8.GetString(json, 0, MaxJsonBytes) + "\n…" : Encoding.UTF8.GetString(json);
            return new AssetPreview(PreviewKind.Text, name + ".json", null, text, json);
        }

        return AssetPreviewer.RawPreview(name, data);
    }

    private const string LuaTable = "Lua data table";

    // ACB banks are CRI @UTF tables.
    private static bool IsSoundBank(byte[] data) => data.AsSpan().StartsWith("@UTF"u8);

    private static string Format(byte[] data)
    {
        var head = data.AsSpan();
        if (head.StartsWith("CPK "u8))
        {
            return "CRI archive (CPK)";
        }

        if (head.StartsWith("AFS2"u8))
        {
            return "CRI wave archive (AWB)";
        }

        if (head.StartsWith("MZ"u8))
        {
            return "Windows program library";
        }

        if (AssetPreviewer.LooksLikeText(data))
        {
            return "Text";
        }

        return DecodeTables(data) is not null ? LuaTable : "Binary file";
    }

    // The sharecfgdata format: Lua statements filling pg.base tables, run here and written out as JSON.
    private static byte[]? DecodeTables(byte[] data)
    {
        try
        {
            var interpreter = new ConfigInterpreter();
            var prototypes = ConfigChunkReader.ReadAll(data);
            if (prototypes.Count == 0)
            {
                return null;
            }

            foreach (var prototype in prototypes)
            {
                interpreter.Run(prototype);
            }

            if (interpreter.Base.Count == 0)
            {
                return null;
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
                   {
                       Indented = true,
                       Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   }))
            {
                LuaJson.Write(writer, interpreter.Base.Count == 1 ? interpreter.Base.Entries.First().Value : interpreter.Base);
            }

            return stream.ToArray();
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    // "12.4 s" from the WAV header: data bytes over bytes per second.
    private static string Duration(byte[] wav)
    {
        if (wav.Length < 44)
        {
            return "";
        }

        var bytesPerSecond = BitConverter.ToInt32(wav, 28);
        var seconds = bytesPerSecond > 0 ? (wav.Length - 44) / (double)bytesPerSecond : 0;
        return seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s";
    }
}
