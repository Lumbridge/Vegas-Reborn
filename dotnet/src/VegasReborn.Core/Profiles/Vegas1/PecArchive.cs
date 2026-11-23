using System.Buffers.Binary;

namespace VegasReborn.Core.Profiles.Vegas1;

/// <summary>
/// Rainbow Six Vegas (1) native PEC archive (<c>R6_PECPEC</c>) raw-XP adapter; leaves
/// unrelated bytes untouched. Serializer 0x10e66660, XP getter/setter 0x10e66320/0x10e66310,
/// checksum 0x10dedb60 and validator 0x10d026b0 establish this format. This does not
/// establish how every native rank/unlock consumer uses raw XP. Not valid for Vegas 2.
/// </summary>
public static class PecArchive
{
    public const uint Seed = 0x0645adef;
    public const uint Polynomial = 0x04c11db7;
    public const int XpOffset = 17;
    public const int MaxBytes = 1024 * 1024;
    private const int MinBytes = 21;

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint value = 0; value < 256; value++)
        {
            var crc = value << 24;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ Polynomial : crc << 1;
            }
            table[value] = crc;
        }
        return table;
    }

    /// <summary>MSB-first CRC-32 (poly 0x04c11db7) with the native seed.</summary>
    public static uint Checksum(ReadOnlySpan<byte> data, uint seed = Seed)
    {
        var crc = ~seed;
        foreach (var value in data)
        {
            crc = (crc << 8) ^ Table[value ^ (crc >> 24)];
        }
        return ~crc;
    }

    /// <summary>Validates the outer archive, raw version and checksum, then returns raw XP.</summary>
    /// <exception cref="InvalidDataException">The archive is not a supported, intact PEC.</exception>
    public static uint ReadXp(ReadOnlySpan<byte> data)
    {
        if (data.Length is < MinBytes or > MaxBytes)
        {
            throw new InvalidDataException("PEC length outside supported bounds");
        }
        var stored = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var outer = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        if (outer != 6 || size != (uint)(data.Length - 12))
        {
            throw new InvalidDataException("unsupported outer PEC archive");
        }
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[12..]) != 0x19)
        {
            throw new InvalidDataException("unsupported raw PEC version");
        }
        var actual = Checksum(data[4..]);
        if (actual == Seed || stored == Seed || actual != stored)
        {
            throw new InvalidDataException("invalid native PEC checksum");
        }
        return BinaryPrimitives.ReadUInt32LittleEndian(data[XpOffset..]);
    }

    public static bool TryReadXp(ReadOnlySpan<byte> data, out uint experience)
    {
        try
        {
            experience = ReadXp(data);
            return true;
        }
        catch (InvalidDataException)
        {
            experience = 0;
            return false;
        }
    }

    /// <summary>Returns a copy with only the raw XP and checksum changed.</summary>
    public static byte[] PatchXp(ReadOnlySpan<byte> data, uint experience)
    {
        ReadXp(data);
        var result = data.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(XpOffset), experience);
        BinaryPrimitives.WriteUInt32LittleEndian(result, Checksum(result.AsSpan(4)));
        ReadXp(result);
        return result;
    }

    /// <summary>
    /// True when two validated archives differ only in checksum and raw XP
    /// (progression changed, appearance did not).
    /// </summary>
    public static bool SameAppearance(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second) =>
        first.Length >= MinBytes && second.Length >= MinBytes
        && first[4..XpOffset].SequenceEqual(second[4..XpOffset])
        && first[(XpOffset + 4)..].SequenceEqual(second[(XpOffset + 4)..]);
}
