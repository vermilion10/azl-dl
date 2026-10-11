using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AzlDl.Core.Data;

// Who wrote a post, comment or message: their Juustagram handle and the chibi head (qicon/<Avatar>) the game shows.
// Group 0 is the Commander, who has no avatar.
public sealed record SocialAuthor(long ShipGroup, string Name, string Avatar)
{
    public bool IsCommander => ShipGroup == 0;
}

public sealed record JuustagramComment(SocialAuthor Author, string Text, DateTime Time, IReadOnlyList<JuustagramComment> Replies);

// A reply the Commander can leave on a post, and the author's answer to it.
public sealed record JuustagramOption(string Text, string Answer);

public sealed record JuustagramPost(
    long Id,
    SocialAuthor Author,
    string PictureUrl,
    string Message,
    DateTime Time,
    IReadOnlyList<JuustagramComment> Comments,
    IReadOnlyList<JuustagramOption> Options);

public enum ChatLineKind
{
    Text,
    Sticker,
    RedEnvelope,
    Notice,
}

// Flag says which branch a line belongs to: 0 is always shown, N only after the Commander picked reply N.
// Options are the replies offered after this line.
public sealed record ChatLine(SocialAuthor Author, ChatLineKind Kind, string Text, int Flag, IReadOnlyList<ChatOption> Options);

public sealed record ChatOption(int Flag, string Text);

public sealed record ChatTopic(long Id, string Name, string Unlock, IReadOnlyList<ChatLine> Lines);

// One conversation partner in Fleet Chat: a ship, or a group chat (Eagle Union Group Chat, General Chat, ...).
public sealed record ChatContact(SocialAuthor Who, bool IsGroup, IReadOnlyList<ChatTopic> Topics);

// Reads Juustagram (activity_ins_template and its comments) and Fleet Chat (activity_ins_chat_group) from the decoded
// sharecfg tables.
public sealed partial class SocialCatalog
{
    public IReadOnlyList<JuustagramPost> Posts { get; }

    public IReadOnlyList<ChatContact> Contacts { get; }

    private SocialCatalog(IReadOnlyList<JuustagramPost> posts, IReadOnlyList<ChatContact> contacts)
    {
        Posts = posts;
        Contacts = contacts;
    }

    public static IReadOnlyList<string> RequiredFiles { get; } =
    [
        "sharecfg/activity_ins_template.json", "sharecfg/activity_ins_language.json", "sharecfg/activity_ins_npc_template.json",
        "sharecfg/activity_ins_ship_group_template.json", "sharecfg/activity_ins_chat_group.json",
        "sharecfg/activity_ins_chat_language.json",
    ];

    public static IReadOnlyList<string> MissingFiles(string dataFolder) =>
        RequiredFiles.Where(f => !File.Exists(Path.Combine(dataFolder, f))).ToList();

    // Text the EN client hasn't translated yet.
    private const string Placeholder = "Future Content";

    public static SocialCatalog Load(string dataFolder)
    {
        JsonObject Read(string file) =>
            JsonNode.Parse(File.ReadAllBytes(Path.Combine(dataFolder, file)))?.AsObject()
            ?? throw new InvalidDataException($"{file} is empty.");

        JsonObject ReadOptional(string file) => File.Exists(Path.Combine(dataFolder, file)) ? Read(file) : [];

        var templates = Read("sharecfg/activity_ins_template.json");
        var language = Read("sharecfg/activity_ins_language.json");
        var npcs = Read("sharecfg/activity_ins_npc_template.json");
        var people = Read("sharecfg/activity_ins_ship_group_template.json");
        var groups = Read("sharecfg/activity_ins_chat_group.json");
        var lines = Read("sharecfg/activity_ins_chat_language.json");
        var emoji = ReadOptional("sharecfg/emoji_template.json");
        var envelopes = ReadOptional("sharecfg/activity_ins_redpackage.json");

        string Say(string key) => language[key]?["value"]?.GetValue<string>() is { } text ? Clean(text) : "";

        var authors = new Dictionary<long, SocialAuthor>();
        SocialAuthor Author(long group)
        {
            if (!authors.TryGetValue(group, out var author))
            {
                var row = people[group.ToString(CultureInfo.InvariantCulture)];
                author = group == 0
                    ? new SocialAuthor(0, "Commander", "")
                    : new SocialAuthor(group, Text(row?["name"]) is { Length: > 0 } name ? name.Trim() : $"#{group}", Text(row?["sculpture"]));
                authors[group] = author;
            }

            return author;
        }

        JuustagramComment Comment(JsonObject row) => new(
            Author(Number(row["ship_group"])),
            Say(Text(row["message_persist"])),
            Time(row["time_persist"]),
            (row["npc_reply_persist"] as JsonArray ?? [])
                .Select(id => npcs[Number(id).ToString(CultureInfo.InvariantCulture)] as JsonObject).OfType<JsonObject>()
                .Select(Comment).Where(c => c.Text is { Length: > 0 } and not Placeholder).OrderBy(c => c.Time).ToList());

        var posts = new List<JuustagramPost>();
        foreach (var row in templates.Select(p => p.Value).OfType<JsonObject>())
        {
            // Type 2 rows are official link posts with only a title; the reader shows the ships' own posts, and skips the
            // ones still waiting for their translation.
            var messageKey = Text(row["message_persist"]);
            if (Number(row["type"]) != 1 || Say(messageKey) is "" or Placeholder)
            {
                continue;
            }

            // The Commander's replies are numbered after the post's message (ins_5 has ins_op_5_1_n), not after its id.
            var number = messageKey.StartsWith("ins_", StringComparison.Ordinal) ? messageKey[4..] : "";
            var author = Author(Number(row["ship_group"]));
            var options = new List<JuustagramOption>();
            for (var choice = 1; number.Length > 0 && language[$"ins_op_{number}_1_{choice}"] is not null; choice++)
            {
                options.Add(new JuustagramOption(Say($"ins_op_{number}_1_{choice}"), Say($"op_reply_{number}_1_{choice}")));
            }

            posts.Add(new JuustagramPost(
                Number(row["id"]),
                // The post row's own sculpture is the outfit the author wears in this post.
                Text(row["sculpture"]) is { Length: > 0 } outfit ? author with { Avatar = outfit } : author,
                Text(row["picture_persist"]),
                Say(messageKey),
                Time(row["time_persist"] is JsonArray { Count: > 0 } ? row["time_persist"] : row["time"]),
                (row["npc_discuss_persist"] as JsonArray ?? [])
                    .Select(c => npcs[Number(c).ToString(CultureInfo.InvariantCulture)] as JsonObject).OfType<JsonObject>()
                    .Select(Comment).Where(c => c.Text is { Length: > 0 } and not Placeholder).OrderBy(c => c.Time).ToList(),
                options.Where(o => o.Text is { Length: > 0 } and not Placeholder).ToList()));
        }

        ChatLine Line(JsonObject row)
        {
            var kind = Number(row["type"]) switch { 3 => ChatLineKind.RedEnvelope, 4 => ChatLineKind.Sticker, 5 => ChatLineKind.Notice, _ => ChatLineKind.Text };
            var param = row["param"]?.GetValueKind() == JsonValueKind.Number ? Number(row["param"]).ToString(CultureInfo.InvariantCulture) : Text(row["param"]);
            var text = kind switch
            {
                ChatLineKind.Sticker => Clean(Text(emoji[param]?["desc"])).Trim('[', ']') is { Length: > 0 } name ? name : "Sticker",
                ChatLineKind.RedEnvelope => Clean(Text(envelopes[param]?["desc"])) is { Length: > 0 } wish ? wish : "Red envelope",
                _ => Clean(param),
            };
            var options = row["option"] is JsonArray choices
                ? choices.OfType<JsonArray>().Where(c => c.Count >= 2).Select(c => new ChatOption((int)Number(c[0]), Clean(Text(c[1])))).ToList()
                : [];
            return new ChatLine(Author(Number(row["ship_group"])), kind, text, row["flag"] is { } flag ? (int)Number(flag) : 0, options);
        }

        var contacts = groups.Select(p => p.Value).OfType<JsonObject>()
            .Where(g => g["ship_group"] is not null && g["content"] is JsonArray)
            .GroupBy(g => Number(g["ship_group"]))
            .Select(g => new ChatContact(
                Author(g.Key),
                Number(people[g.Key.ToString(CultureInfo.InvariantCulture)]?["type"] ?? 1) == 2,
                g.OrderBy(t => Number(t["id"])).Select(t => new ChatTopic(
                    Number(t["id"]),
                    Clean(Text(t["name"])),
                    Clean(Text(t["unlock_desc"])),
                    ((JsonArray)t["content"]!).Select(id => lines[Number(id).ToString(CultureInfo.InvariantCulture)] as JsonObject)
                        .OfType<JsonObject>().Select(Line).ToList())).ToList()))
            // Group chats first, then ships by name.
            .OrderByDescending(c => c.IsGroup).ThenBy(c => c.IsGroup ? c.Who.ShipGroup : 0).ThenBy(c => c.Who.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SocialCatalog(posts.OrderByDescending(p => p.Time).ThenByDescending(p => p.Id).ToList(), contacts);
    }

    // Plays out a chat: lines of the always-shown branch and of every branch the Commander picked, up to the next line
    // that offers replies. The caller adds the picked reply's flag and asks again.
    public static IReadOnlyList<ChatLine> Play(ChatTopic topic, IReadOnlyCollection<int> picked, out IReadOnlyList<ChatOption> waiting)
    {
        var shown = new List<ChatLine>();
        waiting = [];
        foreach (var line in topic.Lines)
        {
            if (line.Flag != 0 && !picked.Contains(line.Flag))
            {
                continue;
            }

            shown.Add(line);
            if (line.Options.Count > 0 && !line.Options.Any(o => picked.Contains(o.Flag)))
            {
                waiting = line.Options;
                break;
            }
        }

        return shown;
    }

    // Game text carries Unity rich-text colour tags and escaped quotes.
    private static string Clean(string text) => RichText().Replace(text, "").Replace("\\\"", "\"").Replace("\\n", "\n").Trim();

    [GeneratedRegex(@"</?color[^>]*>")]
    private static partial Regex RichText();

    private static DateTime Time(JsonNode? node)
    {
        if (node is JsonArray { Count: >= 2 } parts && parts[0] is JsonArray date && parts[1] is JsonArray time && date.Count >= 3 && time.Count >= 3)
        {
            try
            {
                return new DateTime((int)Number(date[0]), (int)Number(date[1]), (int)Number(date[2]),
                    (int)Number(time[0]), (int)Number(time[1]), (int)Number(time[2]));
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        return DateTime.MinValue;
    }

    private static long Number(JsonNode? node) => node!.GetValueKind() == JsonValueKind.Number
        ? (long)node.GetValue<double>()
        : long.Parse(node.GetValue<string>(), CultureInfo.InvariantCulture);

    private static string Text(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : "";
}
