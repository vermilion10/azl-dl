using System.Collections.ObjectModel;
using AzlDl.App.Settings;
using AzlDl.Core;
using AzlDl.Core.Download;
using AzlDl.Core.Gate;
using AzlDl.Core.Pipeline;
using AzlDl.Core.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzlDl.App.ViewModels;

public enum StepState
{
    Waiting,
    Running,
    Done,
}

public sealed partial class UpdateStep(UpdateStage stage, string title) : ObservableObject
{
    public UpdateStage Stage { get; } = stage;

    public string Title { get; } = title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Marker))]
    private StepState state;

    public string Marker => State switch
    {
        StepState.Done => "done",
        StepState.Running => "working",
        _ => "",
    };
}

// One asset category: what the server has, what is on disk, and its own download.
public sealed partial class CategoryRow(AssetVersion version, IReadOnlyList<HashRow> rows, Func<CategoryRow, Task> download)
    : ObservableObject
{
    public AssetVersion Version { get; } = version;

    public IReadOnlyList<HashRow> Rows { get; } = rows;

    // What each category holds, going by its files' folders.
    private static readonly Dictionary<AssetCategory, string> Labels = new()
    {
        [AssetCategory.Azl] = "Main client",
        [AssetCategory.Painting] = "Paintings",
        [AssetCategory.Live2D] = "Live2D",
        [AssetCategory.Cv] = "Voice lines",
        [AssetCategory.Bgm] = "Music",
        [AssetCategory.Pic] = "Gallery pictures",
        [AssetCategory.Manga] = "Manga",
        [AssetCategory.Cipher] = "Sound stories",
        [AssetCategory.Dorm] = "Dorm",
        [AssetCategory.Map] = "Maps",
    };

    public string Name => Labels.GetValueOrDefault(Version.Category, Version.Category.ToString());

    public string InternalName => Version.Category.ToString().ToLowerInvariant();

    public string Remote => $"{Format.Count(Rows.Count)} files, {Format.Size(Rows.Sum(r => r.Size))}";

    [ObservableProperty]
    private string local = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private bool confirming;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private bool busy;

    [ObservableProperty]
    private double progress;

    [ObservableProperty]
    private long missingBytes;

    public bool CanDownload => !Busy && !Confirming && MissingBytes > 0;

    public string ConfirmText => $"Download {Format.Size(MissingBytes)} of {Name} files?";

    [RelayCommand]
    private void AskDownload()
    {
        OnPropertyChanged(nameof(ConfirmText));
        Confirming = true;
    }

    [RelayCommand]
    private void CancelDownload() => Confirming = false;

    [RelayCommand]
    private Task ConfirmDownload()
    {
        Confirming = false;
        return download(this);
    }
}

public sealed partial class SyncViewModel : ObservableObject
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly AppSettings settings;
    private readonly Func<Task> onGameDataUpdated;

    public SyncViewModel(AppSettings settings, Func<Task> onGameDataUpdated)
    {
        this.settings = settings;
        this.onGameDataUpdated = onGameDataUpdated;
        Steps =
        [
            new(UpdateStage.CheckingVersion, "Check the game version"),
            new(UpdateStage.Downloading, "Download tables and scripts"),
            new(UpdateStage.DecodingTables, "Decode stat and skin tables"),
            new(UpdateStage.ExtractingScripts, "Decrypt the scripts bundle"),
            new(UpdateStage.RunningShareCfg, "Build skill, retrofit and voice tables"),
            new(UpdateStage.DecodingStories, "Decode stories"),
        ];
        RefreshInstalled();
    }

    public IReadOnlyList<UpdateStep> Steps { get; }

    public ObservableCollection<CategoryRow> Categories { get; } = [];

    public ObservableCollection<string> Failures { get; } = [];

    [ObservableProperty]
    private string installedVersion = "";

    [ObservableProperty]
    private string status = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateGameDataCommand), nameof(CheckCategoriesCommand))]
    private bool updating;

    [ObservableProperty]
    private bool checkingCategories;

    [ObservableProperty]
    private string categoryStatus = "Check the server to see each category's size and what's already downloaded.";

    private GameDataUpdater Updater() => new(Http, ClientRegion.En, settings.DataFolder);

    public void RefreshInstalled() =>
        InstalledVersion = Updater().InstalledVersion is { } v ? $"Game data {v} in {settings.DataFolder}" : $"No game data yet in {settings.DataFolder}";

    private bool CanUpdate() => !Updating;

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateGameDataAsync()
    {
        Updating = true;
        Failures.Clear();
        foreach (var step in Steps)
        {
            step.State = StepState.Waiting;
        }

        var progress = new Progress<UpdateProgress>(p =>
        {
            Status = p.Message;
            foreach (var step in Steps)
            {
                step.State = step.Stage < p.Stage ? StepState.Done : step.Stage == p.Stage ? StepState.Running : StepState.Waiting;
            }
        });

        try
        {
            var summary = await Task.Run(() => Updater().UpdateAsync(progress: progress));
            foreach (var failure in summary.Failures)
            {
                Failures.Add(failure);
            }

            foreach (var step in Steps)
            {
                step.State = StepState.Done;
            }

            Status = summary.AlreadyCurrent
                ? $"Already up to date ({summary.Version})."
                : $"Updated to {summary.Version}: {summary.Tables + summary.ShareCfgTables} tables and {Format.Count(summary.Stories)} stories.";
            RefreshInstalled();
            await onGameDataUpdated();
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException
                                      or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            Status = $"The update stopped: {e.Message} Nothing already decoded was removed; try again.";
        }
        finally
        {
            Updating = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task CheckCategoriesAsync()
    {
        CheckingCategories = true;
        CategoryStatus = "Reading the server's file lists…";
        try
        {
            var reply = await GateClient.QueryVersionsAsync(ClientRegion.En);
            var cdn = new CdnClient(Http, ClientRegion.En, Path.Combine(settings.DataFolder, "cache"));
            var rows = new List<CategoryRow>();
            foreach (var version in reply.Versions.Select(AssetVersion.TryParse).OfType<AssetVersion>())
            {
                rows.Add(new CategoryRow(version, await cdn.GetHashListAsync(version), DownloadCategoryAsync));
            }

            Categories.Clear();
            foreach (var row in rows.OrderByDescending(r => r.Rows.Sum(x => x.Size)))
            {
                Categories.Add(row);
            }

            RefreshLocal();
            CategoryStatus = $"{Categories.Count} categories on the server.";
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException
                                      or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            CategoryStatus = $"Couldn't reach the server: {e.Message}";
        }
        finally
        {
            CheckingCategories = false;
        }
    }

    private void RefreshLocal()
    {
        var state = AssetDownloader.ReadState(Path.Combine(settings.DataFolder, "AssetBundles"));
        foreach (var row in Categories)
        {
            var have = row.Rows.Where(r => state.TryGetValue(r.Path, out var md5) && md5 == r.Md5).ToList();
            row.Local = $"{Format.Count(have.Count)} downloaded, {Format.Size(have.Sum(r => r.Size))}";
            row.MissingBytes = row.Rows.Sum(r => r.Size) - have.Sum(r => r.Size);
        }
    }

    private async Task DownloadCategoryAsync(CategoryRow row)
    {
        row.Busy = true;
        row.Progress = 0;
        var done = 0;
        var cdn = new CdnClient(Http, ClientRegion.En, Path.Combine(settings.DataFolder, "cache"));
        var log = new Progress<string>(_ => row.Progress = Math.Min(100, ++done * 100.0 / Math.Max(1, row.Rows.Count)));
        try
        {
            var summary = await Task.Run(() => new AssetDownloader(Http, cdn).DownloadAsync(
                row.Rows, new DownloadOptions(Path.Combine(settings.DataFolder, "AssetBundles")), log));
            CategoryStatus = summary.Failed == 0
                ? $"{row.Name}: {Format.Count(summary.Downloaded)} files downloaded."
                : $"{row.Name}: {Format.Count(summary.Failed)} files failed; download again to retry them.";
        }
        catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException)
        {
            CategoryStatus = $"{row.Name}: the download stopped ({e.Message}). Download again to continue where it left off.";
        }
        finally
        {
            row.Busy = false;
            RefreshLocal();
        }
    }
}

// Numbers are formatted the same way everywhere (1,234 and 9.88 GiB), whatever the system locale.
public static class Format
{
    public static string Count(long value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => FormattableString.Invariant($"{bytes / (double)(1L << 30):0.00} GiB"),
        >= 1L << 20 => FormattableString.Invariant($"{bytes / (double)(1L << 20):0.0} MiB"),
        >= 1L << 10 => FormattableString.Invariant($"{bytes / 1024.0:0.0} KiB"),
        _ => $"{bytes} B",
    };
}
