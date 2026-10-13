using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AzlDl.App.Controls;
using AzlDl.Core.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzlDl.App.ViewModels;

// A gallery card whose image is decoded in the background. ImagePath is null until the image is downloaded; the card
// then shows a "?".
public abstract partial class GalleryCard(string? imagePath) : ObservableObject
{
    public string? ImagePath { get; private set; } = imagePath;

    public bool HasImage => ImagePath is not null;

    public Bitmap? Image => ImagePath is { } path ? CardImages.Get(path, this) : null;

    internal void SetImagePath(string? path)
    {
        ImagePath = path;
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(Image));
    }

    internal void ImageLoaded() => OnPropertyChanged(nameof(Image));
}

public sealed class ShipCard(ShipInfo ship, string? imagePath) : GalleryCard(imagePath)
{
    public ShipInfo Ship { get; } = ship;
}

public sealed partial class EquipmentCard(EquipmentInfo equipment, string? imagePath) : GalleryCard(imagePath)
{
    public EquipmentInfo Equipment { get; } = equipment;

    // The equipment shown in the detail pane.
    [ObservableProperty]
    private bool isSelected;
}

// Galleries are laid out as rows of cards so the list can virtualize; the row length follows the window width.
public sealed record ShipCardRow(IReadOnlyList<ShipCard> Cards);

public sealed record EquipmentCardRow(IReadOnlyList<EquipmentCard> Cards);

// A filter value. Chips in one group combine with OR, groups with AND.
public sealed partial class FilterChip(string label, string code, Func<object, bool> matches, RarityTier? tier = null, string icon = "")
    : ObservableObject
{
    public static FilterChip For<T>(string label, string code, Func<T, bool> matches, RarityTier? tier = null, string icon = "") =>
        new(label, code, item => item is T value && matches(value), tier, icon);

    // The game's badge or emblem for this value (hull_3, rarity_4, nation_1) when its art is downloaded.
    public string Icon { get; } = icon;

    // Rarity letters and tier badges say what the code and the colour swatch would, so they replace both once downloaded.
    private bool ShowsLetterBadge => (Icon.StartsWith("rarity_", StringComparison.Ordinal) || Icon.StartsWith("tech_", StringComparison.Ordinal))
                                     && GameArt.Get(Icon) is not null;

    // Faction emblems are white artwork, drawn in the text colour so they show on light and dark chips alike.
    public bool IconIsEmblem => Icon.StartsWith("nation_", StringComparison.Ordinal);

    public bool IconIsBadge => Icon.Length > 0 && !IconIsEmblem;

    public string Label { get; } = label;

    // A short code shown before the label (SSR, DD); empty when the label says it all.
    public string Code { get; } = code;

    public bool HasCode => Code.Length > 0 && !ShowsLetterBadge;

    // Rarity chips carry their tier so they show the card frame's colour.
    public RarityTier? Tier { get; } = tier;

    // The swatch stands in for the game's art, so any downloaded icon replaces it.
    public bool HasTier => Tier is not null && (Icon.Length == 0 || GameArt.Get(Icon) is null);

    public Func<object, bool> Matches { get; } = matches;

    [ObservableProperty]
    private bool isChecked;
}

public sealed partial class FilterGroup : ObservableObject
{
    public FilterGroup(string title, IEnumerable<FilterChip> chips, Action changed)
    {
        Title = title;
        Chips = new ObservableCollection<FilterChip>(chips);
        foreach (var chip in Chips)
        {
            chip.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(FilterChip.IsChecked))
                {
                    OnPropertyChanged(nameof(ShowsAll));
                    changed();
                }
            };
        }
    }

    public string Title { get; }

    public ObservableCollection<FilterChip> Chips { get; }

    // "All" reads as pressed while no chip in the group is.
    public bool ShowsAll => Chips.All(c => !c.IsChecked);

    public bool Matches(object item) => ShowsAll || Chips.Any(c => c.IsChecked && c.Matches(item));

    [RelayCommand]
    public void Clear()
    {
        foreach (var chip in Chips)
        {
            chip.IsChecked = false;
        }

        OnPropertyChanged(nameof(ShowsAll));
    }
}

public record SortOption(string Title);

public sealed record SortOption<T>(string Title, Func<IEnumerable<T>, IEnumerable<T>> Sort) : SortOption(Title);

// Decoded card images, shared by every card and kept small: images are decoded at card size on a worker thread, and
// only the most recently used ones are kept, so scrolling the whole roster doesn't hold every image in memory.
internal static class CardImages
{
    private const int DecodeWidth = 180;
    private const int Capacity = 360;

    private static readonly Dictionary<string, LinkedListNode<(string Path, Bitmap Image)>> Loaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<(string Path, Bitmap Image)> Recent = new();
    private static readonly Dictionary<string, List<GalleryCard>> Pending = new(StringComparer.OrdinalIgnoreCase);

    // Called on the UI thread. Returns the image when it is decoded; otherwise starts decoding it and tells the card
    // once it is ready.
    public static Bitmap? Get(string path, GalleryCard card)
    {
        if (Loaded.TryGetValue(path, out var node))
        {
            Recent.Remove(node);
            Recent.AddFirst(node);
            return node.Value.Image;
        }

        if (Pending.TryGetValue(path, out var waiting))
        {
            if (!waiting.Contains(card))
            {
                waiting.Add(card);
            }

            return null;
        }

        Pending[path] = [card];
        _ = Task.Run(() =>
        {
            Bitmap? image = null;
            try
            {
                using var stream = File.OpenRead(path);
                image = Bitmap.DecodeToWidth(stream, DecodeWidth, BitmapInterpolationMode.HighQuality);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                // A file that doesn't decode leaves the card on its "?".
            }

            Dispatcher.UIThread.Post(() => Finish(path, image));
        });
        return null;
    }

    private static void Finish(string path, Bitmap? image)
    {
        var cards = Pending.Remove(path, out var waiting) ? waiting : [];
        if (image is null)
        {
            return;
        }

        Loaded[path] = Recent.AddFirst((path, image));
        while (Recent.Count > Capacity)
        {
            // Evicted images may still be on screen for a moment, so they are left to the garbage collector.
            Loaded.Remove(Recent.Last!.Value.Path);
            Recent.RemoveLast();
        }

        foreach (var card in cards)
        {
            card.ImageLoaded();
        }
    }

    // Forgets a path so a newly downloaded file is read again.
    public static void Forget(string path)
    {
        if (Loaded.Remove(path, out var node))
        {
            Recent.Remove(node);
        }
    }
}
