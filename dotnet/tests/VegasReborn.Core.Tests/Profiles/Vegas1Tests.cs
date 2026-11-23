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
                Assert.Equal("The destination is not a supported companion-owned game copy", error.Message);
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
}
