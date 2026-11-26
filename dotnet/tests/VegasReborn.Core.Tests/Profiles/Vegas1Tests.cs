using System.Buffers.Binary;
using System.Security.Cryptography;
using VegasReborn.Core.Profiles.Vegas1;

namespace VegasReborn.Core.Tests.Profiles;

/// <summary>
/// PEC codec checks are synthetic. Rank and carrier checks against original data read the
/// reference installation when present (read only) and pass vacuously otherwise; no game
/// data is committed.
/// </summary>
public sealed class Vegas1Tests
{
    private const string Reference = "E:/SteamLibrary/steamapps/common/Rainbow Six Vegas";
    private static readonly bool HasReference = File.Exists(Path.Combine(Reference, RankTable.PecInfoPath));

    [Fact]
    public void ChecksumMatchesPythonReference()
    {
        // Values from vegas_reborn.retail_profile.checksum.
        Assert.Equal(0x0645adefu, PecArchive.Checksum([]));
        Assert.Equal(0x339d926bu, PecArchive.Checksum("123456789"u8));
        Assert.Equal(0xfc891918u, PecArchive.Checksum("123456789"u8, 0));
    }

    [Fact]
    public void PatchChangesOnlyXpAndChecksum()
    {
        var original = RetailProfileSyncTests.Fixture(10, "appearance bytes");
        Assert.Equal(10u, PecArchive.ReadXp(original));
        var patched = PecArchive.PatchXp(original, uint.MaxValue);
        Assert.Equal(uint.MaxValue, PecArchive.ReadXp(patched));
        Assert.Equal(original[4..17], patched[4..17]);
        Assert.Equal(original[21..], patched[21..]);
        Assert.True(PecArchive.SameAppearance(original, patched));
        Assert.False(PecArchive.SameAppearance(original, RetailProfileSyncTests.Fixture(10, "other appearance")));
    }

    [Fact]
    public void MalformedArchivesAreRejected()
    {
        var valid = RetailProfileSyncTests.Fixture();
        var badCrc = (byte[])valid.Clone();
        badCrc[17] ^= 1;
        var badVersion = (byte[])valid.Clone();
        badVersion[12] = 0x18;
        var badOuter = (byte[])valid.Clone();
        badOuter[4] = 5;
        foreach (var data in new[] { badCrc, badVersion, badOuter, valid[..20], valid[..^1], new byte[PecArchive.MaxBytes + 1] })
        {
            Assert.Throws<InvalidDataException>(() => PecArchive.ReadXp(data));
            Assert.False(PecArchive.TryReadXp(data, out _));
        }
    }

    [Fact]
    public void CarrierWrapperRoundTripsAndDetectsTampering()
    {
        var body = RandomNumberGenerator.GetBytes(100);
        var wrapped = MatchmakingCarrier.Wrap(body);
        Assert.Equal(body, MatchmakingCarrier.Unwrap(wrapped));
        wrapped[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => MatchmakingCarrier.Unwrap(wrapped));
        Assert.Throws<InvalidDataException>(() => MatchmakingCarrier.Unwrap(new byte[23]));
    }

    [Fact]
    public void CarrierRejectsUnpinnedSourcesAndSeparateCopiesAreRequired()
    {
        Assert.Throws<InvalidDataException>(() => MatchmakingCarrier.Derive(new byte[MatchmakingCarrier.OriginalBytes]));
        var root = Path.Combine(Path.GetTempPath(), "vr-carrier-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Throws<InvalidDataException>(() => MatchmakingCarrier.Ensure(root, root));
            Assert.Throws<InvalidDataException>(() => MatchmakingCarrier.Ensure(root, Path.Combine(root, "missing")));
            var owned = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "vr-owned-" + Guid.NewGuid().ToString("N"))).FullName;
            try
            {
                File.WriteAllText(Path.Combine(owned, ".companion-ready.json"), """{"version":1,"sourceSha256":"other"}""");
                var error = Assert.Throws<InvalidDataException>(() => MatchmakingCarrier.Ensure(root, owned));
                Assert.Equal("The destination is not a supported launcher-owned game copy", error.Message);
            }
            finally
            {
                Directory.Delete(owned, recursive: true);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OriginalCarrierDerivesThePinnedRepair()
    {
        var source = Path.Combine(Reference, MatchmakingCarrier.RelativeCarrier);
        if (!File.Exists(source))
        {
            return;
        }
        var (derived, receipt) = MatchmakingCarrier.Derive(File.ReadAllBytes(source));
        Assert.Equal(MatchmakingCarrier.DerivedSha256, Convert.ToHexStringLower(SHA256.HashData(derived)));
        Assert.Equal(44370, receipt.DerivedBytes);
        Assert.Equal((500, 75, 390, 18, 72, 482), (receipt.ChunkCount, receipt.BoardCount, receipt.PropertyCount,
            receipt.ModifiedBoardCount, receipt.RemovedColumnCount, receipt.UnchangedChunkCount));
        Assert.Equal("4b552d59a83834fec4517d019746331c0a33db9b717789eb874935c11bae7692", receipt.UnchangedChunksSha256);
        Assert.Equal(new CarrierBoard("0xfffe0000", "None_Standard_TrueSkill"), receipt.ModifiedBoards[0]);
        Assert.Equal(MatchmakingCarrier.Codec, receipt.StatisticsCodec);
    }

    [Fact]
    public void ActualOriginalThresholdBoundariesAndLabels()
    {
        if (!HasReference)
        {
            return;
        }
        var table = RankTable.Load(Reference);
        Assert.Equal(102, table.Records.Count);
        foreach (var (xp, index, ordinal, pips) in new[] { (0u, 0, 1u, 0), (60u, 0, 1u, 0), (499u, 0, 1u, 0),
                     (500u, 1, 1u, 1), (2999u, 5, 1u, 5), (3000u, 6, 2u, 0) })
        {
            var rank = table.RankForExperience(xp);
            Assert.Equal((index, ordinal, pips), (rank.Index, rank.Ordinal, rank.Subrank));
        }
        Assert.Equal("PRIVATE SECOND CLASS", table.RankForExperience(60).Label);
        Assert.Equal("PRIVATE FIRST CLASS", table.RankForExperience(3000).Label);
        Assert.Equal(["RECRUIT", "PRIVATE SECOND CLASS", "PRIVATE FIRST CLASS", "SPECIALIST", "CORPORAL", "SERGEANT",
            "STAFF SERGEANT", "SERGEANT FIRST CLASS", "MASTER SERGEANT", "FIRST SERGEANT", "SERGEANT MAJOR",
            "SECOND LIEUTENANT", "FIRST LIEUTENANT", "CAPTAIN", "MAJOR", "LIEUTENANT COLONEL", "COLONEL", "ELITE"], table.Labels);
    }

    [Fact]
    public void RetainedActualNativeProbe1100()
    {
        if (!HasReference)
        {
            return;
        }
        var rank = RankTable.Load(Reference).RankForExperience(1100);
        Assert.Equal(2, rank.Index);
        Assert.Equal(1500u, rank.NextThreshold);
        Assert.Equal(0.2, rank.ProgressToNext!.Value, 9);
        Assert.Equal("Private Second Class  /  2/5\n1,100 XP  ·  400 XP to the next step", rank.DisplayText);
    }

    [Fact]
    public void MaximumIsBoundedWithoutNextRecordAccess()
    {
        if (!HasReference)
        {
            return;
        }
        var table = RankTable.Load(Reference);
        foreach (var xp in new[] { 850000u, 2000000u, uint.MaxValue })
        {
            var rank = table.RankForExperience(xp);
            Assert.Equal((101, 17u, 5, "ELITE"), (rank.Index, rank.Ordinal, rank.Subrank, rank.Label));
            Assert.Null(rank.NextThreshold);
            Assert.Null(rank.ProgressToNext);
            Assert.EndsWith("Maximum rank", rank.DisplayText);
        }
    }

    [Fact]
    public void ModifiedOrTruncatedSourcesFailClosed()
    {
        if (!HasReference)
        {
            Assert.Throws<RankSourceException>(() => RankTable.Parse(new byte[RankTable.PecInfoBytes], []));
            return;
        }
        var carrier = File.ReadAllBytes(Path.Combine(Reference, RankTable.PecInfoPath));
        var menus = File.ReadAllBytes(Path.Combine(Reference, RankTable.EnglishMenusPath));
        var altered = (byte[])carrier.Clone();
        altered[108] ^= 1;
        foreach (var (data, labels) in new[] { (altered, menus), (carrier[..^1], menus), (carrier, menus.Concat("\n"u8.ToArray()).ToArray()) })
        {
            Assert.Throws<RankSourceException>(() => RankTable.Parse(data, labels));
        }
    }

    [Fact]
    public void NativeArchiveCrcIsRequiredWithoutWrites()
    {
        if (!HasReference)
        {
            return;
        }
        var table = RankTable.Load(Reference);
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "vr-rank-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var target = Path.Combine(directory, "R6_PECPEC");
            File.WriteAllBytes(target, PecArchive.PatchXp(RetailProfileSyncTests.Fixture(), 60));
            var before = File.ReadAllBytes(target);
            Assert.Equal(60u, RankTable.ReadRank(target, table).Experience);
            Assert.Equal(before, File.ReadAllBytes(target));
            Assert.StartsWith("Private Second Class", RankTable.Describe(target, Reference));
            var bad = (byte[])before.Clone();
            bad[17] ^= 1;
            File.WriteAllBytes(target, bad);
            Assert.Throws<InvalidDataException>(() => RankTable.ReadRank(target, table));
            Assert.Equal(RankTable.UnavailableMessage, RankTable.Describe(target, Reference));
            Assert.Equal(RankTable.SyncFirstMessage, RankTable.Describe(Path.Combine(directory, "missing", "R6_PECPEC"), Reference));
            Assert.Equal(RankTable.SignInMessage, RankTable.Describe(null, Reference));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
    [Fact]
    public void SidebarRankIsNullWithoutCharacterOrOriginalData()
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "vr-rank-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var target = Path.Combine(directory, "R6_PECPEC");
            File.WriteAllBytes(target, PecArchive.PatchXp(RetailProfileSyncTests.Fixture(), 1100));
            Assert.Null(RankTable.TryReadRank(null, Reference));
            Assert.Null(RankTable.TryReadRank(target, null));
            Assert.Null(RankTable.TryReadRank(Path.Combine(directory, "missing", "R6_PECPEC"), Reference));
            // A folder without the original rank data.
            Assert.Null(RankTable.TryReadRank(target, directory));
            if (!HasReference)
            {
                return;
            }
            var bad = File.ReadAllBytes(target);
            bad[17] ^= 1;
            var corrupt = Path.Combine(directory, "corrupt");
            File.WriteAllBytes(corrupt, bad);
            Assert.Null(RankTable.TryReadRank(corrupt, Reference));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SidebarRankReadsTitleAndProgressFromOriginalThresholds()
    {
        if (!HasReference)
        {
            return;
        }
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "vr-rank-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var target = Path.Combine(directory, "R6_PECPEC");
            // 1100 XP: record 2 (1000..1500) in the original table, a fifth of the way to the next step.
            File.WriteAllBytes(target, PecArchive.PatchXp(RetailProfileSyncTests.Fixture(), 1100));
            var before = File.ReadAllBytes(target);
            var rank = RankTable.TryReadRank(target, Reference)!;
            Assert.Equal("Private Second Class", rank.Title);
            Assert.Equal(2, rank.Subrank);
            Assert.Equal(0.2, rank.BarProgress, 9);
            Assert.Equal(before, File.ReadAllBytes(target));

            // Exactly on a threshold starts the bar empty; the step below it is the previous label's last pip.
            File.WriteAllBytes(target, PecArchive.PatchXp(RetailProfileSyncTests.Fixture(), 3000));
            rank = RankTable.TryReadRank(target, Reference)!;
            Assert.Equal(("Private First Class", 0, 0.0), (rank.Title, rank.Subrank, rank.BarProgress));
            File.WriteAllBytes(target, PecArchive.PatchXp(RetailProfileSyncTests.Fixture(), 2999));
            rank = RankTable.TryReadRank(target, Reference)!;
            Assert.Equal(("Private Second Class", 5), (rank.Title, rank.Subrank));
            Assert.InRange(rank.BarProgress, 0.99, 1.0);

            // The maximum rank has no next step and shows a full bar.
            File.WriteAllBytes(target, PecArchive.PatchXp(RetailProfileSyncTests.Fixture(), uint.MaxValue));
            rank = RankTable.TryReadRank(target, Reference)!;
            Assert.Equal(("Elite", 1.0), (rank.Title, rank.BarProgress));
            Assert.Null(rank.ProgressToNext);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BarProgressIsClampedAndFullAtMaximum()
    {
        RetailRank Make(double? progress) => new(0, 0, 1, 0, 0, "RankPrivate2ndclass", "LIEUTENANT COLONEL", 0, null, null, progress);
        Assert.Equal(1.0, Make(null).BarProgress);
        Assert.Equal(0.0, Make(-0.5).BarProgress);
        Assert.Equal(1.0, Make(1.5).BarProgress);
        Assert.Equal(0.25, Make(0.25).BarProgress);
        Assert.Equal("Lieutenant Colonel", Make(0).Title);
    }
    [Fact]
    public void PipsFillEarnedStepsThenProgress()
    {
        RetailRank Make(int subrank, double? progress) => new(0, 0, 1, 0, subrank, "RankCaptain", "CAPTAIN", 0, null, null, progress);
        Assert.Equal([0.0, 0.0, 0.0, 0.0, 0.0], Make(0, 0).PipFill);
        Assert.Equal([1.0, 1.0, 0.25, 0.0, 0.0], Make(2, 0.25).PipFill);
        Assert.Equal([1.0, 1.0, 1.0, 1.0, 1.0], Make(5, 0.5).PipFill);
        Assert.Equal([1.0, 1.0, 1.0, 1.0, 1.0], Make(5, null).PipFill);
    }

    [Fact]
    public void EveryRankNameHasSixStepsInTheOriginalTable()
    {
        if (!HasReference)
        {
            return;
        }
        var table = RankTable.Load(Reference);
        var groups = table.Records.GroupBy(row => row.Ordinal).ToList();
        Assert.Equal(17, groups.Count);
        Assert.All(groups, group => Assert.Equal(RetailRank.PipCount + 1, group.Count()));
        Assert.DoesNotContain(table.Records, row => row.Ordinal == 0);
    }

    private static byte[] Tga(int width, int height, byte descriptor, Func<int, int, byte> shade, int type = 2, int depth = 32)
    {
        var data = new byte[18 + width * height * 4];
        data[2] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), (ushort)height);
        data[16] = (byte)depth;
        data[17] = descriptor;
        for (var row = 0; row < height; row++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = 18 + (row * width + x) * 4;
                data[at] = shade(row, x);
                data[at + 3] = 255;
            }
        }
        return data;
    }

    [Fact]
    public void InsigniaDecodesStoredRowOrder()
    {
        // Bottom-left origin (descriptor 8): the first stored row is the bottom row.
        var bottomFirst = RankInsignia.Decode(Tga(2, 2, 8, (row, x) => (byte)(row * 10 + x)));
        Assert.Equal((2, 2), (bottomFirst.Width, bottomFirst.Height));
        Assert.Equal([10, 11, 0, 1], new[] { bottomFirst.Bgra[0], bottomFirst.Bgra[4], bottomFirst.Bgra[8], bottomFirst.Bgra[12] });
        var topFirst = RankInsignia.Decode(Tga(2, 2, 0x28, (row, x) => (byte)(row * 10 + x)));
        Assert.Equal([0, 1, 10, 11], new[] { topFirst.Bgra[0], topFirst.Bgra[4], topFirst.Bgra[8], topFirst.Bgra[12] });
        Assert.Equal(255, topFirst.Bgra[3]);
    }

    [Fact]
    public void InsigniaRejectsOtherFormats()
    {
        Assert.Throws<InvalidDataException>(() => RankInsignia.Decode(new byte[10]));
        Assert.Throws<InvalidDataException>(() => RankInsignia.Decode(Tga(2, 2, 8, (_, _) => 0, type: 10)));
        Assert.Throws<InvalidDataException>(() => RankInsignia.Decode(Tga(2, 2, 0, (_, _) => 0)));
        Assert.Throws<InvalidDataException>(() => RankInsignia.Decode(Tga(2, 2, 8, (_, _) => 0)[..^1]));
        Assert.Null(RankInsignia.TryLoad(null, "RankCaptain"));
        Assert.Null(RankInsignia.TryLoad(Path.GetTempPath(), "RankCaptain"));
        Assert.Null(RankInsignia.TryLoad(Reference, "../../Binaries/R6Vegas_Game"));
    }

    [Fact]
    public void InsigniaLoadsForEveryRankThatHasRecords()
    {
        if (!HasReference)
        {
            return;
        }
        foreach (var key in RankTable.RankLabelKeys.Skip(1))
        {
            var image = RankInsignia.TryLoad(Reference, key);
            Assert.NotNull(image);
            Assert.Equal((48, 48, 48 * 48 * 4), (image.Width, image.Height, image.Bgra.Length));
            Assert.Contains(image.Bgra.Where((_, i) => i % 4 == 3), alpha => alpha != 0);
        }
        // Recruit has no shipped icon.
        Assert.Null(RankInsignia.TryLoad(Reference, "RankRecruit"));
    }
}
