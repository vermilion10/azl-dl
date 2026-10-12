using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using AzlDl.App.Settings;
using AzlDl.Core;
using AzlDl.Core.Art;
using AzlDl.Core.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzlDl.App.ViewModels;

// A round chibi-head avatar. Until its image is downloaded it shows the name's first letter.
public sealed class Avatar(SocialAuthor author, string? imagePath) : GalleryCard(imagePath)
{
    public SocialAuthor Author { get; } = author;

    public string Initial => Author.Name.Length > 0 ? char.ToUpperInvariant(Author.Name[0]).ToString() : "?";
}

// Avatars shared by every post and message, downloaded in batches as conversations open.
public sealed class AvatarStore(Func<AssetFetcher> fetcher)
{
    private readonly Dictionary<string, Avatar> avatars = new(StringComparer.Ordinal);

    public Avatar For(SocialAuthor author)
    {
        var key = author.Avatar + "|" + author.ShipGroup;
        if (!avatars.TryGetValue(key, out var avatar))
        {
            var path = author.Avatar.Length > 0 ? fetcher().AvatarPath(author.Avatar) : null;
            avatars[key] = avatar = new Avatar(author, path is not null && File.Exists(path) ? path : null);
        }

        return avatar;
    }

    // Downloads the avatars of these authors that aren't on disk. Avatars are optional, so failures are ignored.
    public async Task FetchAsync(IEnumerable<SocialAuthor> authors)
    {
        var missing = authors.Select(For).Where(a => !a.HasImage && a.Author.Avatar.Length > 0).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var source = fetcher();
        try
        {
            await Task.Run(() => source.FetchImagesAsync(missing.Select(a => AssetFetcher.AvatarFolder + a.Author.Avatar).Distinct().ToList()));
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException
                                      or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            return;
        }

        foreach (var avatar in missing)
        {
            var path = source.AvatarPath(avatar.Author.Avatar);
            if (File.Exists(path))
            {
                avatar.SetImagePath(path);
            }
        }
    }
}

public sealed record CommentItem(Avatar Avatar, string Name, string Text, string When, IReadOnlyList<CommentItem> Replies)
{
    public bool HasReplies => Replies.Count > 0;
}

public sealed record PostItem(JuustagramPost Post, string ShipName)
{
    public string When => Post.Time == DateTime.MinValue ? "" : Post.Time.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    public string Preview => Post.Message.ReplaceLineEndings(" ");

    // The handle, with the ship's name when the handle doesn't say it.
    public string Title => ShipName.Length > 0 && !Post.Author.Name.Contains(ShipName, StringComparison.OrdinalIgnoreCase)
        ? $"{Post.Author.Name} · {ShipName}"
        : Post.Author.Name;
}

// Juustagram: the ships' posts, newest first, with their picture, comments and the replies the Commander can leave.
public sealed partial class JuustagramViewModel(AppSettings settings) : ObservableObject
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    private IReadOnlyList<PostItem> all = [];
    private AvatarStore? avatars;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsReady), nameof(HasProblem))]
    private LoadState state = LoadState.Loading;

    public bool IsLoading => State == LoadState.Loading;

    public bool IsReady => State == LoadState.Ready;

    public bool HasProblem => State is LoadState.MissingData or LoadState.Failed;

    [ObservableProperty]
    private string message = "";

    [ObservableProperty]
    private string filter = "";

    public ObservableCollection<PostItem> Posts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPost))]
    private PostItem? selectedPost;

    public bool HasPost => SelectedPost is not null;

    [ObservableProperty]
    private Avatar? postAvatar;

    [ObservableProperty]
    private Bitmap? picture;

    [ObservableProperty]
    private string pictureMessage = "";

    public ObservableCollection<CommentItem> Comments { get; } = [];

    public ObservableCollection<JuustagramOption> Options { get; } = [];

    // The reply the Commander picked, shown as their comment with the author's answer under it.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReply))]
    private CommentItem? reply;

    public bool CanReply => Reply is null && Options.Count > 0;

    public Action? OpenSync { get; set; }

    [RelayCommand]
    private void GoToSync() => OpenSync?.Invoke();

    public async Task LoadAsync()
    {
        State = LoadState.Loading;
        Message = "Reading Juustagram…";
        var folder = settings.DataFolder;
        if (SocialCatalog.MissingFiles(folder).Count > 0)
        {
            State = LoadState.MissingData;
            Message = $"There's no game data in {folder} yet. Update it from Sync, or pick a folder that already has it in Settings.";
            return;
        }

        try
        {
            var (social, names) = await Task.Run(() => (SocialCatalog.Load(folder), ShipNames(folder)));
            avatars = new AvatarStore(Fetcher);
            all = social.Posts.Select(p => new PostItem(p, names.GetValueOrDefault(p.Author.ShipGroup, ""))).ToList();
            ApplyFilter();
            SelectedPost ??= Posts.FirstOrDefault();
            State = LoadState.Ready;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or FormatException)
        {
            State = LoadState.Failed;
            Message = $"Couldn't read Juustagram in {folder}: {e.Message}";
        }
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        Posts.Clear();
        foreach (var post in all.Where(p => Filter.Length == 0
                                            || p.Title.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                                            || p.Post.Message.Contains(Filter, StringComparison.OrdinalIgnoreCase)))
        {
            Posts.Add(post);
        }
    }

    partial void OnSelectedPostChanged(PostItem? value) => _ = ShowPostAsync(value);

    private async Task ShowPostAsync(PostItem? item)
    {
        Comments.Clear();
        Options.Clear();
        Reply = null;
        Picture = null;
        PictureMessage = "";
        if (item is null || avatars is not { } store)
        {
            PostAvatar = null;
            return;
        }

        var post = item.Post;
        PostAvatar = store.For(post.Author);
        foreach (var comment in post.Comments)
        {
            Comments.Add(Item(comment, store));
        }

        foreach (var option in post.Options)
        {
            Options.Add(option);
        }

        OnPropertyChanged(nameof(CanReply));
        var authors = post.Comments.SelectMany(c => c.Replies.Prepend(c)).Select(c => c.Author).Prepend(post.Author);
        _ = store.FetchAsync(authors);

        if (post.PictureUrl.Length == 0)
        {
            return;
        }

        PictureMessage = "Loading the picture…";
        try
        {
            var path = await Fetcher().FetchPictureAsync(post.PictureUrl);
            if (SelectedPost != item)
            {
                return;
            }

            Picture = path is null ? null : await Task.Run(() => new Bitmap(path));
            PictureMessage = path is null ? "This post's picture isn't on the game's image host." : "";
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException or ArgumentException
                                      or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            if (SelectedPost == item)
            {
                PictureMessage = $"The picture didn't load: {e.Message}";
            }
        }
    }

    [RelayCommand]
    private void Choose(JuustagramOption? option)
    {
        if (option is null || SelectedPost is not { } item || avatars is not { } store)
        {
            return;
        }

        var commander = new SocialAuthor(0, "Commander", "");
        var answer = option.Answer.Length > 0
            ? new[] { new CommentItem(store.For(item.Post.Author), item.Post.Author.Name, option.Answer, "", []) }
            : [];
        Reply = new CommentItem(store.For(commander), commander.Name, option.Text, "", answer);
    }

    private static CommentItem Item(JuustagramComment comment, AvatarStore store) =>
        new(store.For(comment.Author), comment.Author.Name, comment.Text,
            comment.Time == DateTime.MinValue ? "" : comment.Time.ToString("HH:mm", CultureInfo.InvariantCulture),
            comment.Replies.Select(r => Item(r, store)).ToList());

    // Ship group -> ship name, so handles like "Ootori_Shikikan" can say who they are.
    internal static IReadOnlyDictionary<long, string> ShipNames(string folder) =>
        ShipCatalog.MissingFiles(folder).Count == 0
            ? ShipCatalog.Load(folder).Ships.GroupBy(s => s.GroupId).ToDictionary(g => g.Key, g => g.First().Name)
            : new Dictionary<long, string>();

    private AssetFetcher Fetcher() => new(Http, ClientRegion.En, settings.DataFolder);
}

public sealed record ContactItem(ChatContact Contact, Avatar Avatar, string Title, string Subtitle);

public enum MessageSide
{
    Them,
    Commander,
    Notice,
}

public sealed record MessageItem(Avatar Avatar, string Name, string Text, MessageSide Side, ChatLineKind Kind, bool ShowsName)
{
    public bool IsThem => Side == MessageSide.Them;

    public bool IsCommander => Side == MessageSide.Commander;

    public bool IsNotice => Side == MessageSide.Notice;

    // Stickers and red envelopes are described rather than drawn.
    public string Shown => Kind switch
    {
        ChatLineKind.Sticker => $"[Sticker: {Text}]",
        ChatLineKind.RedEnvelope => $"[Red envelope: {Text}]",
        _ => Text,
    };
}

// Fleet Chat: conversations with ships and group chats. A chat plays up to the Commander's next reply; picking one
// continues down that branch, as in the game.
public sealed partial class FleetChatViewModel(AppSettings settings) : ObservableObject
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    private IReadOnlyList<ContactItem> all = [];
    private AvatarStore? avatars;
    private readonly HashSet<int> picked = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsReady), nameof(HasProblem))]
    private LoadState state = LoadState.Loading;

    public bool IsLoading => State == LoadState.Loading;

    public bool IsReady => State == LoadState.Ready;

    public bool HasProblem => State is LoadState.MissingData or LoadState.Failed;

    [ObservableProperty]
    private string message = "";

    [ObservableProperty]
    private string filter = "";

    public ObservableCollection<ContactItem> Contacts { get; } = [];

    [ObservableProperty]
    private ContactItem? selectedContact;

    public ObservableCollection<ChatTopic> Topics { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTopic))]
    private ChatTopic? selectedTopic;

    public bool HasTopic => SelectedTopic is not null;

    public ObservableCollection<MessageItem> Messages { get; } = [];

    public ObservableCollection<ChatOption> Replies { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFinished))]
    private bool isWaiting;

    public bool IsFinished => HasTopic && !IsWaiting;

    public Action? OpenSync { get; set; }

    [RelayCommand]
    private void GoToSync() => OpenSync?.Invoke();

    public async Task LoadAsync()
    {
        State = LoadState.Loading;
        Message = "Reading Fleet Chat…";
        var folder = settings.DataFolder;
        if (SocialCatalog.MissingFiles(folder).Count > 0)
        {
            State = LoadState.MissingData;
            Message = $"There's no game data in {folder} yet. Update it from Sync, or pick a folder that already has it in Settings.";
            return;
        }

        try
        {
            var (social, names) = await Task.Run(() => (SocialCatalog.Load(folder), JuustagramViewModel.ShipNames(folder)));
            var store = avatars = new AvatarStore(Fetcher);
            all = social.Contacts.Select(c =>
            {
                var title = !c.IsGroup && names.TryGetValue(c.Who.ShipGroup, out var ship) ? ship : c.Who.Name;
                var topics = c.Topics.Count == 1 ? "1 chat" : $"{c.Topics.Count} chats";
                return new ContactItem(c, store.For(c.Who), title, title == c.Who.Name ? topics : $"{c.Who.Name} · {topics}");
            }).ToList();
            ApplyFilter();
            SelectedContact ??= Contacts.FirstOrDefault();
            State = LoadState.Ready;
            _ = store.FetchAsync(all.Select(c => c.Contact.Who));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or FormatException)
        {
            State = LoadState.Failed;
            Message = $"Couldn't read Fleet Chat in {folder}: {e.Message}";
        }
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        Contacts.Clear();
        foreach (var contact in all.Where(c => Filter.Length == 0
                                               || c.Title.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                                               || c.Contact.Who.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                                               || c.Contact.Topics.Any(t => t.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase))))
        {
            Contacts.Add(contact);
        }
    }

    partial void OnSelectedContactChanged(ContactItem? value)
    {
        Topics.Clear();
        foreach (var topic in value?.Contact.Topics ?? [])
        {
            Topics.Add(topic);
        }

        SelectedTopic = Topics.FirstOrDefault();
    }

    partial void OnSelectedTopicChanged(ChatTopic? value)
    {
        picked.Clear();
        Play();
        if (value is not null && avatars is { } store)
        {
            _ = store.FetchAsync(value.Lines.Select(l => l.Author).DistinctBy(a => a.ShipGroup));
        }
    }

    [RelayCommand]
    private void Answer(ChatOption? option)
    {
        if (option is null)
        {
            return;
        }

        picked.Add(option.Flag);
        Play();
    }

    [RelayCommand]
    private void Restart()
    {
        picked.Clear();
        Play();
    }

    private void Play()
    {
        Messages.Clear();
        Replies.Clear();
        if (SelectedTopic is not { } topic || avatars is not { } store)
        {
            IsWaiting = false;
            return;
        }

        var lines = SocialCatalog.Play(topic, picked, out var waiting);
        long? previous = null;
        foreach (var line in lines)
        {
            var side = line.Kind == ChatLineKind.Notice ? MessageSide.Notice
                : line.Author.IsCommander ? MessageSide.Commander
                : MessageSide.Them;
            // Consecutive messages from one ship show the name once, as messaging apps do.
            Messages.Add(new MessageItem(store.For(line.Author), line.Author.Name, line.Text, side, line.Kind,
                side == MessageSide.Them && previous != line.Author.ShipGroup));
            previous = side == MessageSide.Them ? line.Author.ShipGroup : null;
        }

        foreach (var option in waiting)
        {
            Replies.Add(option);
        }

        IsWaiting = waiting.Count > 0;
    }

    private AssetFetcher Fetcher() => new(Http, ClientRegion.En, settings.DataFolder);
}
