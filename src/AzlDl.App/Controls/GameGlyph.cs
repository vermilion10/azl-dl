using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace AzlDl.App.Controls;

// One of the game's single-colour icons (stat icons, faction emblems) from art/ui, drawn in the text colour so it
// reads on light and dark backgrounds alike. Draws nothing until the art is downloaded.
public sealed class GameGlyph : Control
{
    // The art/ui file name without .png, such as attr_cannon or nation_1.
    public static readonly StyledProperty<string?> ArtProperty = AvaloniaProperty.Register<GameGlyph, string?>(nameof(Art));

    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<GameGlyph>();

    static GameGlyph() => AffectsRender<GameGlyph>(ArtProperty, ForegroundProperty);

    public string? Art
    {
        get => GetValue(ArtProperty);
        set => SetValue(ArtProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (string.IsNullOrEmpty(Art) || GameArt.Get(Art) is not { } image || Foreground is not { } brush)
        {
            return;
        }

        var area = new Rect(Bounds.Size);
        using (context.PushOpacityMask(new ImageBrush(image) { Stretch = Stretch.Uniform }, area))
        {
            context.FillRectangle(brush, area);
        }
    }
}
