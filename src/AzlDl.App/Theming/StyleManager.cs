using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;

namespace AzlDl.App.Theming;

// Applies a style and a light/dark mode to the running app. The style is one ResourceInclude in the application's
// merged dictionaries, swapped in place; every view uses DynamicResource, so the change shows at once.
public static class StyleManager
{
    private static ResourceInclude? current;

    public static Uri SourceFor(AppStyle style) => new($"avares://azl-dl-app/Styles/{style}.axaml");

    public static void Apply(Application app, AppStyle style, ThemeMode mode)
    {
        var include = new ResourceInclude(SourceFor(style)) { Source = SourceFor(style) };
        var merged = app.Resources.MergedDictionaries;
        if (current is not null && merged.IndexOf(current) is var index and >= 0)
        {
            merged[index] = include;
        }
        else
        {
            merged.Add(include);
        }

        current = include;

        app.RequestedThemeVariant = mode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    // A style's dictionary on its own, for previews that show a style other than the current one.
    public static IResourceDictionary Load(AppStyle style) =>
        (IResourceDictionary)new ResourceInclude(SourceFor(style)) { Source = SourceFor(style) }.Loaded;
}
