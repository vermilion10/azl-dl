using Avalonia;
using Avalonia.Controls;
using AzlDl.App.Theming;

namespace AzlDl.App.Controls;

// The large ship name. Its font comes from the style tokens; Avalonia has no text-transform, so the uppercase
// styles are applied here by re-reading the ShipNameUppercase token whenever resources change.
public class ShipNameText : TextBlock
{
    public static readonly StyledProperty<string?> NameTextProperty =
        AvaloniaProperty.Register<ShipNameText, string?>(nameof(NameText));

    static ShipNameText()
    {
        NameTextProperty.Changed.AddClassHandler<ShipNameText>((text, _) => text.Refresh());
    }

    public string? NameText
    {
        get => GetValue(NameTextProperty);
        set => SetValue(NameTextProperty, value);
    }

    protected override void OnAttachedToLogicalTree(Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        ResourcesChanged += OnResourcesChanged;
        Refresh();
    }

    protected override void OnDetachedFromLogicalTree(Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        ResourcesChanged -= OnResourcesChanged;
        base.OnDetachedFromLogicalTree(e);
    }

    private void OnResourcesChanged(object? sender, ResourcesChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var upper = this.TryFindResource(StyleTokens.ShipNameUppercase, out var value) && value is true;
        Text = upper ? NameText?.ToUpperInvariant() : NameText;
    }
}
