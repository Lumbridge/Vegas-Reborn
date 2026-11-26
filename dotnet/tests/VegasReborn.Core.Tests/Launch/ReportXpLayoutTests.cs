using System.Buffers.Binary;
using System.Text;
using VegasReborn.Core.Launch;

namespace VegasReborn.Core.Tests.Launch;

/// <summary>Copy-only widening of the Vegas 1 co-op After Action Report score cell.</summary>
public sealed class ReportXpLayoutTests
{
    // Synthetic layout with the score element's rectangle and count-prefixed placeholder at
    // their shipped offsets, in a buffer the size of the shipped file. No game data is used.
    private static byte[] Layout()
    {
        var data = new byte[77204];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i * 5);
        }
        for (var i = 0; i < ReportXpLayout.OriginalRectangle.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(ReportXpLayout.RectangleOffset + i * 2), ReportXpLayout.OriginalRectangle[i]);
        }
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(ReportXpLayout.PlaceholderOffset - 4), ReportXpLayout.Placeholder.Length);
        Encoding.BigEndianUnicode.GetBytes(ReportXpLayout.Placeholder).CopyTo(data, ReportXpLayout.PlaceholderOffset);
        return data;
    }

    [Fact]
    public void WidensOnlyTheScoreCellLeftEdge()
    {
        var original = Layout();

        var widened = ReportXpLayout.Widen(original);

        Assert.NotNull(widened);
        Assert.Equal(original.Length, widened.Length);
        Assert.Equal(ReportXpLayout.WidenedLeft, BinaryPrimitives.ReadUInt16BigEndian(widened.AsSpan(ReportXpLayout.RectangleOffset)));
        Assert.True(ReportXpLayout.WidenedLeft > 178 && ReportXpLayout.WidenedLeft < 345);
        for (var i = 0; i < original.Length; i++)
        {
            if (i != ReportXpLayout.RectangleOffset && i != ReportXpLayout.RectangleOffset + 1)
            {
                Assert.Equal(original[i], widened[i]);
            }
        }
    }

    [Fact]
    public void LeavesAnUnexpectedLayoutUnchanged()
    {
        var moved = Layout();
        BinaryPrimitives.WriteUInt16BigEndian(moved.AsSpan(ReportXpLayout.RectangleOffset + 2), 421);
        var renamed = Layout();
        BinaryPrimitives.WriteInt32BigEndian(renamed.AsSpan(ReportXpLayout.PlaceholderOffset - 4), 6);

        Assert.Null(ReportXpLayout.Widen(moved));
        Assert.Null(ReportXpLayout.Widen(renamed));
        Assert.Null(ReportXpLayout.Widen(new byte[16]));
    }

    [Fact]
    public void ApplyRebuildsTheCopyFromThePinnedOriginal()
    {
        using var root = new TestDirectory();
        var original = Layout();
        root.Bytes("install/" + ReportXpLayout.File, original);
        var target = root.Bytes("game/" + ReportXpLayout.File, original);
        var copy = Path.Combine(root.Path, "game");
        var install = Path.Combine(root.Path, "install");

        ReportXpLayout.Apply(copy, install, enabled: true, Synthetic.Sha(original));
        ReportXpLayout.Apply(copy, install, enabled: true, Synthetic.Sha(original));

        Assert.Equal(ReportXpLayout.Widen(original), File.ReadAllBytes(target));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(install, ReportXpLayout.File)));
    }

    [Fact]
    public void ApplyRestoresTheOriginalWhenDisabledOrUnpinned()
    {
        using var root = new TestDirectory();
        var original = Layout();
        root.Bytes("install/" + ReportXpLayout.File, original);
        var target = root.Bytes("game/" + ReportXpLayout.File, ReportXpLayout.Widen(original)!);
        var copy = Path.Combine(root.Path, "game");
        var install = Path.Combine(root.Path, "install");

        ReportXpLayout.Apply(copy, install, enabled: false, Synthetic.Sha(original));
        Assert.Equal(original, File.ReadAllBytes(target));

        File.WriteAllBytes(target, ReportXpLayout.Widen(original)!);
        ReportXpLayout.Apply(copy, install, enabled: true);
        Assert.Equal(original, File.ReadAllBytes(target));
    }
}
