using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AzlDl.App.Controls;

// A border whose top-right and bottom-left corners are cut at 45 degrees, the panel shape of the game's own UI.
// With Chamfer = 0 it draws a plain rectangle, so styles turn the look on and off through the PanelChamfer token.
public class ChamferBorder : Decorator
{
    public static readonly StyledProperty<double> ChamferProperty =
        AvaloniaProperty.Register<ChamferBorder, double>(nameof(Chamfer));

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<ChamferBorder>();

    public static readonly StyledProperty<IBrush?> BorderBrushProperty =
        Border.BorderBrushProperty.AddOwner<ChamferBorder>();

    public static readonly StyledProperty<double> BorderThicknessProperty =
        AvaloniaProperty.Register<ChamferBorder, double>(nameof(BorderThickness));

    static ChamferBorder()
    {
        AffectsRender<ChamferBorder>(ChamferProperty, BackgroundProperty, BorderBrushProperty, BorderThicknessProperty);
        AffectsArrange<ChamferBorder>(ChamferProperty);
    }

    public double Chamfer
    {
        get => GetValue(ChamferProperty);
        set => SetValue(ChamferProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? BorderBrush
    {
        get => GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public double BorderThickness
    {
        get => GetValue(BorderThicknessProperty);
        set => SetValue(BorderThicknessProperty, value);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        Clip = Outline(new Rect(size), Chamfer);
        return size;
    }

    public override void Render(DrawingContext context)
    {
        var inset = BorderThickness / 2;
        var outline = Outline(new Rect(Bounds.Size).Deflate(inset), Math.Max(0, Chamfer - inset));
        var pen = BorderBrush is null || BorderThickness <= 0 ? null : new Pen(BorderBrush, BorderThickness);
        context.DrawGeometry(Background, pen, outline);
    }

    private static Geometry Outline(Rect r, double cut)
    {
        cut = Math.Min(cut, Math.Min(r.Width, r.Height) / 2);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(r.TopLeft, true);
            g.LineTo(new Point(r.Right - cut, r.Top));
            g.LineTo(new Point(r.Right, r.Top + cut));
            g.LineTo(r.BottomRight);
            g.LineTo(new Point(r.Left + cut, r.Bottom));
            g.LineTo(new Point(r.Left, r.Bottom - cut));
            g.EndFigure(true);
        }

        return geometry;
    }
}
