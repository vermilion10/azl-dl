using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using AzlDl.App.Audio;
using AzlDl.App.Settings;
using AzlDl.Core;
using AzlDl.Core.Art;
using AzlDl.Core.Audio;
using AzlDl.Core.Data;
using AzlDl.Core.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzlDl.App.ViewModels;

public sealed record TrackItem(MusicTrack Track, int Number)
{
    // Tracks outside the collection have no length in the game's tables.
    public string LengthText => Track.Length > TimeSpan.Zero ? Track.Length.ToString(@"m\:ss", CultureInfo.InvariantCulture) : "";
}

public sealed partial class AlbumItem(string name, string cover, IReadOnlyList<TrackItem> tracks) : ObservableObject
{
    public string Name { get; } = name;

    public string Cover { get; } = cover;

    public IReadOnlyList<TrackItem> Tracks { get; } = tracks;

    public string Subtitle => $"{Tracks.Count} tracks";

    [ObservableProperty]
    private Bitmap? image;
}

// Music: the in-game music collection by album, and every music bank the game ships.
public sealed partial class MusicViewModel(AppSettings settings, PlayerViewModel player) : ObservableObject
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public PlayerViewModel Player { get; } = player;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsReady), nameof(HasProblem))]
    private LoadState state = LoadState.Loading;

    public bool IsLoading => State == LoadState.Loading;

    public bool IsReady => State == LoadState.Ready;

    public bool HasProblem => State is LoadState.MissingData or LoadState.Failed;

    [ObservableProperty]
    private string message = "";

    public ObservableCollection<AlbumItem> Albums { get; } = [];

    [ObservableProperty]
    private AlbumItem? selectedAlbum;

    // Downloading or decoding the track asked for, or why it couldn't play.
    [ObservableProperty]
    private string status = "";

    public bool CanPlay => WavePlayer.IsSupported;

    public Action? OpenSync { get; set; }

    [RelayCommand]
    private void GoToSync() => OpenSync?.Invoke();

    public async Task LoadAsync()
    {
        State = LoadState.Loading;
        Message = "Reading the music collection…";
        var folder = settings.DataFolder;
        if (AudioCatalog.MissingFiles(folder).Count > 0)
        {
            State = LoadState.MissingData;
            Message = $"There's no game data in {folder} yet. Update it from Sync, or pick a folder that already has it in Settings.";
            return;
        }

        try
        {
            var fetcher = Fetcher();
            var (albums, banks) = await Task.Run(() => (AudioCatalog.LoadAlbums(folder), MusicBanks(fetcher)));
            Albums.Clear();
            foreach (var album in albums)
            {
                Albums.Add(new AlbumItem(album.Name, album.Cover, album.Tracks.Select((t, i) => new TrackItem(t, i + 1)).ToList()));
            }

            // Every music bank in the last hash list, titled from the collection where it has a title.
            var titled = albums.SelectMany(a => a.Tracks).GroupBy(t => t.Bank).ToDictionary(g => g.Key, g => g.First());
            var everything = banks
                .Select(b => titled.GetValueOrDefault(b) ?? new MusicTrack(b["bgm-".Length..], b, TimeSpan.Zero, ""))
                .Select((t, i) => new TrackItem(t, i + 1))
                .ToList();
            if (everything.Count > 0)
            {
                Albums.Add(new AlbumItem("All game music", "", everything));
            }

            SelectedAlbum ??= Albums.FirstOrDefault();
            State = LoadState.Ready;
            _ = LoadCoversAsync();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or FormatException)
        {
            State = LoadState.Failed;
            Message = $"Couldn't read the music collection in {folder}: {e.Message}";
        }
    }

    private static List<string> MusicBanks(AssetFetcher fetcher) =>
        fetcher.KnownFiles(AssetCategory.Azl)
            .Select(r => r.Path)
            .Where(p => p.StartsWith("cue/bgm-", StringComparison.Ordinal) && p.EndsWith(".b", StringComparison.Ordinal))
            .Select(p => p["cue/".Length..^".b".Length])
            .Order(StringComparer.Ordinal)
            .ToList();

    // Covers are a few small pictures; they're fetched in the background and the albums show without them until then.
    private async Task LoadCoversAsync()
    {
        var fetcher = Fetcher();
        var albums = Albums.Where(a => a.Cover.Length > 0).ToList();
        try
        {
            await fetcher.FetchImagesAsync(albums.Select(a => AssetFetcher.AlbumCoverFolder + a.Cover.ToLowerInvariant()).ToList());
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException
                                      or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            // Offline or the server is busy: the albums keep their text-only look.
        }

        foreach (var album in albums)
        {
            var path = fetcher.AlbumCoverPath(album.Cover);
            if (File.Exists(path))
            {
                album.Image = await Task.Run(() =>
                {
                    using var stream = File.OpenRead(path);
                    return Bitmap.DecodeToWidth(stream, 240);
                });
            }
        }
    }

    [RelayCommand]
    private async Task PlayAsync(TrackItem? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            var wav = await ReadAsync(item.Track);
            Player.Start(wav, item.Track.Title, item.Track.Album.Length > 0 ? item.Track.Album : item.Track.Bank, this);
            Status = "";
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException or ArgumentException
                                      or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            Status = $"Couldn't play {item.Track.Title}: {e.Message}";
        }
    }

    public async Task SaveAsync(MusicTrack track, string path)
    {
        try
        {
            await File.WriteAllBytesAsync(path, await ReadAsync(track));
            Status = $"Saved {Path.GetFileName(path)}.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or InvalidDataException
                                      or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            Status = $"Couldn't save {track.Title}: {e.Message}";
        }
    }

    // The track as a WAV file, downloading its bank the first time.
    public async Task<byte[]> ReadAsync(MusicTrack track)
    {
        var fetcher = Fetcher();
        var path = fetcher.SoundBankPath(track.Bank);
        if (!File.Exists(path))
        {
            Status = $"Downloading {track.Title}…";
            await fetcher.FetchSoundBankAsync(track.Bank);
            if (!File.Exists(path))
            {
                throw new InvalidDataException("The game's server doesn't have this track.");
            }
        }

        Status = $"Decoding {track.Title}…";
        return await Task.Run(() =>
        {
            var bank = AcbFile.Read(path);
            var cue = bank.CueNames.FirstOrDefault(c => c == track.Bank) ?? bank.CueNames.Order(StringComparer.Ordinal).First();
            return bank.ReadWav(cue) ?? throw new InvalidDataException("The bank has no recording.");
        });
    }

    private AssetFetcher Fetcher() => new(Http, ClientRegion.En, settings.DataFolder);
}

public sealed partial class StoryItem(SoundStory story, string shipName) : ObservableObject
{
    public SoundStory Story { get; } = story;

    public string ShipName { get; } = shipName;

    public string Subtitle => Story.Voice.Length > 0 ? $"{ShipName} · CV {Story.Voice}" : ShipName;

    [ObservableProperty]
    private Bitmap? image;
}

public sealed partial class SubtitleItem(SoundStoryLine line) : ObservableObject
{
    public SoundStoryLine Line { get; } = line;

    public string Time => Line.Start.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    [ObservableProperty]
    private bool isCurrent;
}

// ASMR: the game's sound stories, each a recording of one ship with timed subtitles. Subtitles download when a story is
// picked; the recording (75-100 MB) only when asked for.
public sealed partial class AsmrViewModel(AppSettings settings, PlayerViewModel player) : ObservableObject
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    private IReadOnlyDictionary<string, long> sizes = new Dictionary<string, long>();
    private bool following;

    public PlayerViewModel Player { get; } = player;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsReady), nameof(HasProblem))]
    private LoadState state = LoadState.Loading;

    public bool IsLoading => State == LoadState.Loading;

    public bool IsReady => State == LoadState.Ready;

    public bool HasProblem => State is LoadState.MissingData or LoadState.Failed;

    [ObservableProperty]
    private string message = "";

    public ObservableCollection<StoryItem> Stories { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStory), nameof(HasPreview))]
    private StoryItem? selectedStory;

    public bool HasStory => SelectedStory is not null;

    public bool HasPreview => SelectedStory?.Story.Preview.Length > 0;

    public ObservableCollection<SubtitleItem> Lines { get; } = [];

    [ObservableProperty]
    private string subtitleMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayTitle))]
    private bool hasRecording;

    // "Download and play (75 MB)" until the recording is on disk.
    public string PlayTitle => HasRecording || SelectedStory is null
        ? "Play the story"
        : sizes.TryGetValue(SelectedStory.Story.Resource, out var size) ? $"Download and play ({Format.Size(size)})" : "Download and play";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayStoryCommand))]
    private bool busy;

    [ObservableProperty]
    private string status = "";

    public bool CanPlay => WavePlayer.IsSupported;

    public Action? OpenSync { get; set; }

    [RelayCommand]
    private void GoToSync() => OpenSync?.Invoke();

    public async Task LoadAsync()
    {
        State = LoadState.Loading;
        Message = "Reading the sound stories…";
        var folder = settings.DataFolder;
        if (AudioCatalog.MissingFiles(folder).Count > 0)
        {
            State = LoadState.MissingData;
            Message = $"There's no game data in {folder} yet. Update it from Sync, or pick a folder that already has it in Settings.";
            return;
        }

        try
        {
            var fetcher = Fetcher();
            var (stories, ships, known) = await Task.Run(() => (
                AudioCatalog.LoadSoundStories(folder),
                ShipCatalog.MissingFiles(folder).Count == 0 ? ShipCatalog.Load(folder).Ships : [],
                fetcher.KnownFiles(AssetCategory.Cipher)));
            sizes = known.Where(r => r.Path.EndsWith(".cpk", StringComparison.Ordinal))
                .ToDictionary(r => Path.GetFileNameWithoutExtension(r.Path), r => r.Size, StringComparer.Ordinal);
            var byGroup = ships.GroupBy(s => s.GroupId).ToDictionary(g => g.Key, g => g.First());

            Stories.Clear();
            foreach (var story in stories)
            {
                var ship = byGroup.GetValueOrDefault(story.ShipGroup);
                var item = new StoryItem(story, ship?.Name.Trim() ?? "");
                Stories.Add(item);
                // The ship's card picture, when the gallery's images are downloaded.
                if (ship is not null && File.Exists(fetcher.ShipImagePath(ship.Painting)))
                {
                    _ = ShowImageAsync(item, fetcher.ShipImagePath(ship.Painting));
                }
            }

            SelectedStory ??= Stories.FirstOrDefault();
            State = LoadState.Ready;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or FormatException)
        {
            State = LoadState.Failed;
            Message = $"Couldn't read the sound stories in {folder}: {e.Message}";
        }

        if (!following)
        {
            following = true;
            Player.PositionChanged += Follow;
        }
    }

    private static async Task ShowImageAsync(StoryItem item, string path) =>
        item.Image = await Task.Run(() =>
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, 120);
        });

    partial void OnSelectedStoryChanged(StoryItem? value) => _ = ShowStoryAsync(value);

    private async Task ShowStoryAsync(StoryItem? item)
    {
        Lines.Clear();
        Status = "";
        if (item is null)
        {
            return;
        }

        var fetcher = Fetcher();
        var story = item.Story;
        HasRecording = File.Exists(fetcher.SoundStoryPath(story.Resource));
        OnPropertyChanged(nameof(PlayTitle));
        var subtitles = fetcher.SubtitlePath(story.Resource);
        SubtitleMessage = File.Exists(subtitles) ? "" : "Loading the subtitles…";
        try
        {
            if (!File.Exists(subtitles))
            {
                await fetcher.FetchSoundStoryAsync(story.Resource, recording: false);
            }

            var lines = await Task.Run(() => File.Exists(subtitles) ? AudioCatalog.ReadSubtitles(subtitles) : []);
            if (SelectedStory != item)
            {
                return;
            }

            foreach (var line in lines)
            {
                Lines.Add(new SubtitleItem(line));
            }

            SubtitleMessage = lines.Count > 0 ? "" : "This recording has no English subtitles.";
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException
                                      or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            if (SelectedStory == item)
            {
                SubtitleMessage = $"The subtitles didn't load: {e.Message}";
            }
        }
    }

    // The short sample the game plays before a story is bought.
    [RelayCommand]
    private async Task PlayPreviewAsync()
    {
        if (SelectedStory is not { Story.Preview.Length: > 0 } item)
        {
            return;
        }

        var fetcher = Fetcher();
        var path = fetcher.SoundBankPath(item.Story.Preview);
        try
        {
            if (!File.Exists(path))
            {
                Status = "Downloading the sample…";
                await fetcher.FetchSoundBankAsync(item.Story.Preview);
            }

            var wav = await Task.Run(() =>
            {
                var bank = AcbFile.Read(path);
                return bank.ReadWav(bank.CueNames.Order(StringComparer.Ordinal).First()) ?? throw new InvalidDataException("The sample has no recording.");
            });
            Player.Start(wav, $"{item.Story.Title} (sample)", item.Subtitle, this);
            Status = "";
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException or InvalidOperationException
                                      or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            Status = $"The sample didn't play: {e.Message}";
        }
    }

    private bool CanPlayStory() => !Busy;

    [RelayCommand(CanExecute = nameof(CanPlayStory))]
    private async Task PlayStoryAsync()
    {
        if (SelectedStory is not { } item)
        {
            return;
        }

        try
        {
            var wav = await ReadAsync(item.Story);
            Player.Start(wav, item.Story.Title, item.Subtitle, item);
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException or ArgumentException
                                      or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            Status = $"The story didn't play: {e.Message}";
        }
    }

    public async Task SaveAsync(string path)
    {
        if (SelectedStory is not { } item)
        {
            return;
        }

        try
        {
            await File.WriteAllBytesAsync(path, await ReadAsync(item.Story));
            Status = $"Saved {Path.GetFileName(path)}.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or InvalidDataException
                                      or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            Status = $"Couldn't save the story: {e.Message}";
        }
    }

    // The whole recording as a WAV file, downloading it the first time.
    public async Task<byte[]> ReadAsync(SoundStory story)
    {
        var fetcher = Fetcher();
        var path = fetcher.SoundStoryPath(story.Resource);
        Busy = true;
        try
        {
            if (!File.Exists(path))
            {
                Status = sizes.TryGetValue(story.Resource, out var size)
                    ? $"Downloading {story.Title} ({Format.Size(size)})…"
                    : $"Downloading {story.Title}…";
                await fetcher.FetchSoundStoryAsync(story.Resource, recording: true);
                HasRecording = File.Exists(path);
                if (!HasRecording)
                {
                    throw new InvalidDataException("The game's server doesn't have this recording.");
                }
            }

            Status = "Decoding the recording…";
            var wav = await Task.Run(() => SoundStoryAudio.ReadWav(path));
            Status = "";
            return wav;
        }
        finally
        {
            Busy = false;
        }
    }

    // Clicking a subtitle jumps there, starting the story if it isn't the one playing.
    [RelayCommand]
    private async Task SeekToAsync(SubtitleItem? line)
    {
        if (line is null || SelectedStory is not { } item)
        {
            return;
        }

        if (Player.Owner == item)
        {
            Player.Seek(line.Line.Start);
            return;
        }

        try
        {
            var wav = await ReadAsync(item.Story);
            Player.Start(wav, item.Story.Title, item.Subtitle, item, line.Line.Start);
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException or ArgumentException
                                      or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            Status = $"The story didn't play: {e.Message}";
        }
    }

    // Lights up the line being spoken while the selected story plays.
    private void Follow(TimeSpan position)
    {
        var playing = SelectedStory is { } item && Player.Owner == item;
        foreach (var line in Lines)
        {
            line.IsCurrent = playing && position >= line.Line.Start && position < line.Line.End;
        }

        CurrentLine = playing ? Lines.FirstOrDefault(l => l.IsCurrent) : null;
    }

    // The line being spoken, which the view scrolls into sight.
    [ObservableProperty]
    private SubtitleItem? currentLine;

    private AssetFetcher Fetcher() => new(Http, ClientRegion.En, settings.DataFolder);
}
