using System.Text;

namespace ShardRouting;

/// <summary>
/// MurmurHash3 (x86, 32-bit variant) - a fixed, well-known hash (database.md
/// §4), not a language built-in hash(), since those aren't guaranteed stable
/// across processes/versions.
/// </summary>
public static class MurmurHash3
{
    private const uint C1 = 0xcc9e2d51;
    private const uint C2 = 0x1b873593;

    public static uint Hash32(string input, uint seed = 0) =>
        Hash32(Encoding.UTF8.GetBytes(input), seed);

    public static uint Hash32(ReadOnlySpan<byte> data, uint seed)
    {
        var h1 = seed;
        var length = data.Length;
        var roundedEnd = length & ~3;

        for (var i = 0; i < roundedEnd; i += 4)
        {
            var k1 = (uint)(data[i] | (data[i + 1] << 8) | (data[i + 2] << 16) | (data[i + 3] << 24));
            k1 *= C1;
            k1 = RotateLeft(k1, 15);
            k1 *= C2;
            h1 ^= k1;
            h1 = RotateLeft(h1, 13);
            h1 = (h1 * 5) + 0xe6546b64;
        }

        uint k2 = 0;
        var remainder = length & 3;
        if (remainder == 3)
        {
            k2 ^= (uint)data[roundedEnd + 2] << 16;
        }

        if (remainder >= 2)
        {
            k2 ^= (uint)data[roundedEnd + 1] << 8;
        }

        if (remainder >= 1)
        {
            k2 ^= data[roundedEnd];
            k2 *= C1;
            k2 = RotateLeft(k2, 15);
            k2 *= C2;
            h1 ^= k2;
        }

        h1 ^= (uint)length;
        h1 = FMix(h1);
        return h1;
    }

    private static uint RotateLeft(uint x, int r) => (x << r) | (x >> (32 - r));

    private static uint FMix(uint h)
    {
        h ^= h >> 16;
        h *= 0x85ebca6b;
        h ^= h >> 13;
        h *= 0xc2b2ae35;
        h ^= h >> 16;
        return h;
    }
}
