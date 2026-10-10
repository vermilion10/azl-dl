using System.Buffers.Binary;
using VGAudio.Codecs.CriHca;
using VGAudio.Containers.Hca;
using VGAudio.Containers.Wave;

namespace AzlDl.Core.Audio;

// A CRI ACB sound bank with its waveforms embedded (the cue/cv-*.b files). A cue name resolves to its waveforms
// through Cue -> Sequence -> Track -> track event "note on" commands -> Synth -> Waveform, whose MemoryAwbId is the
// entry in the embedded AFS2 archive. Waveforms are HCA, encrypted with the game's key.
public sealed class AcbFile
{
    // Azur Lane's HCA key, as listed in vgmstream's hca_keys.h.
    public const ulong HcaKey = 0x0002354E95356C72;

    private const int ReferenceWaveform = 1;
    private const int ReferenceSynth = 2;
    private const int ReferenceSequence = 3;
    private const int NoteOn = 2000;
    private const int NoteOnWithNo = 2003;

    private readonly Dictionary<string, IReadOnlyList<int>> cues = new(StringComparer.Ordinal);
    private readonly byte[] archive;
    private readonly Dictionary<int, (int Start, int End)> entries = [];
    private readonly ushort subKey;

    private AcbFile(CriUtfTable header)
    {
        var names = Table(header, "CueNameTable");
        var cueTable = Table(header, "CueTable");
        var sequences = Table(header, "SequenceTable");
        var tracks = Table(header, "TrackTable");
        var events = Table(header, "TrackEventTable");
        var synths = Table(header, "SynthTable");
        var waveforms = Table(header, "WaveformTable");

        IEnumerable<int> Waves(long type, long index, int depth)
        {
            if (depth > 8 || index < 0)
            {
                yield break;
            }

            switch (type)
            {
                case ReferenceWaveform when index < waveforms.RowCount:
                    yield return (int)waveforms.Number((int)index, "MemoryAwbId");
                    break;
                case ReferenceSynth when index < synths.RowCount:
                    var items = synths.Data((int)index, "ReferenceItems");
                    for (var i = 0; i + 4 <= items.Length; i += 4)
                    {
                        foreach (var wave in Waves(BinaryPrimitives.ReadUInt16BigEndian(items.AsSpan(i)),
                                     BinaryPrimitives.ReadUInt16BigEndian(items.AsSpan(i + 2)), depth + 1))
                        {
                            yield return wave;
                        }
                    }

                    break;
                case ReferenceSequence when index < sequences.RowCount:
                    var trackIndexes = sequences.Data((int)index, "TrackIndex");
                    for (var i = 0; i + 2 <= trackIndexes.Length; i += 2)
                    {
                        var track = BinaryPrimitives.ReadUInt16BigEndian(trackIndexes.AsSpan(i));
                        if (track >= tracks.RowCount)
                        {
                            continue;
                        }

                        var eventIndex = (int)tracks.Number(track, "EventIndex");
                        if (eventIndex >= events.RowCount)
                        {
                            continue;
                        }

                        foreach (var (target, targetIndex) in NoteOns(events.Data(eventIndex, "Command")))
                        {
                            foreach (var wave in Waves(target, targetIndex, depth + 1))
                            {
                                yield return wave;
                            }
                        }
                    }

                    break;
            }
        }

        for (var row = 0; row < names.RowCount; row++)
        {
            var cue = (int)names.Number(row, "CueIndex");
            if (cue < cueTable.RowCount)
            {
                cues[names.Text(row, "CueName")] = Waves(cueTable.Number(cue, "ReferenceType"), cueTable.Number(cue, "ReferenceIndex"), 0)
                    .Distinct().ToList();
            }
        }

        archive = header.Data(0, "AwbFile");
        subKey = ReadArchive(archive, entries);
    }

    public IReadOnlyCollection<string> CueNames => cues.Keys;

    public static AcbFile Read(string path) => Read(File.ReadAllBytes(path));

    public static AcbFile Read(byte[] data) => new(CriUtfTable.Read(data));

    public bool HasCue(string name) => cues.TryGetValue(name, out var waves) && waves.Any(entries.ContainsKey);

    // The cue's first waveform as a WAV file, or null when the bank has no such cue.
    public byte[]? ReadWav(string cueName)
    {
        if (!cues.TryGetValue(cueName, out var waves) || !waves.Any(entries.ContainsKey))
        {
            return null;
        }

        var (start, end) = entries[waves.First(entries.ContainsKey)];
        var key = new CriHcaKey(subKey == 0 ? HcaKey : HcaKey * (((ulong)subKey << 16) | (ushort)(~subKey + 2)));
        var audio = new HcaReader { EncryptionKey = key }.Read(archive.AsSpan(start, end - start).ToArray());
        return new WaveWriter().GetFile(audio);
    }

    // "note on" commands in a track event's command list: 16-bit code, 8-bit size, then (for these) target type and index.
    private static IEnumerable<(int Type, int Index)> NoteOns(byte[] commands)
    {
        var at = 0;
        while (at + 3 <= commands.Length)
        {
            var code = BinaryPrimitives.ReadUInt16BigEndian(commands.AsSpan(at));
            var size = commands[at + 2];
            if (code is NoteOn or NoteOnWithNo && size >= 4 && at + 3 + 4 <= commands.Length)
            {
                yield return (BinaryPrimitives.ReadUInt16BigEndian(commands.AsSpan(at + 3)), BinaryPrimitives.ReadUInt16BigEndian(commands.AsSpan(at + 5)));
            }

            at += 3 + size;
        }
    }

    // AFS2: magic, version, offset width, id width, file count, alignment (low 16 bits) and HCA sub key (high 16 bits),
    // then the ids and count + 1 offsets; each file starts at its offset rounded up to the alignment.
    private static ushort ReadArchive(byte[] data, Dictionary<int, (int Start, int End)> entries)
    {
        if (data.Length < 16 || !data.AsSpan(0, 4).SequenceEqual("AFS2"u8))
        {
            return 0;
        }

        var offsetWidth = data[5];
        var idWidth = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
        var alignment = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12));
        var key = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14));
        var idsAt = 16;
        var offsetsAt = idsAt + (count * idWidth);

        long Offset(int i) => offsetWidth == 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offsetsAt + (i * 2)))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offsetsAt + (i * 4)));

        for (var i = 0; i < count; i++)
        {
            var id = idWidth == 4 ? (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(idsAt + (i * 4))) : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(idsAt + (i * 2)));
            var start = Offset(i);
            if (alignment > 1)
            {
                start = (start + alignment - 1) / alignment * alignment;
            }

            entries[id] = ((int)start, (int)Offset(i + 1));
        }

        return key;
    }

    private static CriUtfTable Table(CriUtfTable header, string name)
    {
        var data = header.Data(0, name);
        return CriUtfTable.IsTable(data) ? CriUtfTable.Read(data) : CriUtfTable.Empty;
    }
}
