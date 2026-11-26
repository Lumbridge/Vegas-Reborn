using System.Buffers.Binary;

namespace VegasReborn.Core.Profiles.Vegas1;

/// <summary>A decoded insignia: 32-bit BGRA rows, top row first, straight (not premultiplied) alpha.</summary>
public sealed record RankImage(int Width, int Height, byte[] Bgra);

/// <summary>
/// Rainbow Six Vegas (1) rank insignia, read only from the installed game's menu textures.
/// The game names each icon <c>Ic_</c> + the rank label key (for example
/// <c>Ic_RankCaptain</c>), in ordinal order beside the label keys in R6Vegas_Game.exe, and
/// ships them as loose 48x48 32-bit uncompressed TGA files listed in
/// <c>MosaicFiles/CommonTex.mos.xml</c>. Recruit (ordinal 0) has no shipped icon and no
/// rank record uses it. Nothing is copied or committed; a missing or unexpected file
/// gives no image.
/// </summary>
public static class RankInsignia
{
    public const string TextureFolder = "KellerGame/Content/MenusPC/Textures/common";
    private const int MaxSide = 256;
    private const int MaxBytes = 18 + 255 + MaxSide * MaxSide * 4 + 4096;

    public static string PathFor(string gameRoot, string labelKey) =>
        Path.Combine(gameRoot, TextureFolder, "Ic_" + labelKey + ".tga");

    /// <summary>The insignia for a rank label key, or null when it is absent or not the shipped format.</summary>
    public static RankImage? TryLoad(string? gameRoot, string labelKey)
    {
        if (string.IsNullOrEmpty(gameRoot) || !RankTable.RankLabelKeys.Contains(labelKey))
        {
            return null;
        }
        try
        {
            var info = new FileInfo(PathFor(gameRoot, labelKey));
            if (!info.Exists || info.Length > MaxBytes)
            {
                return null;
            }
            return Decode(File.ReadAllBytes(info.FullName));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Decodes the shipped icon format: TGA type 2, no colour map, 32 bits with 8 alpha bits.</summary>
    public static RankImage Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 18)
        {
            throw new InvalidDataException("insignia_truncated");
        }
        int idLength = data[0], colorMapType = data[1], imageType = data[2];
        var width = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
        int depth = data[16], descriptor = data[17];
        if (colorMapType != 0 || imageType != 2 || depth != 32 || (descriptor & 0x0f) != 8 || (descriptor & 0x10) != 0
            || width is 0 or > MaxSide || height is 0 or > MaxSide)
        {
            throw new InvalidDataException("insignia_unsupported_format");
        }
        var stride = width * 4;
        var start = 18 + idLength;
        if (data.Length < start + stride * height)
        {
            throw new InvalidDataException("insignia_truncated");
        }
        var topFirst = (descriptor & 0x20) != 0;
        var pixels = new byte[stride * height];
        for (var row = 0; row < height; row++)
        {
            var source = data.Slice(start + row * stride, stride);
            var target = topFirst ? row : height - 1 - row;
            source.CopyTo(pixels.AsSpan(target * stride, stride));
        }
        return new RankImage(width, height, pixels);
    }
}
