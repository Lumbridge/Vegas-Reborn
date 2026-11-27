using System.Buffers.Binary;
using VegasReborn.Core.Profiles.Vegas1;
using VegasReborn.Core.Profiles.Vegas2;

namespace VegasReborn.Core.Tests.Profiles;

/// <summary>
/// Save offsets are checked synthetically. Level thresholds, labels and icons read the
/// reference Vegas 2 installation when present (read only) and pass vacuously otherwise.
/// </summary>
public sealed class Vegas2AcesTests
{
    private const string Reference = "E:/SteamLibrary/steamapps/common/Rainbow Six Vegas 2";
    private static readonly bool HasReference = File.Exists(Path.Combine(Reference, AcesTable.PackagePath));

    private static readonly uint[] OriginalRequired =
        [25, 50, 85, 120, 165, 210, 265, 320, 385, 450, 525, 600, 685, 770, 865, 960, 1065, 1170, 1285, 1400];

    private static byte[] Save(uint marksman, uint cqb, uint assault)
    {
        var data = Vegas2RankTests.Save(30);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(PecSave.AcesOffset), marksman);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(PecSave.AcesOffset + 4), cqb);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(PecSave.AcesOffset + 8), assault);
        return data;
    }

    [Fact]
    public void SaveReadsThreePointTotalsAfterXp()
    {
        Assert.Equal([3u, 6u, 0u], PecSave.ReadAces(Vegas2RankTests.Save(30)));
        Assert.Equal([400u, 1u, 1400u], PecSave.ReadAces(Save(400, 1, 1400)));
        var bad = Save(1, 2, 3);
        bad[12] = 0x19;
        Assert.Throws<InvalidDataException>(() => PecSave.ReadAces(bad));
        Assert.Throws<InvalidDataException>(() => PecSave.ReadAces(Vegas2RankTests.Save(30, 32)));
        Assert.Equal(3, PecSave.ReadAces(Vegas2RankTests.Save(30, 33)).Length);
    }

    [Fact]
    public void OriginalLevelsAndLabels()
    {
        if (!HasReference)
        {
            Assert.Throws<RankSourceException>(() => AcesTable.Parse(new byte[AcesTable.PackageBytes], []));
            return;
        }
        var table = AcesTable.Load(Reference);
        Assert.Equal(OriginalRequired, table.Required);
        Assert.Equal(["MARKSMAN", "CQB", "ASSAULT"], table.Labels);
    }

    [Fact]
    public void LevelsAndProgressFollowCumulativeThresholds()
    {
        if (!HasReference)
        {
            return;
        }
        var table = AcesTable.Load(Reference);
        var start = table.CategoryFor(0, 3);
        Assert.Equal(("Marksmanship", "Marksman", 0, 25u), (start.Key, start.Title, start.Level, start.NextRequired!.Value));
        Assert.Equal(0.12, start.Progress, 9);
        Assert.Equal("Marksman: level 0, 3 of 25 points", start.DisplayText);
        var cqb = table.CategoryFor(1, 6);
        Assert.Equal(("CQB", "CQB", 0), (cqb.Key, cqb.Title, cqb.Level));
        var exact = table.CategoryFor(2, 50);
        Assert.Equal((2, 85u, 0.0), (exact.Level, exact.NextRequired!.Value, exact.Progress));
        var between = table.CategoryFor(2, 100);
        Assert.Equal(3, between.Level);
        Assert.Equal(15.0 / 35.0, between.Progress, 9);
        var max = table.CategoryFor(2, 5000);
        Assert.Equal((20, (uint?)null, 1.0), (max.Level, max.NextRequired, max.Progress));
        Assert.Equal("Assault: level 20, maximum level", max.DisplayText);
    }

    [Fact]
    public void TryReadReadsSaveWithoutWriting()
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "vr-aces-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var save = Path.Combine(directory, PecSave.FileName);
            File.WriteAllBytes(save, Save(3, 6, 0));
            Assert.Null(AcesTable.TryRead(save, directory));
            Assert.Null(AcesTable.TryRead(null, Reference));
            Assert.Null(AcesTable.TryRead(Path.Combine(directory, "missing.dat"), Reference));
            if (!HasReference)
            {
                return;
            }
            var before = File.ReadAllBytes(save);
            var aces = AcesTable.TryRead(save, Reference)!;
            Assert.Equal([("Marksmanship", 3u, 0), ("CQB", 6u, 0), ("Assault", 0u, 0)], aces.Select(a => (a.Key, a.Points, a.Level)));
            Assert.Equal(before, File.ReadAllBytes(save));
            var bad = (byte[])before.Clone();
            bad[0] = 0x33;
            File.WriteAllBytes(save, bad);
            Assert.Null(AcesTable.TryRead(save, Reference));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void IconsLoadForTheThreeCategoriesOnly()
    {
        Assert.Null(AcesTable.TryLoadIcon(Reference, "Marksmanship_Silver"));
        if (!HasReference)
        {
            return;
        }
        foreach (var (key, _) in AcesTable.Categories)
        {
            var icon = AcesTable.TryLoadIcon(Reference, key);
            Assert.NotNull(icon);
            Assert.Equal((64, 64), (icon.Width, icon.Height));
        }
    }
}
