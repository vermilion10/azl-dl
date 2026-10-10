using System.Text.Json;
using System.Text.Json.Nodes;

namespace AzlDl.Core.Data;

public sealed record StatValue(string Label, string Value);

// Description has its $n placeholders filled with the level 1 and max level values ("40.0% → 70.0%").
public sealed record SkillInfo(string Name, string Description, int MaxLevel);

public sealed record VoiceLine(string Label, string Text, string Key);

// Which of a skin's animation sets to label: chibi actions, painting faces and dynamic skin expressions (both
// named by ship_skin_expression), or Live2D motions.
public enum AnimationSet
{
    Chibi,
    Expression,
    Live2D,
}

// Number is the skin's 1-based position in the ship's skin list.
public sealed record SkinInfo(long Id, string Name, string Painting, int Number);

// One ship as the game groups it: all limit-break stages share a group id; the catalog shows the fully limit-broken
// stage, which is the row with the highest star count.
public sealed record ShipInfo(
    long GroupId,
    long StatsId,
    string Name,
    string EnglishName,
    string TypeName,
    string NationName,
    int Rarity,
    string RarityName,
    IReadOnlyList<StatValue> Stats,
    IReadOnlyList<SkillInfo> Skills,
    IReadOnlyList<SkinInfo> Skins);

// Reads the JSON tables written by the config and sharecfg commands and joins them into ships.
// Every label comes from game data: hull types from ship_data_by_type, stat names from attribute_info_by_type,
// rarity and nation names from gametip.
public sealed class ShipCatalog
{
    // Nation id to gametip key, as in the game's Nation.Nation2Name (model/const/nation.lua).
    private static readonly Dictionary<long, string> NationKeys = new()
    {
        [0] = "word_shipNation_other", [1] = "word_shipNation_baiYing", [2] = "word_shipNation_huangJia",
        [3] = "word_shipNation_chongYing", [4] = "word_shipNation_tieXue", [5] = "word_shipNation_dongHuang",
        [6] = "word_shipNation_saDing", [7] = "word_shipNation_beiLian", [8] = "word_shipNation_ziyou",
        [9] = "word_shipNation_weixi", [10] = "word_shipNation_yuanwei", [11] = "word_shipNation_yujinwangguo",
        [12] = "word_shipNation_jinghuanlianmeng", [96] = "word_shipNation_mot", [97] = "word_shipNation_meta",
        [98] = "word_shipNation_other", [101] = "word_shipNation_np", [102] = "word_shipNation_bili",
        [103] = "word_shipNation_um", [104] = "word_shipNation_ai", [105] = "word_shipNation_holo",
        [106] = "word_shipNation_doa", [107] = "word_shipNation_imas", [108] = "word_shipNation_ssss",
        [109] = "word_shipNation_ryza", [110] = "word_shipNation_senran", [111] = "word_shipNation_tolove",
        [112] = "word_shipNation_brs", [113] = "word_shipNation_yumia", [114] = "word_shipNation_danmachi",
        [115] = "word_shipNation_dal", [117] = "word_shipNation_nierautomata",
    };

    // attrs index 6 is armor, which the game shows as an armor type rather than a number.
    private const int ArmorAttributeIndex = 6;

    private readonly JsonObject words;
    private readonly IReadOnlyList<(string Key, string Label)> voiceKeys;
    private readonly IReadOnlyDictionary<string, string> chibiActions;
    private readonly IReadOnlyDictionary<string, string> live2DActions;
    private readonly JsonObject expressions;

    public IReadOnlyList<ShipInfo> Ships { get; }

    private ShipCatalog(IReadOnlyList<ShipInfo> ships, JsonObject words, IReadOnlyList<(string Key, string Label)> voiceKeys,
        IReadOnlyDictionary<string, string> chibiActions, IReadOnlyDictionary<string, string> live2DActions, JsonObject expressions)
    {
        Ships = ships;
        this.words = words;
        this.voiceKeys = voiceKeys;
        this.chibiActions = chibiActions;
        this.live2DActions = live2DActions;
        this.expressions = expressions;
    }

    // The tables the catalog needs, relative to the data folder.
    public static IReadOnlyList<string> RequiredFiles { get; } =
    [
        "config/ship_data_statistics.json", "config/ship_data_template.json", "config/ship_skin_template.json",
        "config/gametip.json", "config/ship_skin_words.json", "sharecfg/ship_data_by_type.json",
        "sharecfg/attribute_info_by_type.json", "sharecfg/skill_data_template.json", "sharecfg/character_voice.json",
    ];

    public static IReadOnlyList<string> MissingFiles(string dataFolder) =>
        RequiredFiles.Where(f => !File.Exists(Path.Combine(dataFolder, f))).ToList();

    public static ShipCatalog Load(string dataFolder)
    {
        JsonObject Read(string file) =>
            JsonNode.Parse(File.ReadAllBytes(Path.Combine(dataFolder, file)))?.AsObject()
            ?? throw new InvalidDataException($"{file} is empty.");

        var statistics = Read("config/ship_data_statistics.json");
        var templates = Read("config/ship_data_template.json");
        var skins = Read("config/ship_skin_template.json");
        var tips = Read("config/gametip.json");
        var types = Read("sharecfg/ship_data_by_type.json");
        var attributes = Read("sharecfg/attribute_info_by_type.json");
        var skills = Read("sharecfg/skill_data_template.json");
        var words = Read("config/ship_skin_words.json");
        var voices = Read("sharecfg/character_voice.json");

        // Voice line keys in the order the game's profile screen lists them, with its labels.
        var voiceKeys = voices.Select(p => p.Value).OfType<JsonObject>()
            .Where(v => v["key"] is not null && v["voice_name"] is not null)
            .OrderBy(v => v["profile_index"] is null ? int.MaxValue : Long(v["profile_index"]))
            .Select(v => (Text(v["key"]), Text(v["voice_name"])))
            .ToList();

        // The chibi animation the profile screen plays with each voice line.
        var chibiActions = voices.Select(p => p.Value).OfType<JsonObject>()
            .Where(v => v["key"] is not null && Text(v["spine_action"]).Length > 0)
            .ToDictionary(v => Text(v["key"]), v => Text(v["spine_action"]));

        // The Live2D motion the game plays with each voice line.
        var live2DActions = voices.Select(p => p.Value).OfType<JsonObject>()
            .Where(v => v["key"] is not null && Text(v["l2d_action"]).Length > 0)
            .ToDictionary(v => Text(v["key"]), v => Text(v["l2d_action"]));

        // Per painting, the face (or dynamic skin expression) shown with each voice line. Older data folders lack it.
        var expressionFile = Path.Combine(dataFolder, "sharecfg/ship_skin_expression.json");
        var expressions = File.Exists(expressionFile) ? Read("sharecfg/ship_skin_expression.json") : [];

        string Tip(string key) => tips[key]?["tip"]?.GetValue<string>().Trim() ?? key;

        var statLabels = Enumerable.Range(1, 12)
            .Select(i => attributes[i.ToString()]?["condition"]?.GetValue<string>() ?? $"#{i}")
            .ToArray();

        var skinsByGroup = skins
            .Select(p => p.Value as JsonObject)
            .Where(s => s?["ship_group"] is not null && s["painting"] is not null)
            .GroupBy(s => Long(s!["ship_group"]))
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<SkinInfo>)g.OrderBy(s => Long(s!["id"]))
                    .Select((s, i) => new SkinInfo(Long(s!["id"]), Text(s["name"]), Text(s["painting"]), i + 1))
                    .ToList());

        // Template rows give each ship id its group and star count; keep the highest-star row per group.
        var best = new Dictionary<long, (long Id, long Star)>();
        foreach (var (_, node) in templates)
        {
            if (node is not JsonObject row || row["group_type"] is null || row["id"] is null)
            {
                continue;
            }

            var id = Long(row["id"]);
            var group = Long(row["group_type"]);
            var star = row["star"] is null ? 0 : Long(row["star"]);
            if (statistics[id.ToString()] is null)
            {
                continue;
            }

            if (!best.TryGetValue(group, out var current) || star > current.Star || (star == current.Star && id < current.Id))
            {
                best[group] = (id, star);
            }
        }

        var ships = new List<ShipInfo>();
        foreach (var (group, (id, _)) in best)
        {
            var stats = (JsonObject)statistics[id.ToString()]!;
            var type = Long(stats["type"]);
            var rarity = (int)Long(stats["rarity"]);
            var attrs = stats["attrs"] as JsonArray;

            var values = new List<StatValue>();
            for (var i = 0; i < statLabels.Length && attrs is not null && i < attrs.Count; i++)
            {
                if (i != ArmorAttributeIndex)
                {
                    values.Add(new StatValue(statLabels[i], FormatNumber(attrs[i])));
                }
            }

            var skillList = new List<SkillInfo>();
            foreach (var skillId in templates[id.ToString()]?["buff_list_display"] as JsonArray ?? [])
            {
                if (skills[Long(skillId).ToString()] is JsonObject skill)
                {
                    skillList.Add(new SkillInfo(Text(skill["name"]), SkillText(skill),
                        skill["max_level"] is null ? 1 : (int)Long(skill["max_level"])));
                }
            }

            ships.Add(new ShipInfo(
                group,
                id,
                Text(stats["name"]),
                Text(stats["english_name"]),
                types[type.ToString()]?["type_name"]?.GetValue<string>() ?? $"Type {type}",
                NationKeys.TryGetValue(Long(stats["nationality"]), out var nationKey) ? Tip(nationKey) : "",
                rarity,
                Tip($"index_rare{rarity}"),
                values,
                skillList,
                skinsByGroup.GetValueOrDefault(group) ?? []));
        }

        return new ShipCatalog(ships.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(), words, voiceKeys, chibiActions, live2DActions, expressions);
    }

    // A skin's voice lines. Skins only store the lines they change; the rest come from the ship's default skin, as
    // in the game. Idle lines are one '|'-separated "main" entry, listed as Main screen 1, 2, ...
    public IReadOnlyList<VoiceLine> VoiceLines(ShipInfo ship, SkinInfo skin)
    {
        var own = words[skin.Id.ToString()] as JsonObject;
        var fallback = ship.Skins.Count > 0 ? words[ship.Skins[0].Id.ToString()] as JsonObject : null;

        string Line(string key) =>
            (own?[key] is { } a && Text(a).Length > 0 ? Text(a) : null) ?? (fallback?[key] is { } b ? Text(b) : "");

        var mains = Line("main").Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<VoiceLine>();
        foreach (var (key, label) in voiceKeys)
        {
            var text = key.StartsWith("main", StringComparison.Ordinal) && int.TryParse(key.AsSpan(4), out var n)
                ? (n <= mains.Length ? mains[n - 1] : "")
                : Line(key);
            if (text.Trim().Length > 0)
            {
                lines.Add(new VoiceLine(label, text.Trim(), key));
            }
        }

        return lines;
    }

    // Animation name -> the voice lines the game plays it with, for this skin's lines only ("Main screen 1, Normal
    // touch"). Chibis use character_voice's spine_action and Live2D models its l2d_action; faces and dynamic skin
    // expressions use the skin's ship_skin_expression entry, whose keys spell main lines as main_1 rather than main1.
    public IReadOnlyDictionary<string, string> AnimationLabels(ShipInfo ship, SkinInfo skin, AnimationSet set)
    {
        var faces = expressions[skin.Painting] as JsonObject;
        string? Animation(string key)
        {
            switch (set)
            {
                case AnimationSet.Chibi:
                    return chibiActions.GetValueOrDefault(key);
                case AnimationSet.Live2D:
                    return live2DActions.GetValueOrDefault(key);
            }

            var faceKey = key.StartsWith("main", StringComparison.Ordinal) && int.TryParse(key.AsSpan(4), out var n) ? $"main_{n}" : key;
            return faces?[faceKey] is { } face ? Text(face) : null;
        }

        return VoiceLines(ship, skin)
            .Select(line => (Animation: Animation(line.Key), line.Label))
            .Where(p => !string.IsNullOrEmpty(p.Animation))
            .GroupBy(p => p.Animation!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(p => p.Label)), StringComparer.Ordinal);
    }

    // "$1 chance" with desc_get_add [["40.0%", "70.0%"]] becomes "40.0% → 70.0% chance" (level 1 → max level).
    private static string SkillText(JsonObject skill)
    {
        var text = Text(skill["desc"]);
        if (skill["desc_get_add"] is JsonArray values)
        {
            for (var i = values.Count; i >= 1; i--)
            {
                if (values[i - 1] is JsonArray range && range.Count > 0)
                {
                    var first = Text(range[0]);
                    var last = Text(range[^1]);
                    text = text.Replace($"${i}", first == last ? first : $"{first} → {last}");
                }
            }
        }

        return text;
    }

    private static long Long(JsonNode? node) => node!.GetValueKind() == JsonValueKind.Number
        ? (long)node.GetValue<double>()
        : long.Parse(node.GetValue<string>());

    private static string Text(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : "";

    private static string FormatNumber(JsonNode? node) =>
        node is null ? "" : node.GetValue<double>().ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
