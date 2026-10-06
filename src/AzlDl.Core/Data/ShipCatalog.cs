using System.Text.Json;
using System.Text.Json.Nodes;

namespace AzlDl.Core.Data;

public sealed record StatValue(string Label, string Value);

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

    public IReadOnlyList<ShipInfo> Ships { get; }

    private ShipCatalog(IReadOnlyList<ShipInfo> ships) => Ships = ships;

    // The tables the catalog needs, relative to the data folder.
    public static IReadOnlyList<string> RequiredFiles { get; } =
    [
        "config/ship_data_statistics.json", "config/ship_data_template.json", "config/ship_skin_template.json",
        "config/gametip.json", "sharecfg/ship_data_by_type.json", "sharecfg/attribute_info_by_type.json",
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
                skinsByGroup.GetValueOrDefault(group) ?? []));
        }

        return new ShipCatalog(ships.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static long Long(JsonNode? node) => node!.GetValueKind() == JsonValueKind.Number
        ? (long)node.GetValue<double>()
        : long.Parse(node.GetValue<string>());

    private static string Text(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : "";

    private static string FormatNumber(JsonNode? node) =>
        node is null ? "" : node.GetValue<double>().ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
