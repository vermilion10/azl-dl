using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace AzlDl.App.Controls;

// Lets the user zoom and move its child: scroll to zoom around the pointer, drag to move, double-click to reset.
// With keyboard focus, + and - zoom, the arrow keys move and 0 resets. A new picture in a child Image resets the view.
public class ZoomPanel : Decorator
{
    private const double MinZoom = 1;
    private const double MaxZoom = 12;
    private const double KeyStep = 40;

    private double zoom = 1;
    private Vector offset;
    private Point? dragFrom;

    public ZoomPanel()
    {
        ClipToBounds = true;
        Focusable = true;
        Background = Brushes.Transparent;
        Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<ZoomPanel>();

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public void Reset()
    {
        zoom = 1;
        offset = default;
        Apply();
    }

    public override void Render(DrawingContext context)
    {
        // A transparent fill so the whole panel takes pointer input, not just the picture.
        if (Background is { } background)
        {
            context.FillRectangle(background, new Rect(Bounds.Size));
        }

        base.Render(context);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ChildProperty)
        {
            if (change.OldValue is Image oldImage)
            {
                oldImage.PropertyChanged -= OnChildChanged;
            }

            if (change.NewValue is Control child)
            {
                child.RenderTransformOrigin = RelativePoint.TopLeft;
                if (child is Image image)
                {
                    image.PropertyChanged += OnChildChanged;
                }
            }

            Reset();
        }
    }

    private void OnChildChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Image.SourceProperty)
        {
            Reset();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ZoomAt(Math.Pow(1.2, e.Delta.Y), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.ClickCount == 2)
        {
            Reset();
            e.Handled = true;
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            dragFrom = e.GetPosition(this);
            e.Pointer.Capture(this);
            Focus();
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (dragFrom is { } from)
        {
            var at = e.GetPosition(this);
            offset += at - from;
            dragFrom = at;
            Apply();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        dragFrom = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        dragFrom = null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add:
                ZoomAt(1.2, center);
                break;
            case Key.OemMinus or Key.Subtract:
                ZoomAt(1 / 1.2, center);
                break;
            case Key.D0 or Key.NumPad0:
                Reset();
                break;
            case Key.Left:
                Move(KeyStep, 0);
                break;
            case Key.Right:
                Move(-KeyStep, 0);
                break;
            case Key.Up:
                Move(0, KeyStep);
                break;
            case Key.Down:
                Move(0, -KeyStep);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void Move(double x, double y)
    {
        offset += new Vector(x, y);
        Apply();
    }

    // Keeps the point under the pointer still while the scale changes.
    private void ZoomAt(double factor, Point at)
    {
        var next = Math.Clamp(zoom * factor, MinZoom, MaxZoom);
        offset = (Vector)at - (((Vector)at - offset) * (next / zoom));
        zoom = next;
        if (zoom <= MinZoom)
        {
            offset = default;
        }

        Apply();
    }

    private void Apply()
    {
        if (Child is { } child)
        {
            child.RenderTransform = new MatrixTransform(Matrix.CreateScale(zoom, zoom) * Matrix.CreateTranslation(offset.X, offset.Y));
        }
    }
}
