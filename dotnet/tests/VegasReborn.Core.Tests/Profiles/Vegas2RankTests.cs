using System.Buffers.Binary;
using VegasReborn.Core.Profiles.Vegas1;
using VegasReborn.Core.Profiles.Vegas2;

namespace VegasReborn.Core.Tests.Profiles;

/// <summary>
/// Save codec checks are synthetic. Rank and insignia checks read the reference Vegas 2
/// installation when present (read only) and pass vacuously otherwise; no game data or save
/// is committed.
/// </summary>
public sealed class Vegas2RankTests
{
    private const string Reference = "E:/SteamLibrary/steamapps/common/Rainbow Six Vegas 2";
    private static readonly bool HasReference = File.Exists(Path.Combine(Reference, RankTable.Vegas2.PecInfoPath));

    /// <summary>A save shaped like the retail PROFILE3.DAT: header, version, flag byte, XP, then other fields.</summary>
    internal static byte[] Save(uint xp, int length = 239)
    {
        var data = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, PecSave.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), (uint)(length - 12));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), PecSave.RawVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(PecSave.XpOffset), xp);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(21), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(25), 6);
        return data;
    }

    [Fact]
    public void SaveReadsXpAfterHeaderAndVersion()
    {
        Assert.Equal(30u, PecSave.ReadXp(Save(30)));
        Assert.Equal(0u, PecSave.ReadXp(Save(0)));
        Assert.Equal(uint.MaxValue, PecSave.ReadXp(Save(uint.MaxValue, 400)));
    }

    [Fact]
    public void SaveRejectsOtherSlotsAndDamage()
    {
        var settings = Save(30);
        BinaryPrimitives.WriteUInt32LittleEndian(settings.AsSpan(8), 4); // PROFILE2-style header
        var wrongMagic = Save(30);
        wrongMagic[0] = 0x33;
        var wrongVersion = Save(30);
        wrongVersion[12] = 0x19;
        var nonZero = Save(30);
        nonZero[4] = 1;
        foreach (var data in new[] { settings, wrongMagic, wrongVersion, nonZero, Save(30)[..20], Save(30)[..^1], new byte[PecSave.MaxBytes + 1] })
        {
            Assert.Throws<InvalidDataException>(() => PecSave.ReadXp(data));
            Assert.False(PecSave.TryReadXp(data, out _));
        }
    }

    [Fact]
    public void OriginalTableHasTwentyTwoNamesFromRecruit()
    {
        if (!HasReference)
        {
            Assert.Throws<RankSourceException>(() => RankTable.Parse(new byte[RankTable.Vegas2.PecInfoBytes], [], RankTable.Vegas2));
            return;
        }
        var table = RankTable.Load(Reference, RankTable.Vegas2);
        Assert.Equal(111, table.Records.Count);
        Assert.Equal(22, table.Labels.Count);
        Assert.Equal("RECRUIT", table.Labels[0]);
        Assert.Equal("WARRANT OFFICER", table.Labels[14]);
        Assert.Equal("ELITE", table.Labels[21]);
        var perName = table.Records.GroupBy(row => row.Ordinal).ToDictionary(group => group.Key, group => group.Count());
        Assert.All(Enumerable.Range(0, 21), ordinal => Assert.Equal(5, perName[(uint)ordinal]));
        Assert.Equal(6, perName[21]);
        // The Vegas 1 source refuses the Vegas 2 file.
        Assert.Throws<RankSourceException>(() => RankTable.Load(Reference));
    }

    [Fact]
    public void RanksUseOriginalThresholdsAndPerNamePips()
    {
        if (!HasReference)
        {
            return;
        }
        var table = RankTable.Load(Reference, RankTable.Vegas2);
        var recruit = table.RankForExperience(30);
        Assert.Equal(("Recruit", 0, 4, 100u), (recruit.Title, recruit.Subrank, recruit.PipCount, recruit.NextThreshold!.Value));
        Assert.Equal(0.3, recruit.BarProgress, 9);
        Assert.Equal([0.3, 0.0, 0.0, 0.0], recruit.PipFill.Select(value => Math.Round(value, 9)));
        var lastRecruit = table.RankForExperience(499);
        Assert.Equal(("Recruit", 4), (lastRecruit.Title, lastRecruit.Subrank));
        Assert.Equal([1.0, 1.0, 1.0, 1.0], lastRecruit.PipFill);
        var private2 = table.RankForExperience(500);
        Assert.Equal(("Private Second Class", 0), (private2.Title, private2.Subrank));
        var warrant = table.RankForExperience(140000);
        Assert.Equal(("Warrant Officer", "RankJuniorOfficer"), (warrant.Title, warrant.LabelKey));
        var elite = table.RankForExperience(uint.MaxValue);
        Assert.Equal(("Elite", 5, 5), (elite.Title, elite.Subrank, elite.PipCount));
        Assert.Null(elite.ProgressToNext);
        Assert.All(elite.PipFill, value => Assert.Equal(1.0, value));
        Assert.Equal("Recruit  /  0/4\n30 XP  ·  70 XP to the next step", recruit.DisplayText);
    }

    [Fact]
    public void SidebarRankReadsSaveWithoutWriting()
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "vr-rank2-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var save = Path.Combine(directory, PecSave.FileName);
            File.WriteAllBytes(save, Save(30));
            Assert.Null(RankTable.TryReadRank(save, directory, RankTable.Vegas2, data => PecSave.ReadXp(data)));
            Assert.Null(RankTable.TryReadRank(Path.Combine(directory, "missing.dat"), Reference, RankTable.Vegas2, data => PecSave.ReadXp(data)));
            if (!HasReference)
            {
                return;
            }
            var before = File.ReadAllBytes(save);
            var rank = RankTable.TryReadRank(save, Reference, RankTable.Vegas2, data => PecSave.ReadXp(data))!;
            Assert.Equal(("Recruit", 30u), (rank.Title, rank.Experience));
            Assert.Equal(before, File.ReadAllBytes(save));
            var bad = (byte[])before.Clone();
            bad[12] = 0x19;
            File.WriteAllBytes(save, bad);
            Assert.Null(RankTable.TryReadRank(save, Reference, RankTable.Vegas2, data => PecSave.ReadXp(data)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InsigniaLoadsForEveryNameButRecruit()
    {
        if (!HasReference)
        {
            return;
        }
        foreach (var key in RankTable.Vegas2.LabelKeys.Skip(1))
        {
            var image = RankInsignia.TryLoad(Reference, key, RankTable.Vegas2);
            Assert.NotNull(image);
            Assert.Equal((64, 64), (image.Width, image.Height));
        }
        Assert.Null(RankInsignia.TryLoad(Reference, "RankRecruit", RankTable.Vegas2));
        // Vegas 2 keys are not valid for the Vegas 1 source.
        Assert.Null(RankInsignia.TryLoad(Reference, "RankLanceCorporal"));
    }
}
