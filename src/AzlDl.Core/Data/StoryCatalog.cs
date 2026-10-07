using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AzlDl.Core.Data;

public sealed record StoryEntry(long Id, string Title, string Subtitle, string StoryId, string Unlock);

public sealed record StoryGroup(long Id, string Title, string Category, IReadOnlyList<StoryEntry> Entries);

public enum StoryLineKind
{
    TitleCard,
    Dialogue,
    Choice,
}

public sealed record StoryLine(StoryLineKind Kind, string Speaker, string Text, string Background, IReadOnlyList<string> Options);

// The game's Memories archive: memory_group lists the chapters, memory_template the stories in each, and the story
// scripts (decoded by the story command) hold the lines. Speaker names come from name_code and ship skins.
public sealed partial class StoryCatalog
{
    // Group categories in the order the game shows its Memories tabs (gametip memory_* labels).
    private static string Category(long type, long subtype) => (type, subtype) switch
    {
        (1, _) => "Main story",
        (2, 1) => "Events",
        (2, 2) => "Special",
        (2, _) => "Permanent",
        (3, _) => "Characters",
        _ => "Hall of Fame",
    };

    private readonly string storyFolder;
    private readonly Dictionary<long, string> nameCodes;
    private readonly Dictionary<long, string> skinSpeakers;

    public IReadOnlyList<StoryGroup> Groups { get; }

    private StoryCatalog(string storyFolder, IReadOnlyList<StoryGroup> groups, Dictionary<long, string> nameCodes,
        Dictionary<long, string> skinSpeakers)
    {
        this.storyFolder = storyFolder;
        Groups = groups;
        this.nameCodes = nameCodes;
        this.skinSpeakers = skinSpeakers;
    }

    public static IReadOnlyList<string> RequiredFiles { get; } =
    [
        "sharecfg/memory_group.json", "sharecfg/memory_template.json", "sharecfg/name_code.json",
        "config/ship_skin_template.json", "story",
    ];

    public static IReadOnlyList<string> MissingFiles(string dataFolder) =>
        RequiredFiles.Where(f => !File.Exists(Path.Combine(dataFolder, f)) && !Directory.Exists(Path.Combine(dataFolder, f))).ToList();

    public static StoryCatalog Load(string dataFolder, ShipCatalog ships)
    {
        JsonObject Read(string file) => JsonNode.Parse(File.ReadAllBytes(Path.Combine(dataFolder, file)))!.AsObject();

        var nameCodes = Rows(Read("sharecfg/name_code.json")).ToDictionary(r => Long(r["id"]), r => Text(r["name"]));

        // Speakers are skin ids; a skin belongs to a ship group, which has the ship's name.
        var shipNames = ships.Ships.ToDictionary(s => s.GroupId, s => s.Name);
        var skinSpeakers = new Dictionary<long, string>();
        foreach (var skin in Rows(Read("config/ship_skin_template.json")))
        {
            if (skin["id"] is not null && skin["ship_group"] is not null && shipNames.TryGetValue(Long(skin["ship_group"]), out var name))
            {
                skinSpeakers[Long(skin["id"])] = name;
            }
        }

        var templates = Rows(Read("sharecfg/memory_template.json")).ToDictionary(r => Long(r["id"]));
        var storyFolder = Path.Combine(dataFolder, "story");
        var groups = new List<StoryGroup>();
        foreach (var group in Rows(Read("sharecfg/memory_group.json")))
        {
            var entries = new List<StoryEntry>();
            foreach (var id in (group["memories"] as JsonArray ?? []).Select(Long))
            {
                if (templates.TryGetValue(id, out var t) && Text(t["story"]) is { Length: > 0 } story
                    && File.Exists(Path.Combine(storyFolder, story.ToLowerInvariant() + ".json")))
                {
                    entries.Add(new StoryEntry(id, Clean(Text(t["title"]), nameCodes), Clean(Text(t["subtitle"]), nameCodes),
                        story.ToLowerInvariant(), Text(t["condition"])));
                }
            }

            if (entries.Count > 0)
            {
                groups.Add(new StoryGroup(Long(group["id"]), Clean(Text(group["title"]), nameCodes),
                    Category(Long(group["type"]), group["subtype"] is null ? 0 : Long(group["subtype"])), entries));
            }
        }

        return new StoryCatalog(storyFolder, groups, nameCodes, skinSpeakers);
    }

    // Reads one story script into lines a reader can show: title cards, dialogue with its speaker, and choices.
    public IReadOnlyList<StoryLine> ReadStory(string storyId)
    {
        var root = JsonNode.Parse(File.ReadAllBytes(Path.Combine(storyFolder, storyId + ".json")));
        var lines = new List<StoryLine>();
        var background = "";
        foreach (var step in root?["scripts"] as JsonArray ?? [])
        {
            if (step is not JsonObject s)
            {
                continue;
            }

            if (s["bgName"] is { } bg)
            {
                background = Text(bg);
            }

            foreach (var card in s["sequence"] as JsonArray ?? [])
            {
                if (card is JsonArray pair && pair.Count > 0)
                {
                    lines.Add(new StoryLine(StoryLineKind.TitleCard, "", Clean(Text(pair[0]), nameCodes), background, []));
                }
            }

            if (s["say"] is { } say)
            {
                lines.Add(new StoryLine(StoryLineKind.Dialogue, Speaker(s), Clean(Text(say), nameCodes), background, []));
            }

            if (s["options"] is JsonArray options && options.Count > 0)
            {
                var texts = options.OfType<JsonObject>().Select(o => Clean(Text(o["content"]), nameCodes)).ToList();
                lines.Add(new StoryLine(StoryLineKind.Choice, "", "", background, texts));
            }
        }

        return lines;
    }

    private string Speaker(JsonObject step)
    {
        if (step["actorName"] is { } name)
        {
            return Clean(Text(name), nameCodes);
        }

        return step["actor"] is { } actor && actor.GetValueKind() == JsonValueKind.Number
               && skinSpeakers.TryGetValue(Long(actor), out var ship) ? ship : "";
    }

    // Resolves {namecode:N}, marks the player's own name and the TB character, and drops the two markup tags the
    // scripts use for styling (<size> and <color>). Other angle brackets are part of the text and stay.
    private static string Clean(string text, Dictionary<long, string> nameCodes)
    {
        text = NameCode().Replace(text, m => nameCodes.GetValueOrDefault(long.Parse(m.Groups[1].Value), m.Value));
        text = text.Replace("{playername}", "[Commander]").Replace("{tb}", "[TB]");
        return StyleTag().Replace(text, "").Trim();
    }

    private static IEnumerable<JsonObject> Rows(JsonObject table) =>
        table.Where(p => p.Key.All(char.IsAsciiDigit)).Select(p => p.Value).OfType<JsonObject>();

    private static long Long(JsonNode? node) => node!.GetValueKind() == JsonValueKind.Number
        ? (long)node.GetValue<double>()
        : long.Parse(node.GetValue<string>());

    private static string Text(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : "";

    [GeneratedRegex(@"\{namecode:(\d+)\}")]
    private static partial Regex NameCode();

    [GeneratedRegex(@"</?(size|color)(=[^>]*)?>")]
    private static partial Regex StyleTag();
}
