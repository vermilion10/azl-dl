using AzlDl.Core.Config;
using AzlDl.Core.Scripts;
using AzlDl.Core.Unity;

namespace AzlDl.Core.Pipeline;

public sealed record DecodeResult(int Inputs, int Outputs, IReadOnlyList<string> Failures);

// The decode steps behind the config, scripts, sharecfg and story commands, shared with the app.
// Each takes an optional name filter (true keeps the file) and reports one line per finished input.
public static class DataDecoders
{
    private const string ScriptSuffix = ".lua.bytes";

    // sharecfgdata/* -> one JSON file per pg.base table.
    public static DecodeResult DecodeConfig(string sharecfgdataFolder, string outputFolder, Func<string, bool>? include = null,
        IProgress<string>? log = null)
    {
        var files = Directory.EnumerateFiles(sharecfgdataFolder)
            .Where(f => Path.GetExtension(f).Length == 0 && (include?.Invoke(Path.GetFileName(f)) ?? true))
            .Order(StringComparer.Ordinal)
            .ToList();

        var interpreter = new ConfigInterpreter();
        var failures = new List<string>();
        foreach (var file in files)
        {
            try
            {
                var count = 0;
                foreach (var prototype in ConfigChunkReader.ReadAll(File.ReadAllBytes(file)))
                {
                    interpreter.Run(prototype);
                    count++;
                }

                log?.Report($"{Path.GetFileName(file),-34} {count,7} entries");
            }
            catch (InvalidDataException e)
            {
                failures.Add($"{Path.GetFileName(file)}: {e.Message}");
            }
        }

        Directory.CreateDirectory(outputFolder);
        foreach (var (name, table) in interpreter.Base.Entries)
        {
            LuaJson.WriteFile(Path.Combine(outputFolder, $"{name}.json"), table);
        }

        return new DecodeResult(files.Count, interpreter.Base.Count, failures);
    }

    // scripts64 (encrypted or already decrypted) -> every Lua script at its in-game path.
    public static DecodeResult ExtractScripts(string bundlePath, string outputFolder)
    {
        var data = File.ReadAllBytes(bundlePath);
        if (ScriptBundleCipher.LooksEncrypted(data))
        {
            data = ScriptBundleCipher.Decrypt(data);
        }
        else if (!BundleInspector.IsUnityFsBytes(data))
        {
            throw new InvalidDataException($"{bundlePath} is neither a bundle nor an encrypted scripts bundle.");
        }

        var root = Path.GetFullPath(outputFolder);
        var written = 0;
        var failures = new List<string>();
        foreach (var script in ScriptBundle.ReadScripts(data))
        {
            var target = Path.GetFullPath(Path.Combine(root, script.Path));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{script.Path}: path leaves the output folder");
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, script.Data);
            written++;
        }

        return new DecodeResult(1, written, failures);
    }

    // sharecfg/**/*.lua.bytes -> one JSON file per table. The game keeps a table's rows in pg.base.<name> and its
    // lookup lists (all, indexes) in pg.<name>; the two are merged.
    public static DecodeResult RunShareCfg(string sharecfgFolder, string outputFolder, Func<string, bool>? include = null)
    {
        var interpreter = new ConfigInterpreter();
        var failures = new List<string>();
        var ran = 0;
        foreach (var file in Directory.EnumerateFiles(sharecfgFolder, "*" + ScriptSuffix, SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            var name = ScriptName(file);
            if (!(include?.Invoke(name) ?? true))
            {
                continue;
            }

            try
            {
                foreach (var prototype in ConfigChunkReader.ReadAll(File.ReadAllBytes(file)))
                {
                    interpreter.Run(prototype);
                }

                ran++;
            }
            catch (Exception e) when (e is InvalidDataException or InvalidCastException or IndexOutOfRangeException)
            {
                failures.Add($"{name}: {e.Message}");
            }
        }

        Directory.CreateDirectory(outputFolder);
        var written = 0;
        var names = interpreter.Pg.Entries.Keys.OfType<string>()
            .Concat(interpreter.Base.Entries.Keys.OfType<string>())
            .Where(n => n != "base")
            .Distinct();
        foreach (var name in names)
        {
            var merged = new LuaTable();
            foreach (var part in new[] { interpreter.Base[name], interpreter.Pg[name] })
            {
                if (part is not LuaTable table)
                {
                    continue;
                }

                foreach (var (key, value) in table.Entries)
                {
                    if (!Equals(key, "__name"))
                    {
                        merged[key] = value;
                    }
                }
            }

            if (merged.Count > 0)
            {
                LuaJson.WriteFile(Path.Combine(outputFolder, $"{name}.json"), merged);
                written++;
            }
        }

        return new DecodeResult(ran + failures.Count, written, failures);
    }

    // gamecfg/story/*.lua.bytes -> one JSON file per story.
    public static DecodeResult DecodeStories(string storyFolder, string outputFolder, Func<string, bool>? include = null)
    {
        Directory.CreateDirectory(outputFolder);
        var inputs = 0;
        var written = 0;
        var failures = new List<string>();
        foreach (var file in Directory.EnumerateFiles(storyFolder, "*" + ScriptSuffix).Order(StringComparer.Ordinal))
        {
            var name = ScriptName(file);
            if (!(include?.Invoke(name) ?? true))
            {
                continue;
            }

            inputs++;
            try
            {
                var interpreter = new ConfigInterpreter();
                object? story = null;
                foreach (var prototype in ConfigChunkReader.ReadAll(File.ReadAllBytes(file)))
                {
                    story = interpreter.Run(prototype);
                }

                LuaJson.WriteFile(Path.Combine(outputFolder, $"{name}.json"),
                    story ?? throw new InvalidDataException("The script doesn't return a table."));
                written++;
            }
            catch (Exception e) when (e is InvalidDataException or InvalidCastException or IndexOutOfRangeException)
            {
                failures.Add($"{name}: {e.Message}");
            }
        }

        return new DecodeResult(inputs, written, failures);
    }

    private static string ScriptName(string file) => Path.GetFileName(file)[..^ScriptSuffix.Length];
}
