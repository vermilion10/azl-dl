using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace AzlDl.App.Controls;

// The game's card art extracted into art/ui (card_4.png, frame_5_0.png, hull_3.png, nation_1.png, star.png). Views ask
// for an image by prefix and key; before the art is downloaded every lookup is null and the views fall back to drawn
// frames and text tags.
public static class GameArt
{
    private static readonly Dictionary<string, Bitmap?> Loaded = new(StringComparer.Ordinal);
    private static string folder = "";

    public static void Use(string artFolder)
    {
        if (!string.Equals(folder, artFolder, StringComparison.OrdinalIgnoreCase))
        {
            folder = artFolder;
            Loaded.Clear();
        }
    }

    // Forgets what was looked up, so files downloaded since are read.
    public static void Refresh() => Loaded.Clear();

    public static Bitmap? Get(string name)
    {
        if (folder.Length == 0)
        {
            return null;
        }

        if (!Loaded.TryGetValue(name, out var image))
        {
            var path = Path.Combine(folder, name + ".png");
            try
            {
                image = File.Exists(path) ? new Bitmap(path) : null;
            }
            catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException)
            {
                image = null;
            }

            Loaded[name] = image;
        }

        return image;
    }

    // The rarity star; read when a card is bound, so it is up to date after a download.
    public static Bitmap? Star => Get("star");

    // {Binding Ship.RarityKey, Converter={x:Static controls:GameArt.Image}, ConverterParameter=card_} loads card_4.png.
    public static readonly IValueConverter Image =
        new FuncValueConverter<object?, string?, Bitmap?>((key, prefix) => key is null ? null : Get($"{prefix}{key}"));

    // True when the image exists, for showing a text stand-in otherwise.
    public static readonly IValueConverter Has =
        new FuncValueConverter<object?, string?, bool>((key, prefix) => key is not null && Get($"{prefix}{key}") is not null);
}
