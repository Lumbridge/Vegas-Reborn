using System.Buffers.Binary;

namespace VegasReborn.Core.Profiles.Vegas2;

/// <summary>
/// Rainbow Six Vegas 2 character save (<c>PROFILE3.DAT</c> in the redirected
/// <c>My Games/Ubisoft/R6Vegas2</c> folder). The game builds its save names from
/// <c>PROFILE%d.DAT</c> (RainbowSixVegas2_SADS.exe). Slots 1 and 2 hold settings; slot 3 holds
/// the character, with its appearance colours at the end. Every slot starts with the u32
/// 0x34; the character slot follows with u32 0, the payload length (file length - 12) and
/// raw version 0x1a, then one flag byte and the raw XP as a u32 at offset 17, the same raw
/// layout as the Vegas 1 PEC after its checksum. Grounded in a retail save before (XP 0)
/// and after online matches (XP 30, then 40, each matching the in-game CURRENT XP). The Vegas 2
/// save carries no checksum in this header.
/// The three A.C.E.S. point totals follow XP as u32 in EACESTYPE order (Marksman, CQB,
/// Assault), at <see cref="AcesOffset"/>; the first match raised them from 0 to 3, 6 and 0.
/// XP and A.C.E.S. points are the progression block; the rest is the character.
/// </summary>
public static class PecSave
{
    public const string FileName = "PROFILE3.DAT";
    public const uint Magic = 0x34;
    public const uint RawVersion = 0x1a;
    public const int XpOffset = 17;
    public const int AcesOffset = 21;
    public const int MaxBytes = 64 * 1024;
    private const int MinBytes = 21;

    /// <summary>Validates the slot header and raw version, then returns raw XP.</summary>
    /// <exception cref="InvalidDataException">The file is not a supported Vegas 2 character save.</exception>
    public static uint ReadXp(ReadOnlySpan<byte> data)
    {
        if (data.Length is < MinBytes or > MaxBytes)
        {
            throw new InvalidDataException("Vegas 2 save length outside supported bounds");
        }
        if (BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic
            || BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) != (uint)(data.Length - 12))
        {
            throw new InvalidDataException("unsupported Vegas 2 save header");
        }
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[12..]) != RawVersion)
        {
            throw new InvalidDataException("unsupported Vegas 2 character version");
        }
        return BinaryPrimitives.ReadUInt32LittleEndian(data[XpOffset..]);
    }

    /// <summary>Validates the save as <see cref="ReadXp"/> does, then returns the Marksman, CQB and Assault points.</summary>
    public static uint[] ReadAces(ReadOnlySpan<byte> data)
    {
        ReadXp(data);
        if (data.Length < AcesOffset + 12)
        {
            throw new InvalidDataException("Vegas 2 save too short for A.C.E.S. points");
        }
        return
        [
            BinaryPrimitives.ReadUInt32LittleEndian(data[AcesOffset..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[(AcesOffset + 4)..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[(AcesOffset + 8)..]),
        ];
    }

    /// <summary>
    /// Returns a copy of <paramref name="data"/> with raw XP set to <paramref name="experience"/>
    /// and each A.C.E.S. total raised to the highest found in <paramref name="others"/>. Points
    /// never go down. Invalid or short other saves are ignored.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a supported Vegas 2 character save.</exception>
    public static byte[] MergeProgression(ReadOnlySpan<byte> data, uint experience, params byte[]?[] others)
    {
        ReadXp(data);
        var result = data.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(XpOffset), experience);
        if (result.Length >= AcesOffset + 12)
        {
            foreach (var other in others)
            {
                if (other is null || other.Length < AcesOffset + 12 || !TryReadXp(other, out _))
                {
                    continue;
                }
                for (var offset = AcesOffset; offset < AcesOffset + 12; offset += 4)
                {
                    var mine = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(offset));
                    var theirs = BinaryPrimitives.ReadUInt32LittleEndian(other.AsSpan(offset));
                    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset), Math.Max(mine, theirs));
                }
            }
        }
        return result;
    }

    /// <summary>Returns a copy with only the raw XP changed.</summary>
    /// <exception cref="InvalidDataException">The file is not a supported Vegas 2 character save.</exception>
    public static byte[] PatchXp(ReadOnlySpan<byte> data, uint experience) => MergeProgression(data, experience);

    /// <summary>
    /// True when two saves differ only in the progression block (XP and A.C.E.S. points):
    /// the player earned points, the character did not change.
    /// </summary>
    public static bool SameAppearance(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        if (first.Length != second.Length || first.Length < MinBytes)
        {
            return false;
        }
        var end = Math.Min(first.Length, AcesOffset + 12);
        return first[..XpOffset].SequenceEqual(second[..XpOffset]) && first[end..].SequenceEqual(second[end..]);
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
}
