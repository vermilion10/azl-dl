using Avalonia;
using Avalonia.Controls;
using AzlDl.App.Theming;

namespace AzlDl.App.Controls;

// Renders its content with one style's resources, whichever style the app is using, so Settings can show all three
// side by side.
public class StylePreview : ContentControl
{
    public static readonly StyledProperty<AppStyle> PreviewStyleProperty =
        AvaloniaProperty.Register<StylePreview, AppStyle>(nameof(PreviewStyle));

    static StylePreview()
    {
        PreviewStyleProperty.Changed.AddClassHandler<StylePreview>((preview, _) => preview.Swap());
    }

    public StylePreview() => Swap();

    public AppStyle PreviewStyle
    {
        get => GetValue(PreviewStyleProperty);
        set => SetValue(PreviewStyleProperty, value);
    }

    private void Swap()
    {
        Resources.MergedDictionaries.Clear();
        Resources.MergedDictionaries.Add(StyleManager.Load(PreviewStyle));
    }
}
