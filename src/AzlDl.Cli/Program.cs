using System.CommandLine;

namespace AzlDl.Cli;

// Targets System.CommandLine 3.0-preview.7 (SetAction, Options.Add, and Parse(args).InvokeAsync).
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var root = new RootCommand("Download and extract Azur Lane game assets.");
        return await root.Parse(args).InvokeAsync();
    }
}
