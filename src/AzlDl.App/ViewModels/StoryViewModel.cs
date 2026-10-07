using System.Collections.ObjectModel;
using AzlDl.App.Settings;
using AzlDl.Core.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzlDl.App.ViewModels;

// A line as the reader shows it; the background name is shown only where the scene changes.
public sealed record ReaderLine(StoryLine Line, bool NewScene)
{
    public bool IsTitle => Line.Kind == StoryLineKind.TitleCard;

    public bool IsDialogue => Line.Kind == StoryLineKind.Dialogue;

    public bool IsChoice => Line.Kind == StoryLineKind.Choice;

    public bool HasSpeaker => IsDialogue && Line.Speaker.Length > 0;
}

// The story reader: chapters from the game's Memories archive, their stories, and the lines of the chosen one.
public sealed partial class StoryViewModel(AppSettings settings) : ObservableObject
{
    private StoryCatalog? catalog;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsReady), nameof(HasProblem))]
    private LoadState state = LoadState.Loading;

    public bool IsLoading => State == LoadState.Loading;

    public bool IsReady => State == LoadState.Ready;

    public bool HasProblem => State is LoadState.MissingData or LoadState.Failed;

    [ObservableProperty]
    private string message = "";

    [ObservableProperty]
    private string filter = "";

    [ObservableProperty]
    private string? selectedCategory;

    [ObservableProperty]
    private StoryGroup? selectedGroup;

    [ObservableProperty]
    private StoryEntry? selectedEntry;

    [ObservableProperty]
    private string readerMessage = "Pick a story to read it.";

    public ObservableCollection<string> Categories { get; } = [];

    public ObservableCollection<StoryGroup> Groups { get; } = [];

    public ObservableCollection<ReaderLine> Lines { get; } = [];

    public Action? OpenSync { get; set; }

    [RelayCommand]
    private void GoToSync() => OpenSync?.Invoke();

    public async Task LoadAsync()
    {
        State = LoadState.Loading;
        Message = "Reading the story archive…";
        var folder = settings.DataFolder;
        var missing = ShipCatalog.MissingFiles(folder).Concat(StoryCatalog.MissingFiles(folder)).Distinct().ToList();
        if (missing.Count > 0)
        {
            State = LoadState.MissingData;
            Message = $"There are no decoded stories in {folder} yet. Update the game data from Sync to read them.";
            return;
        }

        try
        {
            catalog = await Task.Run(() => StoryCatalog.Load(folder, ShipCatalog.Load(folder)));
            Categories.Clear();
            foreach (var category in catalog.Groups.Select(g => g.Category).Distinct())
            {
                Categories.Add(category);
            }

            SelectedCategory = Categories.FirstOrDefault();
            State = LoadState.Ready;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            State = LoadState.Failed;
            Message = $"Couldn't read the story archive in {folder}: {e.Message}";
        }
    }

    partial void OnSelectedCategoryChanged(string? value) => ApplyFilter();

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnSelectedGroupChanged(StoryGroup? value) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (SelectedGroup == value)
            {
                SelectedEntry = value?.Entries.FirstOrDefault();
            }
        });

    partial void OnSelectedEntryChanged(StoryEntry? value)
    {
        Lines.Clear();
        if (value is null || catalog is null)
        {
            return;
        }

        try
        {
            var previous = "";
            foreach (var line in catalog.ReadStory(value.StoryId))
            {
                Lines.Add(new ReaderLine(line, line.Background.Length > 0 && line.Background != previous));
                previous = line.Background;
            }

            ReaderMessage = Lines.Count == 0 ? $"{value.StoryId} has no dialogue; it's a battle or cutscene script." : "";
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
        {
            ReaderMessage = $"Couldn't read {value.StoryId}: {e.Message}";
        }
    }

    private void ApplyFilter()
    {
        if (catalog is null)
        {
            return;
        }

        var keep = SelectedGroup;
        Groups.Clear();
        foreach (var group in catalog.Groups.Where(g => g.Category == SelectedCategory
                                                        && (Filter.Length == 0
                                                            || g.Title.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                                                            || g.Entries.Any(e => e.Title.Contains(Filter, StringComparison.OrdinalIgnoreCase)))))
        {
            Groups.Add(group);
        }

        SelectedGroup = keep is not null && Groups.Contains(keep) ? keep : Groups.FirstOrDefault();
    }
}
