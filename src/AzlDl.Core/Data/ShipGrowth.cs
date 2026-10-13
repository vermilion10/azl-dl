namespace AzlDl.Core.Data;

// An affinity stage and the share it adds to most stats (intimacy_template attr_bonus: Love adds 6%).
public sealed record AffinityLevel(string Name, double Bonus)
{
    public string Title => Bonus > 0 ? $"{Name}, +{Bonus * 100:0}%" : Name;
}

// How a ship gains stats beyond levelling: feeding (ship_data_strengthen), research development levels and fate
// simulation (ship_strengthen_blueprint), or META repairs (ship_strengthen_meta).
public enum DevelopmentKind
{
    Enhancement,
    Research,
    Meta,
}

// One ship_data_statistics row's starting values and growth per level, in attrs order.
public sealed record GrowthRow(double[] Attrs, double[] Growth, double[] Extra);

// The figures a ship's stats are built from, and the game's way of combining them (Ship:getShipProperty,
// getShipProperties and getProperties in model/vo/ship.lua).
public sealed class ShipGrowth
{
    public const int MaxLevel = 125;

    // attrs indices, in the game's Ship.PROPERTIES order.
    public const int Durability = 0, Cannon = 1, Torpedo = 2, AntiAircraft = 3, Air = 4, Reload = 5, Armor = 6, Hit = 7,
        Dodge = 8, Speed = 9, Luck = 10, AntiSub = 11, Count = 12;

    // Affinity multiplies every stat except armor, speed and luck (Ship:intimacyAdditions).
    private static readonly int[] AffinityStats = [Durability, Cannon, Torpedo, AntiAircraft, AntiSub, Air, Reload, Hit, Dodge];

    // From this level on, attrs_growth_extra adds to the growth (gameset extra_attr_level_limit).
    private const int ExtraGrowthLevel = 100;

    public required GrowthRow Base { get; init; }

    // The statistics row the ship switches to once retrofitted, when the retrofit swaps it.
    public GrowthRow? Retrofitted { get; init; }

    // Retrofit node bonuses with every node at its top level; null when the ship has no retrofit.
    public double[]? Retrofit { get; init; }

    public DevelopmentKind Kind { get; init; }

    // Enhancement: the most each stat can be fed (the strengthen row's "durability" field). Research and META: the
    // total bonus with every development level, fate simulation stage or repair done.
    public double[] Development { get; init; } = new double[Count];

    public int ArmorType { get; init; }

    public int OilStart { get; init; }

    public int OilEnd { get; init; }

    public bool HasRetrofit => Retrofit is not null;

    // The ship's stats at a level and affinity, with development fully done or not done at all and with or without its
    // retrofit. Speed keeps its fraction; everything else is rounded down, as the game shows it.
    public double[] At(int level, double affinity, bool developed, bool retrofit)
    {
        level = Math.Clamp(level, 1, MaxLevel);
        var row = retrofit && Retrofitted is not null ? Retrofitted : Base;
        var values = new double[Count];
        for (var i = 0; i < Count; i++)
        {
            var value = row.Attrs[i] + (level - 1) * row.Growth[i] / 1000;
            if (level > ExtraGrowthLevel)
            {
                value += (level - ExtraGrowthLevel) * row.Extra[i] / 1000;
            }

            if (developed)
            {
                value += Kind switch
                {
                    // Ship:getModAttrTopLimit: the cap opens up with level, 30% plus 7% per 10 levels, all of it from 100.
                    DevelopmentKind.Enhancement => Floor((3 + 7 * Math.Min(level, ExtraGrowthLevel) / 100.0) * Development[i] * 0.1),
                    DevelopmentKind.Research => Floor(Development[i]),
                    _ => Development[i],
                };
            }

            values[i] = value;
        }

        foreach (var i in AffinityStats)
        {
            values[i] *= 1 + affinity;
        }

        for (var i = 0; i < Count; i++)
        {
            if (retrofit && Retrofit is not null)
            {
                values[i] += Retrofit[i];
            }

            if (i != Speed)
            {
                values[i] = Floor(values[i]);
            }
        }

        return values;
    }

    // Oil spent per battle: oil_at_start plus oil_at_end scaled by the level's fight_oil_ratio (ship_level).
    public int OilCost(int level, IReadOnlyDictionary<int, double> oilRatios) =>
        OilStart + (int)Math.Floor(OilEnd * oilRatios.GetValueOrDefault(Math.Clamp(level, 1, MaxLevel), 10000) / 10000);

    // Sums of fractions such as 0.1 * 30 land a hair under the whole number; the small margin keeps them from flooring
    // one lower than the game does.
    private static double Floor(double value) => Math.Floor(value + 1e-9);
}
