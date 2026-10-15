using System.Buffers.Binary;
using VGAudio.Codecs.CriHca;
using VGAudio.Containers.Adx;
using VGAudio.Containers.Hca;
using VGAudio.Containers.Wave;

namespace AzlDl.Core.Audio;

// Sound stories (originsource/cipher/soundstory<n>_<lang>.cpk) are CRI CPK archives holding one USM movie: MPEG video
// and an ADX audio stream. This reads the audio out as a WAV file.
//
// USM streams are chunks: a 4-byte signature (@SFA for audio), a big-endian size, then a header whose byte 1 is the
// payload offset and bytes 2-3 the padding. Audio payloads past byte 0x140 are XORed with a 32-byte mask made from the
// movie key, which for this game is the same key its HCA voice banks use.
public static class SoundStoryAudio
{
    private const int MaskStart = 0x140;

    public static byte[] ReadWav(string cpkPath)
    {
        var data = File.ReadAllBytes(cpkPath);
        var (offset, length) = MovieRange(data);
        var audio = AudioStream(data.AsSpan(offset, length));
        if (audio.Length >= 2 && audio[0] == 0x80 && audio[1] == 0x00)
        {
            return new WaveWriter().GetFile(new AdxReader().Read(audio));
        }

        return new WaveWriter().GetFile(new HcaReader { EncryptionKey = new CriHcaKey(AcbFile.HcaKey) }.Read(audio));
    }

    // The archive's first file. The CPK header is a @UTF table at 0x10; its TOC table lists each file's offset, counted
    // from whichever comes first of the TOC and the content area.
    private static (int Offset, int Length) MovieRange(byte[] data)
    {
        if (!data.AsSpan().StartsWith("CPK "u8))
        {
            throw new InvalidDataException("Not a CRI CPK archive.");
        }

        var header = Table(data, 0);
        var tocOffset = header.Number(0, "TocOffset");
        var contentOffset = header.Number(0, "ContentOffset");
        var toc = Table(data, tocOffset);
        if (toc.RowCount == 0)
        {
            throw new InvalidDataException("The archive lists no files.");
        }

        var offset = Math.Min(tocOffset, contentOffset) + toc.Number(0, "FileOffset");
        var length = toc.Number(0, "FileSize");
        if (offset < 0 || offset + length > data.Length)
        {
            throw new InvalidDataException("The archive's file runs past its end.");
        }

        return ((int)offset, (int)length);
    }

    // A CPK section: 4-byte tag, 4 bytes, 8-byte length, then the @UTF table.
    private static CriUtfTable Table(byte[] data, long at)
    {
        var length = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan((int)at + 8));
        return CriUtfTable.Read(data.AsSpan((int)at + 16, (int)length).ToArray());
    }

    private static byte[] AudioStream(ReadOnlySpan<byte> movie)
    {
        var mask = Mask(AcbFile.HcaKey);
        using var audio = new MemoryStream();
        var at = 0;
        while (at + 32 <= movie.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(movie[(at + 4)..]);
            if (size <= 0 || at + 8 + size > movie.Length)
            {
                break;
            }

            // Data chunks (type 0) of the audio stream; headers, metadata and section ends are skipped.
            if (movie[at..].StartsWith("@SFA"u8) && (movie[at + 15] & 3) == 0)
            {
                var payloadOffset = movie[at + 9];
                var padding = BinaryPrimitives.ReadUInt16BigEndian(movie[(at + 10)..]);
                var payload = movie.Slice(at + 8 + payloadOffset, size - payloadOffset - padding).ToArray();
                for (var i = MaskStart; i < payload.Length; i++)
                {
                    payload[i] ^= mask[i & 0x1F];
                }

                audio.Write(payload);
            }

            at += 8 + size;
        }

        if (audio.Length == 0)
        {
            throw new InvalidDataException("The movie has no audio stream.");
        }

        return audio.ToArray();
    }

    // CRI's USM audio mask: odd bytes spell "URUC", even bytes are the inverted key table.
    private static byte[] Mask(ulong key)
    {
        var k = BitConverter.GetBytes(key);
        var t = new byte[0x20];
        t[0x00] = k[0];
        t[0x01] = k[1];
        t[0x02] = k[2];
        t[0x03] = (byte)(k[3] - 0x34);
        t[0x04] = (byte)(k[4] + 0xF9);
        t[0x05] = (byte)(k[5] ^ 0x13);
        t[0x06] = (byte)(k[6] + 0x61);
        t[0x07] = (byte)(t[0x00] ^ 0xFF);
        t[0x08] = (byte)(t[0x01] + t[0x02]);
        t[0x09] = (byte)(t[0x01] - t[0x07]);
        t[0x0A] = (byte)(t[0x02] ^ 0xFF);
        t[0x0B] = (byte)(t[0x01] ^ 0xFF);
        t[0x0C] = (byte)(t[0x0B] + t[0x09]);
        t[0x0D] = (byte)(t[0x08] - t[0x03]);
        t[0x0E] = (byte)(t[0x0D] ^ 0xFF);
        t[0x0F] = (byte)(t[0x0A] - t[0x0B]);
        t[0x10] = (byte)(t[0x08] - t[0x0F]);
        t[0x11] = (byte)(t[0x10] ^ t[0x07]);
        t[0x12] = (byte)(t[0x0F] ^ 0xFF);
        t[0x13] = (byte)(t[0x03] ^ 0x10);
        t[0x14] = (byte)(t[0x04] - 0x32);
        t[0x15] = (byte)(t[0x05] + 0xED);
        t[0x16] = (byte)(t[0x06] ^ 0xF3);
        t[0x17] = (byte)(t[0x13] - t[0x0F]);
        t[0x18] = (byte)(t[0x15] + t[0x07]);
        t[0x19] = (byte)(0x21 - t[0x13]);
        t[0x1A] = (byte)(t[0x14] ^ t[0x17]);
        t[0x1B] = (byte)(t[0x16] + t[0x16]);
        t[0x1C] = (byte)(t[0x17] + 0x44);
        t[0x1D] = (byte)(t[0x03] + t[0x04]);
        t[0x1E] = (byte)(t[0x05] - t[0x16]);
        t[0x1F] = (byte)(t[0x1D] ^ t[0x13]);

        var mask = new byte[0x20];
        for (var i = 0; i < mask.Length; i++)
        {
            mask[i] = (i & 1) == 1 ? "URUC"u8[(i >> 1) & 3] : (byte)~t[i];
        }

        return mask;
    }
}
