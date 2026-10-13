using System.Text.Json;
using System.Text.Json.Nodes;

namespace AzlDl.Core.Data;

// Icon names the stat's sprite in the game's attricon atlas (durability, cannon, ...), when it has one.
public sealed record StatValue(string Label, string Value, string Icon = "");

// A ship's stats as one list, for the views' stat templates.
public sealed record StatSheet(IReadOnlyList<StatValue> Stats);

// Description has its $n placeholders filled with the level 1 and max level values ("40.0% → 70.0%").
public sealed record SkillInfo(string Name, string Description, int MaxLevel);

// ResourceKey names the recording: the cue in the ship's voice banks, before any skin suffix.
public sealed record VoiceLine(string Label, string Text, string Key, string ResourceKey);

// Which of a skin's animation sets to label: chibi actions, painting faces and dynamic skin expressions (both
// named by ship_skin_expression), or Live2D motions.
public enum AnimationSet
{
    Chibi,
    Expression,
    Live2D,
}

// Number is the skin's 1-based position in the ship's skin list.
// GroupIndex is the skin's place in its ship group (0 for the default skin); voice cues of other skins end in _<index>.
public sealed record SkinInfo(long Id, string Name, string Painting, int Number, int GroupIndex = 0);

// How the game frames a ship card. Research ships take Priority (Super Rare) or Decisive (Ultra Rare) frames and META
// ships their own, whatever their rarity.
public enum RarityTier
{
    Common,
    Rare,
    Elite,
    SuperRare,
    UltraRare,
    Priority,
    Decisive,
    Meta,
}

// Where a ship comes from: research (ship_data_blueprint), META (nation 97), a collaboration (nations 101 and up) or
// the standard roster.
public enum ShipCollection
{
    Standard,
    Research,
    Meta,
    Collab,
}

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
    IReadOnlyList<SkinInfo> Skins,
    long TypeId,
    long NationId,
    string TeamType,
    ShipCollection Collection,
    RarityTier Tier,
    int Stars)
{
    // The game's name for the card art of this rarity: 1-5 by rarity, 4_0 Priority, 5_0 Decisive, and META cards one
    // below their rarity with _1 (an Ultra Rare META ship uses 5_1).
    public string RarityKey => Tier switch
    {
        RarityTier.Priority => "4_0",
        RarityTier.Decisive => "5_0",
        RarityTier.Meta => $"{Math.Clamp(Rarity - 1, 3, 5)}_1",
        _ => (Rarity - 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    public IReadOnlyList<int> StarSlots => Enumerable.Range(0, Stars).ToList();

    // The hull type's usual abbreviation (DD, CL, BB, ...); the game shows an icon instead.
    public string HullCode => ShipCatalog.HullCode(TypeId);

    // The letters the game prints on the card frame.
    public string TierCode => Tier switch
    {
        RarityTier.Common => "N",
        RarityTier.Rare => "R",
        RarityTier.Elite => "SR",
        RarityTier.SuperRare => "SSR",
        RarityTier.UltraRare => "UR",
        RarityTier.Priority => "PR",
        RarityTier.Decisive => "DR",
        _ => "META",
    };

    // The default skin's painting, which names the ship's card image.
    public string Painting => Skins.Count > 0 ? Skins[0].Painting : "";

    // What the ship's stats at any level are worked out from; null for ships read from a data folder without the
    // growth tables.
    public ShipGrowth? Growth { get; init; }
}

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
        [12] = "word_shipNation_jinghuanlianmeng", [96] = "word_shipNation_mot", [97] = "word_shipNation_meta_index",
        [98] = "word_shipNation_other", [101] = "word_shipNation_np", [102] = "word_shipNation_bili",
        [103] = "word_shipNation_um", [104] = "word_shipNation_ai", [105] = "word_shipNation_holo",
        [106] = "word_shipNation_doa", [107] = "word_shipNation_imas", [108] = "word_shipNation_ssss",
        [109] = "word_shipNation_ryza", [110] = "word_shipNation_senran", [111] = "word_shipNation_tolove",
        [112] = "word_shipNation_brs", [113] = "word_shipNation_yumia", [114] = "word_shipNation_danmachi",
        [115] = "word_shipNation_dal", [117] = "word_shipNation_nierautomata",
    };

    // Collaboration nations start here (Neptune, Bilibili, hololive, ...).
    private const long FirstCollabNation = 101;
    private const long MetaNation = 97;

    private static readonly Dictionary<long, string> HullCodes = new()
    {
        [1] = "DD", [2] = "CL", [3] = "CA", [4] = "BC", [5] = "BB", [6] = "CVL", [7] = "CV", [8] = "SS", [9] = "CAV",
        [10] = "BBV", [11] = "CLT", [12] = "AR", [13] = "BM", [17] = "SSV", [18] = "CB", [19] = "AE", [20] = "DDG",
        [21] = "DDG", [22] = "IXs", [23] = "IXv", [24] = "IXm",
    };

    public static string HullCode(long type) => HullCodes.GetValueOrDefault(type, "");

    // attrs index 6 is armor, which the game shows as an armor type rather than a number.
    private const int ArmorAttributeIndex = 6;

    private readonly JsonObject words;
    private readonly IReadOnlyList<(string Key, string Label, string Resource)> voiceKeys;
    private readonly IReadOnlyDictionary<string, string> chibiActions;
    private readonly IReadOnlyDictionary<string, string> live2DActions;
    private readonly JsonObject expressions;
    private readonly IReadOnlyDictionary<long, IReadOnlyDictionary<string, string>> live2DIdles;
    private readonly StatText statText;

    public IReadOnlyList<ShipInfo> Ships { get; }

    // The affinity stages that change stats, lowest first (Stranger, Friendly, Crush, Love, Promised, Promised at 200).
    public IReadOnlyList<AffinityLevel> AffinityLevels { get; }

    // Labels for the stat sheet, and the oil ratio per level.
    private sealed record StatText(string[] Labels, string[] ArmorNames, string Cost, IReadOnlyDictionary<int, double> OilRatios);

    private ShipCatalog(IReadOnlyList<ShipInfo> ships, JsonObject words, IReadOnlyList<(string Key, string Label, string Resource)> voiceKeys,
        IReadOnlyDictionary<string, string> chibiActions, IReadOnlyDictionary<string, string> live2DActions, JsonObject expressions,
        IReadOnlyDictionary<long, IReadOnlyDictionary<string, string>> live2DIdles, StatText statText, IReadOnlyList<AffinityLevel> affinityLevels)
    {
        Ships = ships;
        this.statText = statText;
        AffinityLevels = affinityLevels;
        this.words = words;
        this.voiceKeys = voiceKeys;
        this.chibiActions = chibiActions;
        this.live2DActions = live2DActions;
        this.expressions = expressions;
        this.live2DIdles = live2DIdles;
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

        // Tables older data folders may lack; without them the matching part of the stats is left out.
        JsonObject Optional(string file) => File.Exists(Path.Combine(dataFolder, file)) ? Read(file) : [];

        var statistics = Read("config/ship_data_statistics.json");
        var templates = Read("config/ship_data_template.json");
        var skins = Read("config/ship_skin_template.json");
        var tips = Read("config/gametip.json");
        var types = Read("sharecfg/ship_data_by_type.json");
        var attributes = Read("sharecfg/attribute_info_by_type.json");
        var skills = Read("sharecfg/skill_data_template.json");
        var blueprintFile = Path.Combine(dataFolder, "sharecfg/ship_data_blueprint.json");
        var research = File.Exists(blueprintFile)
            ? Read("sharecfg/ship_data_blueprint.json").Select(p => p.Key).ToHashSet(StringComparer.Ordinal)
            : [];
        var words = Read("config/ship_skin_words.json");
        var voices = Read("sharecfg/character_voice.json");

        // Voice line keys in the order the game's profile screen lists them, with its labels.
        var voiceKeys = voices.Select(p => p.Value).OfType<JsonObject>()
            .Where(v => v["key"] is not null && v["voice_name"] is not null)
            .OrderBy(v => v["profile_index"] is null ? int.MaxValue : Long(v["profile_index"]))
            .Select(v => (Text(v["key"]), Text(v["voice_name"]), Text(v["resource_key"])))
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

        // Interactive Live2D skins: ship_l2d rows whose action leaves the model in another idle afterwards
        // (action_trigger_active.idle N plays clip idleN from then on; 0 is the default idle).
        var live2DIdles = new Dictionary<long, IReadOnlyDictionary<string, string>>();
        if (File.Exists(Path.Combine(dataFolder, "sharecfg/ship_l2d.json")))
        {
            foreach (var row in Read("sharecfg/ship_l2d.json").Select(p => p.Value).OfType<JsonObject>())
            {
                if (row["action_trigger"] is JsonObject trigger && Text(trigger["action"]) is { Length: > 0 } action
                    && row["action_trigger_active"] is JsonObject active && active["idle"]?.GetValueKind() == JsonValueKind.Number
                    && row["ship_skin_id"]?.GetValueKind() == JsonValueKind.Number)
                {
                    var idle = Long(active["idle"]);
                    var skinId = Long(row["ship_skin_id"]);
                    if (!live2DIdles.TryGetValue(skinId, out var map))
                    {
                        live2DIdles[skinId] = map = new Dictionary<string, string>(StringComparer.Ordinal);
                    }

                    ((Dictionary<string, string>)map)[action] = idle == 0 ? "idle" : $"idle{idle}";
                }
            }
        }

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
                    .Select((s, i) => new SkinInfo(Long(s!["id"]), Text(s["name"]), Text(s["painting"]), i + 1,
                        s!["group_index"] is { } index ? (int)Long(index) : 0))
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
            if (statistics[id.ToString()] is not JsonObject row2)
            {
                continue;
            }

            // Event and boss rows reuse a ship's group (Compiler is filed under Universal Bulin's); a row belongs to the
            // group only when its own skin does.
            if (row2["skin_id"] is { } skinId && skins[Long(skinId).ToString()]?["ship_group"] is { } skinGroup && Long(skinGroup) != group)
            {
                continue;
            }

            if (!best.TryGetValue(group, out var current) || star > current.Star || (star == current.Star && id < current.Id))
            {
                best[group] = (id, star);
            }
        }

        var growthTables = new GrowthTables(
            statistics,
            Optional("sharecfg/ship_data_strengthen.json"),
            research.Count > 0 ? Read("sharecfg/ship_data_blueprint.json") : [],
            Optional("sharecfg/ship_strengthen_blueprint.json"),
            Optional("sharecfg/ship_strengthen_meta.json"),
            Optional("sharecfg/ship_meta_repair.json"),
            Optional("sharecfg/ship_meta_repair_effect.json"),
            Optional("sharecfg/ship_data_trans.json"),
            Optional("sharecfg/transform_data_template.json"),
            Enumerable.Range(1, ShipGrowth.Count)
                .Select(i => attributes[i.ToString()]?["name"]?.GetValue<string>() ?? "")
                .Select((name, i) => (name, i)).Where(p => p.name.Length > 0)
                .ToDictionary(p => p.name, p => p.i, StringComparer.Ordinal));

        var ships = new List<ShipInfo>();
        foreach (var (group, (id, starCount)) in best)
        {
            var stats = (JsonObject)statistics[id.ToString()]!;
            var type = Long(stats["type"]);
            var rarity = (int)Long(stats["rarity"]);
            var nation = Long(stats["nationality"]);

            // Rarities run from 2 (Common) to 6 (Ultra Rare); other values are boss and test rows.
            if (rarity is < 2 or > 6)
            {
                continue;
            }

            var collection = research.Contains(group.ToString()) ? ShipCollection.Research
                : nation == MetaNation ? ShipCollection.Meta
                : nation >= FirstCollabNation ? ShipCollection.Collab
                : ShipCollection.Standard;
            var tier = collection switch
            {
                ShipCollection.Meta => RarityTier.Meta,
                ShipCollection.Research => rarity >= 6 ? RarityTier.Decisive : RarityTier.Priority,
                _ => (RarityTier)(rarity - 2),
            };
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
                NationName(nation, Tip),
                rarity,
                Tip($"index_rare{rarity}"),
                values,
                skillList,
                skinsByGroup.GetValueOrDefault(group) ?? [],
                type,
                nation,
                types[type.ToString()]?["team_type"]?.GetValue<string>() ?? "",
                collection,
                tier,
                (int)starCount)
            {
                Growth = growthTables.For(group, id, stats, templates[id.ToString()] as JsonObject, collection),
            });
        }

        // Armor types 1-3, labelled as the ship screen labels them.
        var armorNames = new[] { "", Tip("word_lightArmor"), Tip("word_mediumArmor"), Tip("word_heavyarmor") };
        var oilRatios = Optional("sharecfg/ship_level.json").Select(p => p.Value).OfType<JsonObject>()
            .Where(l => l["level"] is not null && l["fight_oil_ratio"] is not null)
            .ToDictionary(l => (int)Long(l["level"]), l => l["fight_oil_ratio"]!.GetValue<double>());
        var statText = new StatText(statLabels, armorNames, Tip("attr_consume"), oilRatios);

        return new ShipCatalog(ships.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(), words, voiceKeys, chibiActions,
            live2DActions, expressions, live2DIdles, statText, ReadAffinityLevels(Optional("sharecfg/intimacy_template.json")));
    }

    // The ship's stat sheet in the order the game's ship screen lists it: HP, armor, reload, firepower, torpedo,
    // evasion, anti-air, aviation, oil cost, anti-submarine, accuracy, luck and speed.
    public IReadOnlyList<StatValue> StatsAt(ShipInfo ship, int level, AffinityLevel affinity, bool developed, bool retrofit)
    {
        if (ship.Growth is not { } growth)
        {
            return ship.Stats;
        }

        var values = growth.At(level, affinity.Bonus, developed, retrofit);
        StatValue Stat(int i) => new(statText.Labels[i], FormatNumber(values[i]), StatIcons[i]);

        return
        [
            Stat(ShipGrowth.Durability),
            new StatValue(statText.Labels[ShipGrowth.Armor], statText.ArmorNames.ElementAtOrDefault(growth.ArmorType) ?? "", StatIcons[ShipGrowth.Armor]),
            Stat(ShipGrowth.Reload),
            Stat(ShipGrowth.Cannon),
            Stat(ShipGrowth.Torpedo),
            Stat(ShipGrowth.Dodge),
            Stat(ShipGrowth.AntiAircraft),
            Stat(ShipGrowth.Air),
            new StatValue(statText.Cost, growth.OilCost(level, statText.OilRatios).ToString(System.Globalization.CultureInfo.InvariantCulture), "expend"),
            Stat(ShipGrowth.AntiSub),
            Stat(ShipGrowth.Hit),
            Stat(ShipGrowth.Luck),
            Stat(ShipGrowth.Speed),
        ];
    }

    // The attricon sprite for each attrs index.
    private static readonly string[] StatIcons =
        ["durability", "cannon", "torpedo", "antiaircraft", "air", "reload", "armor", "hit", "dodge", "speed", "luck", "antisub"];

    // intimacy_template rows that change stats. A stage with the same bonus as the next one (Upset, before Stranger) is
    // left out; a repeated name (Promised, then Promised at 200) gets its affinity added.
    private static IReadOnlyList<AffinityLevel> ReadAffinityLevels(JsonObject template)
    {
        var rows = template.Select(p => p.Value).OfType<JsonObject>()
            .Where(r => r["id"] is not null && r["attr_bonus"] is not null)
            .OrderBy(r => Long(r["id"]))
            .ToList();
        var levels = new List<AffinityLevel>();
        for (var i = 0; i < rows.Count; i++)
        {
            var bonus = Long(rows[i]["attr_bonus"]) / 10000.0;
            if (i + 1 < rows.Count && Long(rows[i + 1]["attr_bonus"]) / 10000.0 == bonus)
            {
                continue;
            }

            var name = Text(rows[i]["name"]);
            if (levels.Any(l => l.Name == name) && rows[i]["lower_bound"] is { } lower)
            {
                name = $"{name} ({Long(lower) / 100})";
            }

            levels.Add(new AffinityLevel(name, bonus));
        }

        return levels.Count > 0 ? levels : [new AffinityLevel("", 0)];
    }

    // Reads what each ship's stats grow from. Field names follow the game's tables; ShipGrowth combines them.
    private sealed class GrowthTables(
        JsonObject statistics,
        JsonObject strengthen,
        JsonObject blueprints,
        JsonObject blueprintLevels,
        JsonObject metaStrengthen,
        JsonObject metaRepairs,
        JsonObject metaRepairEffects,
        JsonObject retrofits,
        JsonObject retrofitNodes,
        IReadOnlyDictionary<string, int> attributeIndex)
    {
        // The five stats feeding and development levels raise, in the order their arrays list them
        // (ShipModAttr.ID_TO_ATTR and BLUEPRINT_ATTRS).
        private static readonly int[] RaisedStats = [ShipGrowth.Cannon, ShipGrowth.Torpedo, ShipGrowth.AntiAircraft, ShipGrowth.Air, ShipGrowth.Reload];

        public ShipGrowth For(long group, long id, JsonObject stats, JsonObject? template, ShipCollection collection)
        {
            var key = group.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var (kind, development) = collection switch
            {
                ShipCollection.Research when blueprints[key] is JsonObject blueprint => (DevelopmentKind.Research, Research(blueprint)),
                ShipCollection.Meta when metaStrengthen[key] is JsonObject meta => (DevelopmentKind.Meta, Meta(meta)),
                _ => (DevelopmentKind.Enhancement, Enhancement(template)),
            };
            var (retrofit, retrofitted) = Retrofit(key, id);
            return new ShipGrowth
            {
                Base = Row(stats),
                Kind = kind,
                Development = development,
                Retrofit = retrofit,
                Retrofitted = retrofitted,
                ArmorType = stats["armor_type"] is { } armor ? (int)Long(armor) : 0,
                OilStart = template?["oil_at_start"] is { } start ? (int)Long(start) : 0,
                OilEnd = template?["oil_at_end"] is { } end ? (int)Long(end) : 0,
            };
        }

        private static GrowthRow Row(JsonObject stats) =>
            new(Values(stats["attrs"]), Values(stats["attrs_growth"]), Values(stats["attrs_growth_extra"]));

        private static double[] Values(JsonNode? node)
        {
            var values = new double[ShipGrowth.Count];
            if (node is JsonArray array)
            {
                for (var i = 0; i < values.Length && i < array.Count; i++)
                {
                    values[i] = array[i]?.GetValueKind() == JsonValueKind.Number ? array[i]!.GetValue<double>() : 0;
                }
            }

            return values;
        }

        private double[] Enhancement(JsonObject? template)
        {
            var development = new double[ShipGrowth.Count];
            if (template?["strengthen_id"] is { } strengthenId && strengthen[Long(strengthenId).ToString()]?["durability"] is JsonArray caps)
            {
                for (var i = 0; i < RaisedStats.Length && i < caps.Count; i++)
                {
                    development[RaisedStats[i]] = caps[i]!.GetValue<double>();
                }
            }

            return development;
        }

        // ShipBluePrint:getTotalAdditions with every development level done: each level's effect points, divided by
        // attr_exp, plus the flat bonuses (effect_attr) of development levels and fate simulation stages.
        private double[] Research(JsonObject blueprint)
        {
            var development = new double[ShipGrowth.Count];
            var attrExp = blueprint["attr_exp"] as JsonArray ?? [];
            var levels = (blueprint["strengthen_effect"] as JsonArray ?? []).Select(Long).ToList();
            foreach (var levelId in levels)
            {
                if (blueprintLevels[levelId.ToString()]?["effect"] is JsonArray effect)
                {
                    for (var i = 0; i < RaisedStats.Length && i < effect.Count && i < attrExp.Count; i++)
                    {
                        development[RaisedStats[i]] += effect[i]!.GetValue<double>() / Math.Max(1, attrExp[i]!.GetValue<double>());
                    }
                }
            }

            foreach (var levelId in levels.Concat((blueprint["fate_strengthen"] as JsonArray ?? []).Select(Long)))
            {
                AddPairs(development, blueprintLevels[levelId.ToString()]?["effect_attr"] as JsonArray);
            }

            return development;
        }

        // MetaCharacter:getAttrAddition with every repair done: each repair item's bonus, plus every repair milestone's
        // (repair_effect lists them by progress).
        private double[] Meta(JsonObject meta)
        {
            var development = new double[ShipGrowth.Count];
            foreach (var (name, node) in meta)
            {
                if (name.StartsWith("repair_", StringComparison.Ordinal) && name != "repair_effect" && node is JsonArray items)
                {
                    foreach (var item in items)
                    {
                        if (item?.GetValueKind() == JsonValueKind.Number && metaRepairs[Long(item).ToString()]?["effect_attr"] is JsonArray pair)
                        {
                            AddPair(development, pair);
                        }
                    }
                }
            }

            foreach (var milestone in meta["repair_effect"] as JsonArray ?? [])
            {
                if (milestone is JsonArray { Count: 2 } entry && metaRepairEffects[Long(entry[1]).ToString()]?["effect_attr"] is JsonArray pairs)
                {
                    AddPairs(development, pairs);
                }
            }

            return development;
        }

        // Every retrofit node at its top level (ship_data_trans lists the nodes per ship group). A node that swaps the
        // ship to another statistics row ("ship_id" pairs) gives the row the retrofitted ship uses.
        private (double[]? Bonus, GrowthRow? Row) Retrofit(string group, long id)
        {
            if (retrofits[group]?["transform_list"] is not JsonArray columns)
            {
                return (null, null);
            }

            var bonus = new double[ShipGrowth.Count];
            GrowthRow? row = null;
            foreach (var cell in columns.OfType<JsonArray>().SelectMany(c => c.OfType<JsonArray>()))
            {
                if (cell.Count < 2 || retrofitNodes[Long(cell[1]).ToString()] is not JsonObject node)
                {
                    continue;
                }

                foreach (var level in node["effect"] as JsonArray ?? [])
                {
                    foreach (var (name, value) in level as JsonObject ?? [])
                    {
                        if (attributeIndex.TryGetValue(name, out var index) && value?.GetValueKind() == JsonValueKind.Number)
                        {
                            bonus[index] += value.GetValue<double>();
                        }
                    }
                }

                foreach (var swap in (node["ship_id"] as JsonArray ?? []).OfType<JsonArray>())
                {
                    if (swap.Count == 2 && (row is null || Long(swap[0]) == id) && statistics[Long(swap[1]).ToString()] is JsonObject stats)
                    {
                        row = Row(stats);
                    }
                }
            }

            return (bonus, row);
        }

        private void AddPairs(double[] development, JsonArray? pairs)
        {
            foreach (var pair in pairs ?? [])
            {
                if (pair is JsonArray entry)
                {
                    AddPair(development, entry);
                }
            }
        }

        // ["durability", 199]
        private void AddPair(double[] development, JsonArray pair)
        {
            if (pair.Count == 2 && attributeIndex.TryGetValue(Text(pair[0]), out var index) && pair[1]?.GetValueKind() == JsonValueKind.Number)
            {
                development[index] += pair[1]!.GetValue<double>();
            }
        }
    }

    // The EN client still names the Bilibili collaboration in Chinese, so that one name is given here.
    internal static string NationName(long nation, Func<string, string> tip) => nation switch
    {
        102 => "Bilibili",
        _ when NationKeys.TryGetValue(nation, out var key) => tip(key),
        _ => tip("word_shipNation_other"),
    };

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
        foreach (var (key, label, resource) in voiceKeys)
        {
            var text = key.StartsWith("main", StringComparison.Ordinal) && int.TryParse(key.AsSpan(4), out var n)
                ? (n <= mains.Length ? mains[n - 1] : "")
                : Line(key);
            if (text.Trim().Length > 0)
            {
                lines.Add(new VoiceLine(label, text.Trim(), key, resource.Length > 0 ? resource : key));
            }
        }

        return lines;
    }

    // For interactive Live2D skins: action clip -> the idle clip the model keeps playing after it.
    public IReadOnlyDictionary<string, string> Live2DIdleAfter(SkinInfo skin) =>
        live2DIdles.TryGetValue(skin.Id, out var map) ? map : new Dictionary<string, string>();

    // Animation name -> the voice lines the game plays it with, for this skin's lines only ("Main screen 1, Normal
    // touch"). Chibis use character_voice's spine_action and Live2D models its l2d_action; faces and dynamic skin
    // expressions use the skin's ship_skin_expression entry, whose keys spell main lines as main_1 rather than main1.
    public IReadOnlyDictionary<string, string> AnimationLabels(ShipInfo ship, SkinInfo skin, AnimationSet set) =>
        AnimationLines(ship, skin, set)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(line => line.Label)), StringComparer.Ordinal);

    // Animation name -> the voice lines the game plays it with, in profile order.
    public IReadOnlyDictionary<string, IReadOnlyList<VoiceLine>> AnimationVoiceLines(ShipInfo ship, SkinInfo skin, AnimationSet set) =>
        AnimationLines(ship, skin, set).ToDictionary(g => g.Key, g => (IReadOnlyList<VoiceLine>)g.ToList(), StringComparer.Ordinal);

    private IEnumerable<IGrouping<string, VoiceLine>> AnimationLines(ShipInfo ship, SkinInfo skin, AnimationSet set)
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
            .Select(line => (Animation: Animation(line.Key), Line: line))
            .Where(p => !string.IsNullOrEmpty(p.Animation))
            .GroupBy(p => p.Animation!, p => p.Line, StringComparer.Ordinal);
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
