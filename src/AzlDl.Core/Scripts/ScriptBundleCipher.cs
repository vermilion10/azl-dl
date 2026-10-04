using System.Buffers.Binary;

namespace AzlDl.Core.Scripts;

// Decrypts the scripts32/scripts64 bundles, which ship fully encrypted unlike every other bundle.
// The file ends with a 4-byte big-endian length of a trailer that sits just before it; the trailer is not part of
// the bundle. The rest is processed in 8-byte blocks of two little-endian words, cycling through four transforms by
// block index: XXTEA, TEA and an XTEA variant (two rounds each, with the round sum counting up from SumStart), and an
// XOR with the block offset. The last 8 bytes or fewer are stored unencrypted.
public static class ScriptBundleCipher
{
    private const uint Delta = 0xF90042FB;
    private const uint SumStart = 0x0DFF7A0A;
    private const uint XteaSecondSumStart = 0x06FFBD05;
    private const int Rounds = 2;

    // The game's 19-word key table (a static array initializer in the client).
    private static readonly uint[] Key =
    [
        0x1D774BBC, 0x0096C0E8, 0x0069A28C, 0x35DACBB9, 0x0063CEC2, 0x005A6BA5, 0x1492712E, 0x0092DE19,
        0x000B6293, 0x03DF5646, 0x005063C2, 0x007293D5, 0x005E5BA3, 0x30C3AFCB, 0x011CA676, 0x00784EAD,
        0x004BB902, 0x0049F48C, 0x05813954,
    ];

    private static readonly byte[] UnityFsSignature = "UnityFS"u8.ToArray();

    // True for a file that isn't a plain bundle but decrypts to one.
    public static bool LooksEncrypted(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16 || data.StartsWith(UnityFsSignature))
        {
            return false;
        }

        Span<byte> head = stackalloc byte[8];
        data[..8].CopyTo(head);
        DecryptBlock(0, head, 0);
        return head[..7].SequenceEqual(UnityFsSignature);
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            throw new InvalidDataException("Too short to be an encrypted script bundle.");
        }

        var trailer = BinaryPrimitives.ReadUInt32BigEndian(data[^4..]);
        var length = data.Length - 4 - (long)trailer;
        if (length < 0)
        {
            throw new InvalidDataException("Trailer length is larger than the file.");
        }

        var output = data[..(int)length].ToArray();
        if (length == 8)
        {
            return output;
        }

        var position = 0;
        var mode = 0;
        do
        {
            DecryptBlock(mode, output.AsSpan(position, 8), position);
            position += 8;
            mode = (mode + 1) & 3;
        }
        while (position < length - 8);

        return output;
    }

    private static void DecryptBlock(int mode, Span<byte> block, int position)
    {
        var v0 = BinaryPrimitives.ReadUInt32LittleEndian(block);
        var v1 = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);

        switch (mode)
        {
            case 0:
                Xxtea(ref v0, ref v1);
                break;
            case 1:
                Tea(ref v0, ref v1);
                break;
            case 2:
                v0 ^= (uint)position ^ Key[13];
                v1 ^= (uint)position ^ Key[2];
                break;
            default:
                Xtea(ref v0, ref v1);
                break;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(block, v0);
        BinaryPrimitives.WriteUInt32LittleEndian(block[4..], v1);
    }

    private static void Xxtea(ref uint v0, ref uint v1)
    {
        var sum = SumStart;
        for (var round = 0; round < Rounds; round++)
        {
            var e = (int)(sum >> 2) & 3;
            v1 -= Mix(v0, v0, sum, Key[1 ^ e]);
            v0 -= Mix(v1, v1, sum, Key[e]);
            sum += Delta;
        }
    }

    private static uint Mix(uint z, uint y, uint sum, uint key) =>
        (((z >> 5) ^ (y << 2)) + ((z << 4) ^ (y >> 3))) ^ ((sum ^ y) + (key ^ z));

    private static void Tea(ref uint v0, ref uint v1)
    {
        var sum = SumStart;
        for (var round = 0; round < Rounds; round++)
        {
            v1 -= ((v0 << 4) + Key[2]) ^ (v0 + sum) ^ ((v0 >> 5) + Key[3]);
            v0 -= ((v1 << 4) + Key[0]) ^ (v1 + sum) ^ ((v1 >> 5) + Key[1]);
            sum += Delta;
        }
    }

    private static void Xtea(ref uint v0, ref uint v1)
    {
        uint first = SumStart, second = XteaSecondSumStart;
        for (var round = 0; round < Rounds; round++)
        {
            v1 -= (first + Key[(first >> 11) & 3]) ^ (((v0 << 4) ^ (v0 >> 5)) + v0);
            v0 -= (second + Key[second & 3]) ^ (((v1 << 4) ^ (v1 >> 5)) + v1);
            first += Delta;
            second += Delta;
        }
    }
}
