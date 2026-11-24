using System.Buffers.Binary;
using System.Text;

namespace VegasReborn.Core.Launch;

/// <summary>
/// Vegas 1 leaderboard selector labels in the copy's PC menu layout
/// (KellerGame/Content/MenusPC/Statistics.mgb, big-endian, UTF-16BE strings with a 32-bit
/// character count). Each leaderboard page has a console label (A_LabelLeftTrigger /
/// A_LabelRightTrigger) that the game fills with the previous and next map, and a PC arrow
/// button a few pixels away (element ids 0xa18368cf / 0xfca3a7d0) whose text the game never
/// sets. The button keeps its layout placeholder, so both texts are drawn over each other.
/// The placeholders are replaced with spaces of the same length; nothing moves in the file.
/// The file is always rebuilt from the installed original, and only the pinned original is changed.
/// </summary>
internal static class LeaderboardLabels
{
    public const string File = "KellerGame/Content/MenusPC/Statistics.mgb";
    public const string OriginalSha256 = "2474a964b8f170f8ed4144f5fe7408b9067f184e7880a350fc0c8e79102f1c39";

    /// <summary>Character offset and original text of each PC arrow-button placeholder.</summary>
    internal static readonly (int Offset, string Text)[] Placeholders =
    [
        (0x0cec, "TERRORIST HUNT"),
        (0x0d6d, "MAP LABEL"),
        (0x0f93, "RETRIEVAL"),
        (0x100a, "ATTACK AND DEFEND"),
        (0x1240, "RETRIEVAL"),
        (0x12b7, "ATTACK AND DEFEND"),
    ];

    /// <summary>Write the copy's layout from the installed original, blanking the placeholders.</summary>
    public static void Apply(string copy, string source, string expectedSha256 = OriginalSha256)
    {
        var original = Path.Combine(source, File);
        var target = Path.Combine(copy, File);
        if (!System.IO.File.Exists(original) || !System.IO.File.Exists(target) || !PrivateFile.IsWithin(target, copy))
        {
            return;
        }
        var bytes = System.IO.File.ReadAllBytes(original);
        var expected = PrivateFile.Sha256(bytes) == expectedSha256 ? Blank(bytes) ?? bytes : bytes;
        if (!System.IO.File.ReadAllBytes(target).AsSpan().SequenceEqual(expected))
        {
            PrivateFile.Write(target, expected);
        }
    }

    /// <summary>The layout with every placeholder blanked, or null when any is not where expected.</summary>
    internal static byte[]? Blank(byte[] original)
    {
        var bytes = (byte[])original.Clone();
        foreach (var (offset, text) in Placeholders)
        {
            var encoded = Encoding.BigEndianUnicode.GetBytes(text);
            if (offset < 4 || offset + encoded.Length > bytes.Length
                || BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset - 4)) != text.Length
                || !bytes.AsSpan(offset, encoded.Length).SequenceEqual(encoded))
            {
                return null;
            }
            Encoding.BigEndianUnicode.GetBytes(new string(' ', text.Length)).CopyTo(bytes, offset);
        }
        return bytes;
    }
}
