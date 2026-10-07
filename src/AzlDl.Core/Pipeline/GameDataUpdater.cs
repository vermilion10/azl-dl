using AzlDl.Core.Download;
using AzlDl.Core.Gate;
using AzlDl.Core.Versioning;

namespace AzlDl.Core.Pipeline;

public enum UpdateStage
{
    CheckingVersion,
    Downloading,
    DecodingTables,
    ExtractingScripts,
    RunningShareCfg,
    DecodingStories,
    Done,
}

public sealed record UpdateProgress(UpdateStage Stage, string Message);

public sealed record UpdateSummary(string Version, bool AlreadyCurrent, int Tables, int ShareCfgTables, int Stories,
    IReadOnlyList<string> Failures);

// Brings a data folder's game data up to date in one go: checks the main client version, downloads the config
// tables and the scripts bundle, and decodes them into config/, sharecfg/ and story/. The folder layout is the
// command-line tool's (AssetBundles/, cache/, scripts/, ...), so both can share one folder.
public sealed class GameDataUpdater(HttpClient http, ClientRegion region, string dataFolder)
{
    private const string VersionFile = "game-data-version.txt";
    private const string ScriptsBundle = "scripts64";
    private static readonly string ScriptRoot = Path.Combine("scripts", "assets", "luabuilds", "android", "arm64");

    public string AssetFolder => Path.Combine(dataFolder, "AssetBundles");

    // The main client version the decoded data in this folder came from, or null if it was never updated.
    public string? InstalledVersion =>
        File.Exists(Path.Combine(dataFolder, VersionFile)) ? File.ReadAllText(Path.Combine(dataFolder, VersionFile)).Trim() : null;

    public async Task<AssetVersion> GetLatestVersionAsync(CancellationToken cancellationToken = default)
    {
        var reply = await GateClient.QueryVersionsAsync(region, cancellationToken: cancellationToken);
        return reply.Versions.Select(AssetVersion.TryParse).FirstOrDefault(v => v?.Category == AssetCategory.Azl)
               ?? throw new InvalidDataException("The gate didn't report a main client version.");
    }

    public async Task<UpdateSummary> UpdateAsync(bool force = false, IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        void Report(UpdateStage stage, string message) => progress?.Report(new UpdateProgress(stage, message));

        Report(UpdateStage.CheckingVersion, "Asking the game server for the current version…");
        var version = await GetLatestVersionAsync(cancellationToken);
        if (!force && InstalledVersion == version.Version)
        {
            Report(UpdateStage.Done, $"Game data is already at {version.Version}.");
            return new UpdateSummary(version.Version, true, 0, 0, 0, []);
        }

        Report(UpdateStage.Downloading, $"Downloading the {version.Version} tables and scripts…");
        var cdn = new CdnClient(http, region, Path.Combine(dataFolder, "cache"));
        var rows = (await cdn.GetHashListAsync(version, cancellationToken))
            .Where(r => r.Path.StartsWith("sharecfgdata/", StringComparison.Ordinal) || r.Path == ScriptsBundle)
            .ToList();
        var download = await new AssetDownloader(http, cdn).DownloadAsync(rows, new DownloadOptions(AssetFolder), cancellationToken: cancellationToken);
        if (download.Failed > 0)
        {
            throw new IOException($"{download.Failed} files didn't download; nothing was decoded. Try again.");
        }

        var failures = new List<string>();

        Report(UpdateStage.DecodingTables, "Decoding the stat and skin tables…");
        var tables = await Task.Run(() => DataDecoders.DecodeConfig(
            Path.Combine(AssetFolder, "sharecfgdata"), Path.Combine(dataFolder, "config")), cancellationToken);
        failures.AddRange(tables.Failures);

        Report(UpdateStage.ExtractingScripts, "Decrypting the scripts bundle…");
        var scripts = await Task.Run(() => DataDecoders.ExtractScripts(
            Path.Combine(AssetFolder, ScriptsBundle), Path.Combine(dataFolder, "scripts")), cancellationToken);
        failures.AddRange(scripts.Failures);

        Report(UpdateStage.RunningShareCfg, "Building skill, retrofit and voice tables…");
        var shareCfg = await Task.Run(() => DataDecoders.RunShareCfg(
            Path.Combine(dataFolder, ScriptRoot, "sharecfg"), Path.Combine(dataFolder, "sharecfg")), cancellationToken);
        failures.AddRange(shareCfg.Failures);

        Report(UpdateStage.DecodingStories, "Decoding stories…");
        var stories = await Task.Run(() => DataDecoders.DecodeStories(
            Path.Combine(dataFolder, ScriptRoot, "gamecfg", "story"), Path.Combine(dataFolder, "story")), cancellationToken);
        failures.AddRange(stories.Failures);

        if (failures.Count == 0)
        {
            await File.WriteAllTextAsync(Path.Combine(dataFolder, VersionFile), version.Version, cancellationToken);
        }

        Report(UpdateStage.Done, failures.Count == 0
            ? $"Game data updated to {version.Version}."
            : $"Updated to {version.Version} with {failures.Count} files that couldn't be decoded.");
        return new UpdateSummary(version.Version, false, tables.Outputs, shareCfg.Outputs, stories.Outputs, failures);
    }
}
