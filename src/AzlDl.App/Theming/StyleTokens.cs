namespace AzlDl.App.Theming;

public enum AppStyle
{
    ChartRoom,
    RegistryBook,
    PortMenu,
}

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

// Every resource key a style dictionary must define. Views only reference these keys, so the styles can't drift into
// separate copies of each screen; anything a style needs to change has to become a key here first.
public static class StyleTokens
{
    // Brushes, defined per theme variant (Light and Dark).
    public const string BgBrush = nameof(BgBrush);
    public const string SurfaceBrush = nameof(SurfaceBrush);
    public const string InkBrush = nameof(InkBrush);
    public const string MutedBrush = nameof(MutedBrush);
    public const string LineBrush = nameof(LineBrush);
    public const string AccentBrush = nameof(AccentBrush);
    public const string AccentInkBrush = nameof(AccentInkBrush);
    public const string StageBrush = nameof(StageBrush);

    public static readonly string[] Brushes =
        [BgBrush, SurfaceBrush, InkBrush, MutedBrush, LineBrush, AccentBrush, AccentInkBrush, StageBrush];

    // Fonts and shape, the same in both variants.
    public const string DisplayFont = nameof(DisplayFont);
    public const string BodyFont = nameof(BodyFont);
    public const string DataFont = nameof(DataFont);
    public const string PanelChamfer = nameof(PanelChamfer);
    public const string ShipNameSize = nameof(ShipNameSize);
    public const string ShipNameWeight = nameof(ShipNameWeight);
    public const string ShipNameStyle = nameof(ShipNameStyle);
    public const string ShipNameUppercase = nameof(ShipNameUppercase);

    // Height of the rarity band above the ship name; 0 hides it.
    public const string NameBandHeight = nameof(NameBandHeight);

    // Whether skins are labelled as numbered plates ("Pl. 3").
    public const string SkinPlateNumbers = nameof(SkinPlateNumbers);

    // Templates for parts whose structure differs between styles.
    public const string StatsTemplate = nameof(StatsTemplate);

    public static readonly string[] Shared =
    [
        DisplayFont, BodyFont, DataFont, PanelChamfer, ShipNameSize, ShipNameWeight, ShipNameStyle, ShipNameUppercase,
        NameBandHeight, SkinPlateNumbers,
        StatsTemplate,
    ];
}
