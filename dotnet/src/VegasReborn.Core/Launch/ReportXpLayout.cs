using System.Buffers.Binary;
using System.Text;

namespace VegasReborn.Core.Launch;

/// <summary>
/// Vegas 1 multiplayer co-op After Action Report score cell in the copy's PC menu layout
/// (KellerGame/Content/MenusPC/MP_RSMenus.mgb, big-endian; each text element stores its
/// rectangle as x0, x1, y0, y1 16-bit values 0x24 bytes before its count-prefixed UTF-16BE
/// placeholder). The client binds element L_ScoreValue to report +0x128 and the Match XP
/// report writes "score / XP" into it. In the original layout that element is a 75 px
/// right-aligned points cell (x 345-420) sized for its "= 8,000" placeholder, so the XP half
/// is cut off. The row's middle cell is empty and its label ends at x 178, so the copy moves
/// the cell's left edge to x 184. The right edge, height and alignment are unchanged, so the
/// game's own right-aligned score stays where it was.
/// The file is always rebuilt from the installed original, and only the pinned original is changed.
/// </summary>
internal static class ReportXpLayout
{
    public const string File = "KellerGame/Content/MenusPC/MP_RSMenus.mgb";
    public const string OriginalSha256 = "6c889a78fcec5feca05aaf43fe68be065ae7ccbfaad6814d99c47f1ded041f8b";

    internal const int PlaceholderOffset = 0xff5f;
    internal const string Placeholder = "= 8,000";
    internal const int RectangleOffset = PlaceholderOffset - 0x24;
    internal static readonly ushort[] OriginalRectangle = [345, 420, 320, 340];
    internal const ushort WidenedLeft = 184;

    /// <summary>Write the copy's layout from the installed original, widened when the fix is enabled.</summary>
    public static void Apply(string copy, string source, bool enabled, string expectedSha256 = OriginalSha256)
    {
        var original = Path.Combine(source, File);
        var target = Path.Combine(copy, File);
        if (!System.IO.File.Exists(original) || !System.IO.File.Exists(target) || !PrivateFile.IsWithin(target, copy))
        {
            return;
        }
        var bytes = System.IO.File.ReadAllBytes(original);
        var expected = enabled && PrivateFile.Sha256(bytes) == expectedSha256 ? Widen(bytes) ?? bytes : bytes;
        if (!System.IO.File.ReadAllBytes(target).AsSpan().SequenceEqual(expected))
        {
            PrivateFile.Write(target, expected);
        }
    }

    /// <summary>The layout with the score cell widened, or null when it is not where expected.</summary>
    internal static byte[]? Widen(byte[] original)
    {
        var encoded = Encoding.BigEndianUnicode.GetBytes(Placeholder);
        if (PlaceholderOffset + encoded.Length > original.Length
            || BinaryPrimitives.ReadInt32BigEndian(original.AsSpan(PlaceholderOffset - 4)) != Placeholder.Length
            || !original.AsSpan(PlaceholderOffset, encoded.Length).SequenceEqual(encoded))
        {
            return null;
        }
        for (var i = 0; i < OriginalRectangle.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt16BigEndian(original.AsSpan(RectangleOffset + i * 2)) != OriginalRectangle[i])
            {
                return null;
            }
        }
        var bytes = (byte[])original.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(RectangleOffset), WidenedLeft);
        return bytes;
    }
}
