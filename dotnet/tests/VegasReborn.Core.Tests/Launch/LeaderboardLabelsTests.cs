using System.Buffers.Binary;
using System.Text;
using VegasReborn.Core.Launch;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Tests.Launch;

/// <summary>Copy-only blanking of the Vegas 1 leaderboard arrow-button placeholders.</summary>
public sealed class LeaderboardLabelsTests
{
    // Synthetic layout with each placeholder at its shipped offset, count-prefixed, in a
    // buffer the size of the shipped file. No game data is used.
    private static byte[] Layout()
    {
        var data = new byte[29017];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i * 7);
        }
        foreach (var (offset, text) in LeaderboardLabels.Placeholders)
        {
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset - 4), text.Length);
            Encoding.BigEndianUnicode.GetBytes(text).CopyTo(data, offset);
        }
        return data;
    }

    [Fact]
    public void BlanksOnlyThePlaceholderCharacters()
    {
        var original = Layout();

        var blanked = LeaderboardLabels.Blank(original);

        Assert.NotNull(blanked);
        Assert.Equal(original.Length, blanked.Length);
        var changed = new bool[original.Length];
        foreach (var (offset, text) in LeaderboardLabels.Placeholders)
        {
            Assert.Equal(new string(' ', text.Length), Encoding.BigEndianUnicode.GetString(blanked, offset, text.Length * 2));
            Assert.Equal(text.Length, BinaryPrimitives.ReadInt32BigEndian(blanked.AsSpan(offset - 4)));
            for (var i = offset; i < offset + text.Length * 2; i++)
            {
                changed[i] = true;
            }
        }
        for (var i = 0; i < original.Length; i++)
        {
            if (!changed[i])
            {
                Assert.Equal(original[i], blanked[i]);
            }
        }
    }

    [Fact]
    public void LeavesAnUnexpectedLayoutUnchanged()
    {
        var moved = Layout();
        var (offset, _) = LeaderboardLabels.Placeholders[1];
        BinaryPrimitives.WriteInt32BigEndian(moved.AsSpan(offset - 4), 8);

        Assert.Null(LeaderboardLabels.Blank(moved));
        Assert.Null(LeaderboardLabels.Blank(new byte[16]));
    }

    [Fact]
    public void ApplyRebuildsTheCopyFromThePinnedOriginal()
    {
        using var root = new TestDirectory();
        var original = Layout();
        root.Bytes("install/" + LeaderboardLabels.File, original);
        var target = root.Bytes("game/" + LeaderboardLabels.File, original);
        var copy = Path.Combine(root.Path, "game");
        var install = Path.Combine(root.Path, "install");

        LeaderboardLabels.Apply(copy, install, Synthetic.Sha(original));
        LeaderboardLabels.Apply(copy, install, Synthetic.Sha(original));

        Assert.Equal(LeaderboardLabels.Blank(original), File.ReadAllBytes(target));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(install, LeaderboardLabels.File)));
    }

    [Fact]
    public void ApplyRestoresAnUnpinnedOriginalUnchanged()
    {
        using var root = new TestDirectory();
        var original = Layout();
        root.Bytes("install/" + LeaderboardLabels.File, original);
        var target = root.Bytes("game/" + LeaderboardLabels.File, LeaderboardLabels.Blank(original)!);

        LeaderboardLabels.Apply(Path.Combine(root.Path, "game"), Path.Combine(root.Path, "install"));

        Assert.Equal(original, File.ReadAllBytes(target));
    }

    [Fact]
    public void OnlyVegas1HasTheFix()
    {
        Assert.NotNull(TitleCatalog.Vegas1.Fix(TitleCatalog.LeaderboardLabels));
        Assert.Null(TitleCatalog.Vegas2.Fix(TitleCatalog.LeaderboardLabels));
    }
}
