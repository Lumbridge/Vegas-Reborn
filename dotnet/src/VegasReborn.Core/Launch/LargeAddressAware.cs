namespace VegasReborn.Core.Launch;

/// <summary>
/// The PE header's IMAGE_FILE_LARGE_ADDRESS_AWARE flag on the launcher's own copy of the
/// executable. The flag only raises the 32-bit process's address space from 2 GB to 4 GB on
/// 64-bit Windows; code and data are unchanged, so the copy's identity is its hash with the
/// flag cleared. The installed game is never written.
/// </summary>
internal static class LargeAddressAware
{
    private const ushort Flag = 0x0020;

    /// <summary>File offset of the COFF Characteristics field, or -1 when this is not a PE image.</summary>
    internal static int CharacteristicsOffset(ReadOnlySpan<byte> image)
    {
        if (image.Length < 0x40 || image[0] != (byte)'M' || image[1] != (byte)'Z')
        {
            return -1;
        }
        var header = BitConverter.ToInt32(image[0x3c..]);
        if (header < 0x40 || header > image.Length - 24
            || image[header] != (byte)'P' || image[header + 1] != (byte)'E' || image[header + 2] != 0 || image[header + 3] != 0)
        {
            return -1;
        }
        return header + 22;
    }

    public static bool IsSet(ReadOnlySpan<byte> image)
    {
        var offset = CharacteristicsOffset(image);
        return offset >= 0 && (BitConverter.ToUInt16(image[offset..]) & Flag) != 0;
    }

    /// <summary>True when the image is the expected executable, with or without the flag.</summary>
    public static bool Matches(byte[] image, string expectedSha256)
    {
        if (PrivateFile.Sha256(image) == expectedSha256)
        {
            return true;
        }
        var offset = CharacteristicsOffset(image);
        if (offset < 0 || !IsSet(image))
        {
            return false;
        }
        var shipped = (byte[])image.Clone();
        Set(shipped, offset, false);
        return PrivateFile.Sha256(shipped) == expectedSha256;
    }

    /// <summary>Set the flag in the copy at <paramref name="path"/>; returns false if it is not a PE image.</summary>
    public static bool Apply(string path)
    {
        var image = File.ReadAllBytes(path);
        var offset = CharacteristicsOffset(image);
        if (offset < 0)
        {
            return false;
        }
        if (!IsSet(image))
        {
            Set(image, offset, true);
            PrivateFile.Write(path, image);
        }
        return true;
    }

    private static void Set(byte[] image, int offset, bool enabled)
    {
        var value = BitConverter.ToUInt16(image, offset);
        value = enabled ? (ushort)(value | Flag) : (ushort)(value & ~Flag);
        image[offset] = (byte)value;
        image[offset + 1] = (byte)(value >> 8);
    }
}
