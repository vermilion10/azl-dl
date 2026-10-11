using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AzlDl.Core.Data;

namespace AzlDl.App.Controls;

// Card frame colours by rarity tier. They follow the game's own coding (grey Common, blue Rare, purple Elite, gold Super
// Rare, iridescent Ultra Rare and Decisive, amber Priority, crimson META) so a player recognises a ship's rarity at a
// glance. That's why they stay the same in every style and theme: they are game information, not decoration.
public static class RarityBrushes
{
    private static readonly Dictionary<RarityTier, (string[] Background, string[] Border)> Colors = new()
    {
        [RarityTier.Common] = (["#5b626d", "#9aa1ab"], ["#b9bec6"]),
        [RarityTier.Rare] = (["#23508a", "#5ea6e8"], ["#7cbcf2"]),
        [RarityTier.Elite] = (["#4b2f7a", "#a77fdc"], ["#c3a1ee"]),
        [RarityTier.SuperRare] = (["#7a5418", "#e6bf55"], ["#f2d47c"]),
        [RarityTier.UltraRare] = (["#23708a", "#8f87e0", "#f3a8c8"], ["#ffd0e4", "#b9b6ff", "#9ff0ea"]),
        [RarityTier.Priority] = (["#6b3a12", "#d98a2b"], ["#f5b35c"]),
        [RarityTier.Decisive] = (["#2b2466", "#8b4fd0", "#e46aa4"], ["#ff9ec7", "#a993ff", "#7fe3ff"]),
        [RarityTier.Meta] = (["#180c10", "#6e1622"], ["#e0414f"]),
    };

    private static readonly Dictionary<RarityTier, IBrush> Backgrounds = Colors.ToDictionary(p => p.Key, p => Gradient(p.Value.Background, vertical: true));

    private static readonly Dictionary<RarityTier, IBrush> Borders = Colors.ToDictionary(p => p.Key, p => Gradient(p.Value.Border, vertical: false));

    public static readonly IValueConverter Background =
        new FuncValueConverter<RarityTier?, IBrush?>(tier => tier is { } t ? Backgrounds[t] : null);

    public static readonly IValueConverter Border =
        new FuncValueConverter<RarityTier?, IBrush?>(tier => tier is { } t ? Borders[t] : null);

    private static IBrush Gradient(string[] colors, bool vertical)
    {
        if (colors.Length == 1)
        {
            return new ImmutableSolidColorBrush(Color.Parse(colors[0]));
        }

        var stops = colors.Select((c, i) => new ImmutableGradientStop(i / (double)(colors.Length - 1), Color.Parse(c))).ToList();
        return vertical
            ? new ImmutableLinearGradientBrush(stops, startPoint: new RelativePoint(0.5, 1, RelativeUnit.Relative),
                endPoint: new RelativePoint(0.5, 0, RelativeUnit.Relative))
            : new ImmutableLinearGradientBrush(stops, startPoint: new RelativePoint(0, 0, RelativeUnit.Relative),
                endPoint: new RelativePoint(1, 1, RelativeUnit.Relative));
    }
}
