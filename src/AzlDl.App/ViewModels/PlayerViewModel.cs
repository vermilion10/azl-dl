using AzlDl.App.Audio;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzlDl.App.ViewModels;

// The now-playing bar shared by Music and ASMR: what is loaded, play and pause, and a position to drag.
public sealed partial class PlayerViewModel : ObservableObject
{
    private readonly WavePlayer player = WavePlayer.Shared;
    private readonly DispatcherTimer timer;
    private bool updating;

    public PlayerViewModel()
    {
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Tick());
    }

    public bool IsSupported => WavePlayer.IsSupported;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrack))]
    private string title = "";

    [ObservableProperty]
    private string subtitle = "";

    public bool HasTrack => Title.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseTitle))]
    private bool isPlaying;

    public string PlayPauseTitle => IsPlaying ? "Pause" : "Play";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LengthText))]
    private double lengthSeconds;

    // Bound two ways to the position slider: the timer moves it, dragging it seeks.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    private double positionSeconds;

    public string PositionText => Clock(PositionSeconds);

    public string LengthText => Clock(LengthSeconds);

    // Where playback is now; Music and ASMR follow it (the ASMR page highlights the subtitle being spoken).
    public event Action<TimeSpan>? PositionChanged;

    // Who loaded what is playing, so a page can tell its own recording from another page's.
    public object? Owner { get; private set; }

    public void Start(byte[] wav, string trackTitle, string trackSubtitle, object owner, TimeSpan from = default)
    {
        player.Load(wav);
        Owner = owner;
        Title = trackTitle;
        Subtitle = trackSubtitle;
        LengthSeconds = player.Length.TotalSeconds;
        player.Play(from);
        timer.Start();
        Tick();
    }

    public void Seek(TimeSpan to)
    {
        if (!player.IsLoaded)
        {
            return;
        }

        var paused = player.IsPaused;
        player.Play(to);
        if (paused)
        {
            player.Pause();
        }

        timer.Start();
        Tick();
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (!player.IsLoaded)
        {
            return;
        }

        if (player.IsPlaying)
        {
            player.Pause();
        }
        else if (player.IsPaused)
        {
            player.Resume();
        }
        else
        {
            // Finished or stopped: start over.
            player.Play(PositionSeconds >= LengthSeconds - 0.5 ? TimeSpan.Zero : TimeSpan.FromSeconds(PositionSeconds));
            timer.Start();
        }

        Tick();
    }

    [RelayCommand]
    private void Stop()
    {
        player.Stop();
        Tick();
    }

    partial void OnPositionSecondsChanged(double value)
    {
        if (!updating && Math.Abs(value - player.Position.TotalSeconds) > 1)
        {
            Seek(TimeSpan.FromSeconds(value));
        }
    }

    private void Tick()
    {
        updating = true;
        PositionSeconds = player.Position.TotalSeconds;
        updating = false;
        IsPlaying = player.IsPlaying;
        PositionChanged?.Invoke(player.Position);
        if (!player.IsPlaying && !player.IsPaused)
        {
            timer.Stop();
        }
    }

    private static string Clock(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", System.Globalization.CultureInfo.InvariantCulture);
}
