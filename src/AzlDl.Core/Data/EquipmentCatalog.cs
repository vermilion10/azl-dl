using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AzlDl.Core.Data;

// One stat at every enhancement level, +0 first ("12" ... "18", or "48 x 2" ... "67 x 2" for damage). Icon names the
// stat's game icon: an attricon sprite (cannon, antiaircraft, ...) or "damage" from equipmentattr.
public sealed record EquipmentStat(string Label, string Icon, IReadOnlyList<string> Values)
{
    public string Base => Values[0];

    public string Max => Values[^1];

    public string At(int level) => Values[Math.Clamp(level, 0, Values.Count - 1)];
}

// A piece of equipment as the game's codex lists it: each tier of a gun (T1, T2, T3) is its own entry.
public sealed record EquipmentInfo(
    long Id,
    string Name,
    int Rarity,
    RarityTier Tier,
    int Tech,
    long TypeId,
    string TypeName,
    long NationId,
    string NationName,
    string Icon,
    string Description,
    string Speciality,
    string Ammo,
    int MaxLevel,
    IReadOnlyList<EquipmentStat> Stats,
    IReadOnlyList<string> UsableBy)
{
    // Tier 0 is equipment outside the research tiers (event and special gear), which the game doesn't label.
    public string TechCode => Tech > 0 ? $"T{Tech}" : "";

    public string TechTitle => Tech > 0 ? $"Tier {Tech}" : "";

    public bool IsEquippable => UsableBy.Count > 0;

    // The game's icon background for this rarity: 1 for Common (one and two stars) up to 5 for Ultra Rare.
    public int RarityKey => Math.Max(1, Rarity - 1);

    public IReadOnlyList<int> StarSlots => Enumerable.Range(0, Rarity).ToList();

    // Rarity as stars, as the game draws them on the icon.
    public string Stars => new('★', Rarity);
}

// Reads equipment from the decoded equip_data_statistics and equip_data_template tables. Enhancement levels only store
// what changes from the base row, so each level is the base row with that level's fields laid over it.
public sealed partial class EquipmentCatalog
{
    public IReadOnlyList<EquipmentInfo> Equipment { get; }

    private EquipmentCatalog(IReadOnlyList<EquipmentInfo> equipment) => Equipment = equipment;

    public static IReadOnlyList<string> RequiredFiles { get; } =
    [
        "config/equip_data_statistics.json", "config/equip_data_template.json", "config/gametip.json",
        "sharecfg/equip_data_by_type.json", "sharecfg/attribute_info_by_type.json", "sharecfg/ship_data_by_type.json",
    ];

    public static IReadOnlyList<string> MissingFiles(string dataFolder) =>
        RequiredFiles.Where(f => !File.Exists(Path.Combine(dataFolder, f))).ToList();

    public static EquipmentCatalog Load(string dataFolder)
    {
        JsonObject Read(string file) =>
            JsonNode.Parse(File.ReadAllBytes(Path.Combine(dataFolder, file)))?.AsObject()
            ?? throw new InvalidDataException($"{file} is empty.");

        var statistics = Read("config/equip_data_statistics.json");
        var templates = Read("config/equip_data_template.json");
        var tips = Read("config/gametip.json");
        var types = Read("sharecfg/equip_data_by_type.json");
        var attributes = Read("sharecfg/attribute_info_by_type.json");
        var shipTypes = Read("sharecfg/ship_data_by_type.json");
        var codeFile = Path.Combine(dataFolder, "sharecfg/equip_data_code.json");
        var codes = File.Exists(codeFile) ? Read("sharecfg/equip_data_code.json") : [];

        string Tip(string key) => tips[key]?["tip"]?.GetValue<string>().Trim() ?? key;

        // "antiaircraft" -> "AA", as the ship stats are labelled.
        var attributeLabels = attributes.Select(p => p.Value).OfType<JsonObject>()
            .Where(a => a["name"] is not null && a["condition"] is not null)
            .ToDictionary(a => Text(a["name"]), a => Text(a["condition"]), StringComparer.Ordinal);
        attributeLabels.TryAdd("oxy_max", "Oxygen");

        var equipment = new List<EquipmentInfo>();
        foreach (var (key, node) in templates)
        {
            // Codex entries are the base rows: level 1, with no previous level.
            if (node is not JsonObject template || template["prev"] is { } prev && Number(prev) != 0
                || statistics[key] is not JsonObject baseRow || baseRow["name"] is null)
            {
                continue;
            }

            // Follow the enhancement chain to its last level.
            var levels = new List<JsonObject> { baseRow };
            var next = template["next"] is { } n ? Number(n) : 0;
            while (next != 0 && levels.Count < 30 && statistics[next.ToString(CultureInfo.InvariantCulture)] is JsonObject row)
            {
                levels.Add(row);
                next = templates[next.ToString(CultureInfo.InvariantCulture)]?["next"] is { } after ? Number(after) : 0;
            }

            JsonNode? Field(JsonObject level, string name) => level[name] ?? baseRow[name];

            var stats = new List<EquipmentStat>();
            if (Text(baseRow["damage"]) is { Length: > 0 })
            {
                stats.Add(new EquipmentStat("Damage", "damage", levels.Select(l => Text(Field(l, "damage"))).ToList()));
            }

            for (var i = 1; i <= 3; i++)
            {
                if (Text(baseRow[$"attribute_{i}"]) is { Length: > 0 } attribute)
                {
                    var field = $"value_{i}";
                    stats.Add(new EquipmentStat(attributeLabels.GetValueOrDefault(attribute, attribute), attribute,
                        levels.Select(l => Value(Field(l, field))).ToList()));
                }
            }

            var type = Number(baseRow["type"]);
            var rarity = (int)Number(baseRow["rarity"]);
            var nation = baseRow["nationality"] is { } nat ? Number(nat) : 0;
            var ammo = baseRow["ammo"] is { } a ? Number(a) : 0;
            var forbidden = (template["ship_type_forbidden"] as JsonArray ?? []).Select(Number).ToHashSet();
            var usable = (baseRow["part_main"] as JsonArray ?? []).Concat(baseRow["part_sub"] as JsonArray ?? [])
                .Select(Number).Distinct().Where(t => !forbidden.Contains(t)).Order()
                .Select(t => shipTypes[t.ToString(CultureInfo.InvariantCulture)]?["type_name"]?.GetValue<string>().Trim())
                .OfType<string>().Distinct().ToList();

            equipment.Add(new EquipmentInfo(
                Number(baseRow["id"]),
                Text(baseRow["name"]).Trim(),
                rarity,
                rarity switch { <= 2 => RarityTier.Common, 3 => RarityTier.Rare, 4 => RarityTier.Elite, 5 => RarityTier.SuperRare, _ => RarityTier.UltraRare },
                baseRow["tech"] is { } tech ? (int)Number(tech) : 0,
                type,
                types[type.ToString(CultureInfo.InvariantCulture)]?["type_name2"]?.GetValue<string>().Trim() ?? $"Type {type}",
                nation,
                ShipCatalog.NationName(nation, Tip),
                Text(baseRow["icon"]),
                Text(baseRow["descrip"]).Trim(),
                Speciality(Text(baseRow["speciality"]), codes),
                ammo > 0 && tips[$"equip_ammo_type_{ammo}"] is not null ? Tip($"equip_ammo_type_{ammo}") : "",
                levels.Count - 1,
                stats,
                usable));
        }

        return new EquipmentCatalog(equipment.OrderByDescending(e => e.Rarity).ThenBy(e => e.Id).ToList());
    }

    // Specialities are either text ("Volley") or codes into equip_data_code ("<[VOL]>"); "N/A" means none.
    private static string Speciality(string text, JsonObject codes)
    {
        var resolved = SpecialityCode().Replace(text, m => codes[m.Groups[1].Value]?["text"]?.GetValue<string>() ?? m.Groups[1].Value);
        resolved = resolved.Replace('\n', ' ').Trim();
        return resolved is "N/A" or "NA" ? "" : resolved;
    }

    [GeneratedRegex(@"<\[([A-Z]+)\]>")]
    private static partial Regex SpecialityCode();

    private static long Number(JsonNode? node) => node!.GetValueKind() == JsonValueKind.Number
        ? (long)node.GetValue<double>()
        : long.Parse(node.GetValue<string>(), CultureInfo.InvariantCulture);

    private static string Text(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : "";

    private static string Value(JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.String => node.GetValue<string>(),
        JsonValueKind.Number => node.GetValue<double>().ToString("0.##", CultureInfo.InvariantCulture),
        _ => "",
    };
}
