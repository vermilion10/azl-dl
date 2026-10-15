using System.Runtime.InteropServices;

namespace AzlDl.App.Audio;

// Plays a WAV through the Windows waveOut API, with pause, seeking and a position to show; music and sound stories
// need those, which the one-shot sound API behind VoicePlayer doesn't offer. One recording plays at a time, and
// starting one stops voice lines. Elsewhere IsSupported is false and the app hides its play buttons.
public sealed class WavePlayer : IDisposable
{
    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint TimeBytes = 0x0004;
    private const uint HeaderDone = 0x0001;

    private byte[] pcm = [];
    private WaveFormat format;
    private IntPtr device;
    private IntPtr header;
    private GCHandle pinned;
    private long startByte;

    public static WavePlayer Shared { get; } = new();

    public static bool IsSupported => OperatingSystem.IsWindows();

    public bool IsLoaded => pcm.Length > 0;

    public bool IsPaused { get; private set; }

    public TimeSpan Length => format.BytesPerSecond > 0 ? TimeSpan.FromSeconds((double)pcm.Length / format.BytesPerSecond) : TimeSpan.Zero;

    public TimeSpan Position
    {
        get
        {
            if (format.BytesPerSecond == 0)
            {
                return TimeSpan.Zero;
            }

            var played = startByte;
            if (device != IntPtr.Zero)
            {
                var time = new MmTime { Type = TimeBytes };
                if (waveOutGetPosition(device, ref time, (uint)Marshal.SizeOf<MmTime>()) == 0)
                {
                    played += time.Bytes;
                }
            }

            return TimeSpan.FromSeconds(Math.Min(played, pcm.Length) / (double)format.BytesPerSecond);
        }
    }

    // True once the recording has played to its end.
    public bool IsFinished => header != IntPtr.Zero && (Marshal.PtrToStructure<WaveHeader>(header).Flags & HeaderDone) != 0;

    public bool IsPlaying => device != IntPtr.Zero && !IsPaused && !IsFinished;

    // Takes a 16-bit PCM WAV (as VGAudio writes them) and keeps its samples for playing.
    public void Load(byte[] wav)
    {
        Stop();
        (format, pcm) = Parse(wav);
    }

    public void Play(TimeSpan from)
    {
        if (!IsSupported || pcm.Length == 0)
        {
            return;
        }

        VoicePlayer.Stop();
        Close();
        startByte = Math.Clamp((long)(from.TotalSeconds * format.BytesPerSecond), 0, pcm.Length);
        startByte -= startByte % format.BlockAlign;
        if (startByte >= pcm.Length)
        {
            return;
        }

        var waveFormat = format.ToNative();
        if (waveOutOpen(out device, WaveMapper, ref waveFormat, IntPtr.Zero, IntPtr.Zero, 0) != 0)
        {
            device = IntPtr.Zero;
            return;
        }

        pinned = GCHandle.Alloc(pcm, GCHandleType.Pinned);
        header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
        Marshal.StructureToPtr(new WaveHeader
        {
            Data = pinned.AddrOfPinnedObject() + (nint)startByte,
            BufferLength = (uint)(pcm.Length - startByte),
        }, header, false);
        waveOutPrepareHeader(device, header, (uint)Marshal.SizeOf<WaveHeader>());
        waveOutWrite(device, header, (uint)Marshal.SizeOf<WaveHeader>());
        IsPaused = false;
    }

    public void Pause()
    {
        if (device != IntPtr.Zero && !IsPaused)
        {
            waveOutPause(device);
            IsPaused = true;
        }
    }

    public void Resume()
    {
        if (device != IntPtr.Zero && IsPaused)
        {
            waveOutRestart(device);
            IsPaused = false;
        }
    }

    public void Stop()
    {
        Close();
        startByte = 0;
        IsPaused = false;
    }

    public void Dispose() => Stop();

    private void Close()
    {
        if (device != IntPtr.Zero)
        {
            waveOutReset(device);
            if (header != IntPtr.Zero)
            {
                waveOutUnprepareHeader(device, header, (uint)Marshal.SizeOf<WaveHeader>());
            }

            waveOutClose(device);
            device = IntPtr.Zero;
        }

        if (header != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(header);
            header = IntPtr.Zero;
        }

        if (pinned.IsAllocated)
        {
            pinned.Free();
        }
    }

    // RIFF chunks: "fmt " gives the format, "data" the samples.
    private static (WaveFormat Format, byte[] Pcm) Parse(byte[] wav)
    {
        if (wav.Length < 12 || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Not a WAV file.");
        }

        WaveFormat? format = null;
        var at = 12;
        while (at + 8 <= wav.Length)
        {
            var size = BitConverter.ToInt32(wav, at + 4);
            var body = at + 8;
            if (wav.AsSpan(at, 4).SequenceEqual("fmt "u8))
            {
                format = new WaveFormat(BitConverter.ToInt16(wav, body + 2), BitConverter.ToInt32(wav, body + 4), BitConverter.ToInt16(wav, body + 14));
            }
            else if (wav.AsSpan(at, 4).SequenceEqual("data"u8) && format is { } found)
            {
                return (found, wav.AsSpan(body, Math.Min(size, wav.Length - body)).ToArray());
            }

            at = body + size + (size & 1);
        }

        throw new InvalidDataException("The WAV file has no samples.");
    }

    private readonly record struct WaveFormat(short Channels, int SampleRate, short BitsPerSample)
    {
        public short BlockAlign => (short)(Channels * BitsPerSample / 8);

        public int BytesPerSecond => SampleRate * BlockAlign;

        public NativeFormat ToNative() => new()
        {
            FormatTag = 1,
            Channels = Channels,
            SamplesPerSec = SampleRate,
            AvgBytesPerSec = BytesPerSecond,
            BlockAlign = BlockAlign,
            BitsPerSample = BitsPerSample,
        };
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct NativeFormat
    {
        public short FormatTag;
        public short Channels;
        public int SamplesPerSec;
        public int AvgBytesPerSec;
        public short BlockAlign;
        public short BitsPerSample;
        public short Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    private struct MmTime
    {
        [FieldOffset(0)]
        public uint Type;

        [FieldOffset(4)]
        public uint Bytes;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr device, uint deviceId, ref NativeFormat format, IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr device, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr device, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr device, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    private static extern int waveOutPause(IntPtr device);

    [DllImport("winmm.dll")]
    private static extern int waveOutRestart(IntPtr device);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr device);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr device);

    [DllImport("winmm.dll")]
    private static extern int waveOutGetPosition(IntPtr device, ref MmTime time, uint size);
}
