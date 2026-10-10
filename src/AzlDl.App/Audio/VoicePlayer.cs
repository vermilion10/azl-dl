using System.Runtime.InteropServices;

namespace AzlDl.App.Audio;

// Plays one WAV at a time with the Windows sound API, which needs no extra libraries. Starting a line stops the one
// before. Elsewhere IsSupported is false and the app hides its play buttons.
public static class VoicePlayer
{
    private const uint Memory = 0x0004;
    private const uint Async = 0x0001;
    private const uint NoDefault = 0x0002;

    // The sound API reads the WAV from memory while it plays, so the buffer stays pinned until the next call.
    private static GCHandle playing;

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static void Play(byte[] wav)
    {
        if (!IsSupported)
        {
            return;
        }

        Stop();
        playing = GCHandle.Alloc(wav, GCHandleType.Pinned);
        PlaySound(playing.AddrOfPinnedObject(), IntPtr.Zero, Memory | Async | NoDefault);
    }

    public static void Stop()
    {
        if (!IsSupported)
        {
            return;
        }

        PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
        if (playing.IsAllocated)
        {
            playing.Free();
        }
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
}
