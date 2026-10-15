using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AzlDl.Core.Data;

// A track of game music. Bank names the CRI sound bank it plays from (cue/<bank>.b, such as bgm-bsm-1).
public sealed record MusicTrack(string Title, string Bank, TimeSpan Length, string Album);

// An album of the in-game music collection (music_album), with its cover (musiccover/<cover>).
public sealed record MusicAlbum(string Name, string Cover, IReadOnlyList<MusicTrack> Tracks);

// One subtitle line of a sound story, timed in seconds from the start of the recording.
public sealed record SoundStoryLine(TimeSpan Start, TimeSpan End, string Text);

// A sound story (soundstory_template): an ASMR recording of one ship. Resource names the full recording
// (originsource/cipher/<resource>.cpk, subtitles in <resource>.txt); Preview names the short sample in cue/<preview>.b.
public sealed record SoundStory(
    long Id,
    string Title,
    string Overview,
    long ShipGroup,
    string Voice,
    string Resource,
    string Preview,
    string PreviewText);

// Reads the music collection and the sound stories from the decoded sharecfg tables.
public static class AudioCatalog
{
    public static IReadOnlyList<string> RequiredFiles { get; } =
        ["sharecfg/music_album.json", "sharecfg/music_collect_config.json", "sharecfg/soundstory_template.json"];

    public static IReadOnlyList<string> MissingFiles(string dataFolder) =>
        RequiredFiles.Where(f => !File.Exists(Path.Combine(dataFolder, f))).ToList();

    // Albums in the collection's order, each with its tracks in id order.
    public static IReadOnlyList<MusicAlbum> LoadAlbums(string dataFolder)
    {
        var albums = Rows(dataFolder, "sharecfg/music_album.json");
        var tracks = Rows(dataFolder, "sharecfg/music_collect_config.json");
        return albums
            .OrderBy(a => Number(a.Row["order"]))
            .Select(a =>
            {
                var name = Text(a.Row["album_name"]);
                var list = tracks.Where(t => Number(t.Row["album_id"]) == a.Id && Text(t.Row["music"]).Length > 0)
                    .OrderBy(t => t.Id)
                    .Select(t => new MusicTrack(Text(t.Row["name"]).Trim(), Text(t.Row["music"]),
                        TimeSpan.FromMilliseconds(Number(t.Row["music_time"])), name))
                    .ToList();
                return new MusicAlbum(name, Text(a.Row["cover"]), list);
            })
            .Where(a => a.Tracks.Count > 0)
            .ToList();
    }

    // Sound stories by id. Each uses its Japanese recording, or the Chinese one when there is none.
    public static IReadOnlyList<SoundStory> LoadSoundStories(string dataFolder) =>
        Rows(dataFolder, "sharecfg/soundstory_template.json")
            .Select(s =>
            {
                var japanese = Text(s.Row["story_resource_JP"]).Length > 0;
                string Field(string name) => Text(s.Row[japanese ? $"{name}_JP" : $"{name}_CN"]);
                return new SoundStory(
                    s.Id,
                    Text(s.Row["name"]).Trim(),
                    Text(s.Row["overview"]).Trim(),
                    Number(s.Row["ship_id"]),
                    Field("CV"),
                    Field("story_resource").ToLowerInvariant(),
                    Field("audition_resource").ToLowerInvariant(),
                    Text(s.Row["audition_text"]).Trim());
            })
            .Where(s => s.Resource.Length > 0)
            .OrderBy(s => s.Id)
            .ToList();

    // Subtitles are "text#start#end" lines in seconds. Recordings without English subtitles have only "&&" in place of
    // the text, which is left out.
    public static IReadOnlyList<SoundStoryLine> ReadSubtitles(string path) =>
        File.ReadAllLines(path)
            .Select(line => line.Split('#'))
            .Where(parts => parts.Length >= 3)
            .Select(parts => (Text: string.Join('#', parts[..^2]).Trim(), Start: parts[^2], End: parts[^1]))
            .Where(p => p.Text.Length > 0 && p.Text != "&&"
                        && double.TryParse(p.Start, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                        && double.TryParse(p.End, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            .Select(p => new SoundStoryLine(
                TimeSpan.FromSeconds(double.Parse(p.Start, CultureInfo.InvariantCulture)),
                TimeSpan.FromSeconds(double.Parse(p.End, CultureInfo.InvariantCulture)),
                p.Text))
            .ToList();

    private static List<(long Id, JsonObject Row)> Rows(string dataFolder, string file) =>
        (JsonNode.Parse(File.ReadAllBytes(Path.Combine(dataFolder, file))) as JsonObject ?? [])
        .Where(p => p.Value is JsonObject && long.TryParse(p.Key, CultureInfo.InvariantCulture, out _))
        .Select(p => (long.Parse(p.Key, CultureInfo.InvariantCulture), (JsonObject)p.Value!))
        .ToList();

    private static long Number(JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.Number => (long)node.GetValue<double>(),
        JsonValueKind.String when long.TryParse(node.GetValue<string>(), CultureInfo.InvariantCulture, out var n) => n,
        _ => 0,
    };

    private static string Text(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : "";
}
