using System.CommandLine;
using AzlDl.Core;
using AzlDl.Core.Config;
using AzlDl.Core.Download;
using AzlDl.Core.Gate;
using AzlDl.Core.Pipeline;
using AzlDl.Core.Scripts;
using AzlDl.Core.Unity;
using AzlDl.Core.Versioning;

namespace AzlDl.Cli;

// Targets System.CommandLine 3.0-preview.7 (SetAction, Options.Add, and Parse(args).InvokeAsync).
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var regionOption = new Option<string>("--region")
        {
            Description = $"Game client region ({string.Join(", ", ClientRegion.All.Select(r => r.Name))})",
            DefaultValueFactory = _ => ClientRegion.En.Name,
            Recursive = true,
        };

        var cacheOption = new Option<string>("--cache")
        {
            Description = "Where to keep downloaded hash lists",
            DefaultValueFactory = _ => Path.Combine("output", "cache"),
            Recursive = true,
        };

        var root = new RootCommand("Download and extract Azur Lane game assets.");
        root.Options.Add(regionOption);
        root.Options.Add(cacheOption);
        root.Subcommands.Add(BuildVersionsCommand(regionOption));
        root.Subcommands.Add(BuildHashesCommand(regionOption, cacheOption));
        root.Subcommands.Add(BuildDownloadCommand(regionOption, cacheOption));
        root.Subcommands.Add(BuildInspectCommand());
        root.Subcommands.Add(BuildConfigCommand());
        root.Subcommands.Add(BuildScriptsCommand());
        root.Subcommands.Add(BuildStoryCommand());
        root.Subcommands.Add(BuildShareCfgCommand());
        root.Subcommands.Add(BuildExtractCommand());
        root.Subcommands.Add(BuildAudioCommand());
        root.Subcommands.Add(BuildUpdateCommand(regionOption));

        return await root.Parse(args).InvokeAsync();
    }

    private static Command BuildUpdateCommand(Option<string> regionOption)
    {
        var dataOption = new Option<string>("--data")
        {
            Description = "Data folder to update (same layout as the other commands' output folder)",
            DefaultValueFactory = _ => "output",
        };

        var forceOption = new Option<bool>("--force")
        {
            Description = "Decode again even if the data is already at the current version",
        };

        var command = new Command("update", "Download and decode the current game data (tables, scripts, stories) in one step.");
        command.Options.Add(dataOption);
        command.Options.Add(forceOption);

        command.SetAction(async (parse, cancellationToken) =>
        {
            var region = RequireRegion(parse, regionOption);
            if (region is null)
            {
                return 1;
            }

            var updater = new GameDataUpdater(Http.Value, region, parse.GetValue(dataOption)!);
            try
            {
                var summary = await updater.UpdateAsync(parse.GetValue(forceOption),
                    new Progress<UpdateProgress>(p => Console.Error.WriteLine(p.Message)), cancellationToken);
                foreach (var failure in summary.Failures)
                {
                    Console.Error.WriteLine($"FAIL {failure}");
                }

                if (!summary.AlreadyCurrent)
                {
                    Console.Error.WriteLine(
                        $"{summary.Tables} config tables, {summary.ShareCfgTables} sharecfg tables, {summary.Stories} stories");
                }

                return summary.Failures.Count > 0 ? 1 : 0;
            }
            catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException
                                          or System.Net.Sockets.SocketException or OperationCanceledException)
            {
                Console.Error.WriteLine($"Update failed: {e.Message}");
                return 1;
            }
        });

        return command;
    }

    private static Command BuildExtractCommand()
    {
        var filtersArgument = new Argument<string[]>("filters")
        {
            Description = "Case-insensitive substring filters on the bundle path (e.g. painting/ qiye), AND-combined",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var assetsOption = new Option<string>("--assets")
        {
            Description = "Directory holding the downloaded bundles (the download command's --output)",
            DefaultValueFactory = _ => Path.Combine("output", "AssetBundles"),
        };

        var outputOption = new Option<string>("--output")
        {
            Description = "Directory to write images and files into",
            DefaultValueFactory = _ => Path.Combine("output", "art"),
        };

        var concurrencyOption = new Option<int>("--concurrency")
        {
            Description = "Bundles processed in parallel",
            DefaultValueFactory = _ => Math.Clamp(Environment.ProcessorCount, 1, 8),
        };

        var command = new Command("extract", "Convert downloaded bundles to PNGs and files; paintings are reassembled from their tiles.");
        command.Arguments.Add(filtersArgument);
        command.Options.Add(assetsOption);
        command.Options.Add(outputOption);
        command.Options.Add(concurrencyOption);

        command.SetAction(parse =>
        {
            var assets = parse.GetValue(assetsOption)!;
            if (!Directory.Exists(assets))
            {
                Console.Error.WriteLine($"No {assets} folder. Download some bundles first.");
                return 1;
            }

            var filters = parse.GetValue(filtersArgument) ?? [];
            var root = Path.GetFullPath(assets);
            var bundles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .Where(p => Path.GetExtension(p).Length == 0 && Matches(p, filters))
                .Where(p => BundleInspector.IsUnityFs(Path.Combine(root, p)))
                // A painting's _tex bundle produces the image; the bare one only holds its prefab.
                .Where(p => !p.StartsWith("painting/", StringComparison.Ordinal) || p.EndsWith("_tex", StringComparison.Ordinal)
                            || !File.Exists(Path.Combine(root, p + "_tex")))
                .Order(StringComparer.Ordinal)
                .ToList();
            if (bundles.Count == 0)
            {
                Console.Error.WriteLine("No downloaded bundles match.");
                return 1;
            }

            var extractor = new AzlDl.Core.Art.ArtExtractor(root, parse.GetValue(outputOption)!);
            var summary = extractor.Extract(bundles, parse.GetValue(concurrencyOption), new Progress<string>(Console.Error.WriteLine));
            foreach (var error in summary.Errors)
            {
                Console.Error.WriteLine($"FAIL {error}");
            }

            Console.Error.WriteLine(
                $"{summary.Bundles} bundles: {summary.Paintings} paintings, {summary.Images} images, {summary.Files} files, {summary.Errors.Count} failed");
            return summary.Errors.Count > 0 ? 1 : 0;
        });

        return command;
    }

    private static Command BuildAudioCommand()
    {
        var filtersArgument = new Argument<string[]>("filters")
        {
            Description = "Case-insensitive substring filters on the bank path (e.g. cv-10706), AND-combined",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var assetsOption = new Option<string>("--assets")
        {
            Description = "Directory holding the downloaded files (the download command's --output)",
            DefaultValueFactory = _ => Path.Combine("output", "AssetBundles"),
        };

        var outputOption = new Option<string>("--output")
        {
            Description = "Directory to write <bank>/<cue>.wav files into",
            DefaultValueFactory = _ => Path.Combine("output", "audio"),
        };

        var command = new Command("audio", "Decode downloaded voice banks (cue/*.b, the Cv category) into WAV files, one per cue.");
        command.Arguments.Add(filtersArgument);
        command.Options.Add(assetsOption);
        command.Options.Add(outputOption);

        command.SetAction(parse =>
        {
            var cueFolder = Path.Combine(parse.GetValue(assetsOption)!, "cue");
            var filters = parse.GetValue(filtersArgument) ?? [];
            var banks = Directory.Exists(cueFolder)
                ? Directory.EnumerateFiles(cueFolder, "*.b").Where(f => Matches("cue/" + Path.GetFileName(f), filters)).Order(StringComparer.Ordinal).ToList()
                : [];
            if (banks.Count == 0)
            {
                Console.Error.WriteLine($"No voice banks in {cueFolder} match. Download the Cv category first.");
                return 1;
            }

            var output = parse.GetValue(outputOption)!;
            int written = 0, failed = 0;
            Parallel.ForEach(banks, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8) }, bank =>
            {
                try
                {
                    var acb = AzlDl.Core.Audio.AcbFile.Read(bank);
                    var folder = Path.Combine(output, Path.GetFileNameWithoutExtension(bank));
                    Directory.CreateDirectory(folder);
                    foreach (var cue in acb.CueNames)
                    {
                        if (acb.ReadWav(cue) is { } wav)
                        {
                            File.WriteAllBytes(Path.Combine(folder, cue + ".wav"), wav);
                            Interlocked.Increment(ref written);
                        }
                    }
                }
                catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or IndexOutOfRangeException)
                {
                    Interlocked.Increment(ref failed);
                    Console.Error.WriteLine($"FAIL {Path.GetFileName(bank)}: {e.Message}");
                }
            });

            Console.Error.WriteLine($"{banks.Count} banks: {written} voice lines, {failed} failed");
            return failed > 0 ? 1 : 0;
        });

        return command;
    }

    private static Command BuildShareCfgCommand()
    {
        var filtersArgument = new Argument<string[]>("filters")
        {
            Description = "Case-insensitive substring filters on the sharecfg file name, AND-combined",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var sourceOption = new Option<string>("--source")
        {
            Description = "Folder of sharecfg scripts (written by the scripts command)",
            DefaultValueFactory = _ => Path.Combine("output", "scripts", "assets", "luabuilds", "android", "arm64", "sharecfg"),
        };

        var outputOption = new Option<string>("--output")
        {
            Description = "Directory to write one JSON file per table into",
            DefaultValueFactory = _ => Path.Combine("output", "sharecfg"),
        };

        var command = new Command("sharecfg", "Run the sharecfg scripts and write the tables they build as JSON.");
        command.Arguments.Add(filtersArgument);
        command.Options.Add(sourceOption);
        command.Options.Add(outputOption);

        command.SetAction(parse =>
        {
            var source = parse.GetValue(sourceOption)!;
            if (!Directory.Exists(source))
            {
                Console.Error.WriteLine($"No {source} folder. Run the scripts command first.");
                return 1;
            }

            var filters = parse.GetValue(filtersArgument) ?? [];
            var output = parse.GetValue(outputOption)!;
            var result = DataDecoders.RunShareCfg(source, output, name => Matches(name, filters));
            return Report(result, $"{result.Inputs - result.Failures.Count} scripts ran, {result.Failures.Count} failed, {result.Outputs} tables written to {output}");
        });

        return command;
    }

    private static Command BuildStoryCommand()
    {
        var filtersArgument = new Argument<string[]>("filters")
        {
            Description = "Case-insensitive substring filters on the story file name, AND-combined",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var sourceOption = new Option<string>("--source")
        {
            Description = "Folder of story scripts (written by the scripts command)",
            DefaultValueFactory = _ => Path.Combine("output", "scripts", "assets", "luabuilds", "android", "arm64", "gamecfg", "story"),
        };

        var outputOption = new Option<string>("--output")
        {
            Description = "Directory to write one JSON file per story into",
            DefaultValueFactory = _ => Path.Combine("output", "story"),
        };

        var command = new Command("story", "Decode the story scripts into JSON.");
        command.Arguments.Add(filtersArgument);
        command.Options.Add(sourceOption);
        command.Options.Add(outputOption);

        command.SetAction(parse =>
        {
            var source = parse.GetValue(sourceOption)!;
            if (!Directory.Exists(source))
            {
                Console.Error.WriteLine($"No {source} folder. Run the scripts command first.");
                return 1;
            }

            var filters = parse.GetValue(filtersArgument) ?? [];
            var output = parse.GetValue(outputOption)!;
            var result = DataDecoders.DecodeStories(source, output, name => Matches(name, filters));
            return Report(result, $"{result.Outputs} stories written to {output}, {result.Failures.Count} failed");
        });

        return command;
    }

    private static int Report(DecodeResult result, string summary)
    {
        foreach (var failure in result.Failures)
        {
            Console.Error.WriteLine($"FAIL {failure}");
        }

        Console.Error.WriteLine(summary);
        return result.Failures.Count > 0 ? 1 : 0;
    }

    private static Command BuildScriptsCommand()
    {
        var bundleOption = new Option<string>("--bundle")
        {
            Description = "The downloaded scripts bundle",
            DefaultValueFactory = _ => Path.Combine("output", "AssetBundles", "scripts64"),
        };

        var outputOption = new Option<string>("--output")
        {
            Description = "Directory to write the Lua bytecode files into, keeping their in-game paths",
            DefaultValueFactory = _ => Path.Combine("output", "scripts"),
        };

        var command = new Command("scripts", "Decrypt the scripts bundle and write out every Lua script it holds.");
        command.Options.Add(bundleOption);
        command.Options.Add(outputOption);

        command.SetAction(parse =>
        {
            var bundlePath = parse.GetValue(bundleOption)!;
            if (!File.Exists(bundlePath))
            {
                Console.Error.WriteLine($"No file at {bundlePath}. Run 'download azl scripts64' first.");
                return 1;
            }

            var output = parse.GetValue(outputOption)!;
            try
            {
                var result = DataDecoders.ExtractScripts(bundlePath, output);
                return Report(result, $"{result.Outputs} scripts written to {output}");
            }
            catch (InvalidDataException e)
            {
                Console.Error.WriteLine(e.Message);
                return 1;
            }
        });

        return command;
    }

    private static Command BuildConfigCommand()
    {
        var filtersArgument = new Argument<string[]>("filters")
        {
            Description = "Case-insensitive substring filters on the sharecfgdata file name, AND-combined",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var assetsOption = new Option<string>("--assets")
        {
            Description = "Directory holding the downloaded bundles (the download command's --output)",
            DefaultValueFactory = _ => Path.Combine("output", "AssetBundles"),
        };

        var outputOption = new Option<string>("--output")
        {
            Description = "Directory to write one JSON file per table into",
            DefaultValueFactory = _ => Path.Combine("output", "config"),
        };

        var command = new Command("config", "Decode the downloaded sharecfgdata tables into JSON.");
        command.Arguments.Add(filtersArgument);
        command.Options.Add(assetsOption);
        command.Options.Add(outputOption);

        command.SetAction(parse =>
        {
            var source = Path.Combine(parse.GetValue(assetsOption)!, "sharecfgdata");
            if (!Directory.Exists(source))
            {
                Console.Error.WriteLine($"No {source} folder. Run 'download azl sharecfgdata/' first.");
                return 1;
            }

            var filters = parse.GetValue(filtersArgument) ?? [];
            var output = parse.GetValue(outputOption)!;
            var result = DataDecoders.DecodeConfig(source, output, name => Matches(name, filters), new Progress<string>(Console.WriteLine));
            if (result.Inputs == 0)
            {
                Console.Error.WriteLine($"No sharecfgdata files in {source} match.");
                return 1;
            }

            return Report(result, $"{result.Outputs} tables written to {output}, {result.Failures.Count} files failed");
        });

        return command;
    }

    private static Command BuildInspectCommand()
    {
        var pathArgument = new Argument<string>("bundle")
        {
            Description = "Path to a downloaded bundle file",
        };

        var dumpOption = new Option<string?>("--dump")
        {
            Description = "Print the full field tree of the asset with this name instead of listing objects",
        };

        var command = new Command("inspect", "List the objects inside one downloaded bundle.");
        command.Arguments.Add(pathArgument);
        command.Options.Add(dumpOption);

        command.SetAction(parse =>
        {
            var path = parse.GetValue(pathArgument)!;
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"No file at {path}");
                return 1;
            }

            if (parse.GetValue(dumpOption) is { } dumpName && BundleInspector.IsUnityFs(path))
            {
                if (BundleInspector.Dump(path, dumpName, Console.Out) == 0)
                {
                    Console.Error.WriteLine($"No asset named '{dumpName}' in {path}");
                    return 1;
                }

                return 0;
            }

            if (!BundleInspector.IsUnityFs(path))
            {
                var head = new byte[16];
                using (var stream = File.OpenRead(path))
                {
                    stream.ReadExactly(head, 0, (int)Math.Min(head.Length, stream.Length));
                }

                Console.Error.WriteLine($"{path} isn't a plain UnityFS bundle. First bytes: {Convert.ToHexString(head)}");
                return 1;
            }

            var report = BundleInspector.Inspect(path);
            Console.WriteLine($"unity  {report.UnityVersion}");
            foreach (var file in report.Files)
            {
                Console.WriteLine($"file   {file}");
            }

            foreach (var asset in report.Assets)
            {
                Console.WriteLine($"{asset.Type,-16} {asset.PathId,22}  {asset.Name}");
            }

            return 0;
        });

        return command;
    }

    private static Command BuildDownloadCommand(Option<string> regionOption, Option<string> cacheOption)
    {
        var categoryArgument = new Argument<string>("category")
        {
            Description = "Asset category to download",
        };

        var filtersArgument = new Argument<string[]>("filters")
        {
            Description = "Case-insensitive substring filters on the bundle path, AND-combined",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var outputOption = new Option<string>("--output")
        {
            Description = "Directory the bundles are mirrored into, keeping their paths",
            DefaultValueFactory = _ => Path.Combine("output", "AssetBundles"),
        };

        var limitOption = new Option<int>("--limit")
        {
            Description = "Download at most this many files (0 for no limit)",
        };

        var concurrencyOption = new Option<int>("--concurrency")
        {
            Description = "Parallel downloads",
            DefaultValueFactory = _ => 8,
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Show what would be downloaded without downloading it",
        };

        var command = new Command("download", "Download a category's bundles from the CDN, skipping files that are already current.");
        command.Arguments.Add(categoryArgument);
        command.Arguments.Add(filtersArgument);
        command.Options.Add(outputOption);
        command.Options.Add(limitOption);
        command.Options.Add(concurrencyOption);
        command.Options.Add(dryRunOption);

        command.SetAction(async (parse, cancellationToken) =>
        {
            var region = RequireRegion(parse, regionOption);
            if (region is null)
            {
                return 1;
            }

            var cacheDirectory = parse.GetValue(cacheOption)!;
            var resolved = await ResolveHashListAsync(region, parse.GetValue(categoryArgument)!, cacheDirectory, cancellationToken);
            if (resolved is null)
            {
                return 1;
            }

            var (version, rows) = resolved.Value;
            var filters = parse.GetValue(filtersArgument) ?? [];
            var selected = rows.Where(r => Matches(r.Path, filters)).ToList();

            var downloader = new AssetDownloader(Http.Value, new CdnClient(Http.Value, region, cacheDirectory));
            var options = new DownloadOptions(
                parse.GetValue(outputOption)!,
                parse.GetValue(concurrencyOption),
                parse.GetValue(dryRunOption),
                parse.GetValue(limitOption));

            var log = new Progress<string>(Console.WriteLine);
            var summary = await downloader.DownloadAsync(selected, options, log, cancellationToken);

            Console.Error.WriteLine(
                $"{version.Category} {version.Version}: {summary.Downloaded} downloaded ({FormatSize(summary.Bytes)}), " +
                $"{summary.Skipped} already current, {summary.Failed} failed");
            return summary.Failed > 0 ? 1 : 0;
        });

        return command;
    }

    private static Command BuildHashesCommand(Option<string> regionOption, Option<string> cacheOption)
    {
        var categoryArgument = new Argument<string>("category")
        {
            Description = $"Asset category ({string.Join(", ", Enum.GetNames<AssetCategory>()).ToLowerInvariant()})",
        };

        var filtersArgument = new Argument<string[]>("filters")
        {
            Description = "Case-insensitive substring filters on the bundle path, AND-combined",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var statsOption = new Option<bool>("--stats")
        {
            Description = "Print file counts and sizes per top-level folder instead of listing files",
        };

        var command = new Command("hashes", "List the files in a category's current hash list.");
        command.Arguments.Add(categoryArgument);
        command.Arguments.Add(filtersArgument);
        command.Options.Add(statsOption);

        command.SetAction(async (parse, cancellationToken) =>
        {
            var region = RequireRegion(parse, regionOption);
            if (region is null)
            {
                return 1;
            }

            var resolved = await ResolveHashListAsync(
                region, parse.GetValue(categoryArgument)!, parse.GetValue(cacheOption)!, cancellationToken);
            if (resolved is null)
            {
                return 1;
            }

            var (version, rows) = resolved.Value;
            var filters = parse.GetValue(filtersArgument) ?? [];
            var matched = rows.Where(r => Matches(r.Path, filters)).ToList();

            if (parse.GetValue(statsOption))
            {
                foreach (var group in matched.GroupBy(r => r.TopDirectory).OrderByDescending(g => g.Sum(r => r.Size)))
                {
                    var name = group.Key.Length == 0 ? "(root)" : group.Key;
                    Console.WriteLine($"{name,-32} {group.Count(),7} files  {FormatSize(group.Sum(r => r.Size)),10}");
                }
            }
            else
            {
                foreach (var row in matched)
                {
                    Console.WriteLine($"{row.Path}  {FormatSize(row.Size)}  {row.Md5}");
                }
            }

            Console.Error.WriteLine(
                $"{version.Category} {version.Version}: {matched.Count} of {rows.Count} files, {FormatSize(matched.Sum(r => r.Size))}");
            return 0;
        });

        return command;
    }

    private static async Task<(AssetVersion Version, IReadOnlyList<HashRow> Rows)?> ResolveHashListAsync(
        ClientRegion region, string categoryName, string cacheDirectory, CancellationToken cancellationToken)
    {
        if (AssetVersion.ParseCategory(categoryName) is not { } category)
        {
            Console.Error.WriteLine(
                $"Unknown category '{categoryName}'. Use one of: {string.Join(", ", Enum.GetNames<AssetCategory>()).ToLowerInvariant()}");
            return null;
        }

        try
        {
            var reply = await GateClient.QueryVersionsAsync(region, cancellationToken: cancellationToken);
            var version = reply.Versions.Select(AssetVersion.TryParse).FirstOrDefault(v => v?.Category == category);
            if (version is null)
            {
                Console.Error.WriteLine($"The gate didn't report a version for {category}.");
                return null;
            }

            var cdn = new CdnClient(Http.Value, region, cacheDirectory);
            return (version, await cdn.GetHashListAsync(version, cancellationToken));
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or HttpRequestException
                                      or InvalidDataException or OperationCanceledException)
        {
            Console.Error.WriteLine($"Couldn't get the {category} hash list: {e.Message}");
            return null;
        }
    }

    private static readonly Lazy<HttpClient> Http = new(() => new HttpClient { Timeout = TimeSpan.FromMinutes(5) });

    private static bool Matches(string path, string[] filters) =>
        filters.All(f => path.Contains(f, StringComparison.OrdinalIgnoreCase));

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => FormattableString.Invariant($"{bytes / (double)(1L << 30):0.00} GiB"),
        >= 1L << 20 => FormattableString.Invariant($"{bytes / (double)(1L << 20):0.0} MiB"),
        >= 1L << 10 => FormattableString.Invariant($"{bytes / 1024.0:0.0} KiB"),
        _ => $"{bytes} B",
    };

    private static Command BuildVersionsCommand(Option<string> regionOption)
    {
        var rawOption = new Option<bool>("--raw")
        {
            Description = "Print the gate's full reply, including entries that aren't asset versions",
        };

        var command = new Command("versions", "Ask the gate server for the current version of every asset category.");
        command.Options.Add(rawOption);

        command.SetAction(async (parse, cancellationToken) =>
        {
            var region = RequireRegion(parse, regionOption);
            if (region is null)
            {
                return 1;
            }

            GateResponse reply;
            try
            {
                reply = await GateClient.QueryVersionsAsync(region, cancellationToken: cancellationToken);
            }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException
                                          or InvalidDataException or OperationCanceledException)
            {
                Console.Error.WriteLine($"Couldn't get versions from {region.GateHost}:{region.GatePort}: {e.Message}");
                return 1;
            }

            if (parse.GetValue(rawOption))
            {
                Console.WriteLine($"gateway  {reply.GatewayIp}:{reply.GatewayPort}");
                Console.WriteLine($"url      {reply.Url}");
                foreach (var cdn in reply.CdnList)
                {
                    Console.WriteLine($"cdn      {cdn}");
                }

                foreach (var version in reply.Versions)
                {
                    Console.WriteLine($"version  {version}");
                }

                return 0;
            }

            foreach (var raw in reply.Versions)
            {
                if (AssetVersion.TryParse(raw) is { } version)
                {
                    Console.WriteLine($"{version.Category,-9} {version.Version,-12} {version.Hash}");
                }
            }

            return 0;
        });

        return command;
    }

    private static ClientRegion? RequireRegion(ParseResult parse, Option<string> regionOption)
    {
        var name = parse.GetValue(regionOption) ?? ClientRegion.En.Name;
        var region = ClientRegion.Find(name);
        if (region is null)
        {
            Console.Error.WriteLine($"Unknown region '{name}'. Supported: {string.Join(", ", ClientRegion.All.Select(r => r.Name))}");
        }

        return region;
    }
}
