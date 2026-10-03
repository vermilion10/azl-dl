using System.CommandLine;
using AzlDl.Core;
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

        var root = new RootCommand("Download and extract Azur Lane game assets.");
        root.Options.Add(regionOption);
        root.Subcommands.Add(BuildVersionsCommand(regionOption));

        return await root.Parse(args).InvokeAsync();
    }

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
