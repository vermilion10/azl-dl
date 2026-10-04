using System.CommandLine;
using AzlDl.Core;
using AzlDl.Core.Download;
using AzlDl.Core.Gate;
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

        return await root.Parse(args).InvokeAsync();
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
