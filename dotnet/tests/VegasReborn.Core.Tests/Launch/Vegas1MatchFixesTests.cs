using VegasReborn.Core.Launch;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Tests.Launch;

/// <summary>Copy-only 16-player mission limits and the large-address-aware flag.</summary>
public sealed class Vegas1MatchFixesTests
{
    // Shape of the shipped MP map files (MP_Casino_01.ini, CRLF).
    private const string Shipped = "[R6Game.R6MissionConfig]\r\nm_iId=5\r\n\r\n"
        + "[GameMode0]\r\nm_eGameMode=GAMEMODE_DEATHMATCH\r\nm_iMissionOrderId=2\r\nm_iMaxPlayers=16\r\nm_iMaxPlayersListenServer=14\r\n\r\n"
        + "[GameMode6]\r\nm_eGameMode=GAMEMODE_TERROHUNT\r\nm_iMaxPlayers=4\r\nm_iMissionOrderId=2\r\n\r\n"
        + "[GameMode7]\r\nm_eGameMode=GAMEMODE_CONQUEST\r\nm_iMissionOrderId=2\r\nm_iMaxPlayers=16\r\nm_iMaxPlayersListenServer=14\r\n\r\n"
        + "[Sound]\r\nm_bBinarizeSound = TRUE\r\n";

    [Fact]
    public void RaisesTerroristHuntAndHostedAdversarialLimitsOnly()
    {
        var raised = MissionPlayerLimits.Raise(Shipped);

        Assert.Equal(Shipped
            .Replace("m_iMaxPlayersListenServer=14", "m_iMaxPlayersListenServer=16")
            .Replace("m_iMaxPlayers=4\r\n", "m_iMaxPlayers=16\r\nm_iMaxPlayersListenServer=16\r\n"), raised);
    }

    [Fact]
    public void StoryCoopAndUnexpectedValuesAreLeftAsShipped()
    {
        const string story = "[GameMode1]\nm_eGameMode=GAMEMODE_STORY\nm_iMaxPlayers=4\n";
        const string custom = "[GameMode0]\nm_eGameMode=GAMEMODE_TERROHUNT\nm_iMaxPlayers=6\n"
            + "[GameMode1]\nm_eGameMode=GAMEMODE_DEATHMATCH\nm_iMaxPlayers=12\nm_iMaxPlayersListenServer=10\n";

        Assert.Null(MissionPlayerLimits.Raise(story));
        Assert.Null(MissionPlayerLimits.Raise(custom));
        Assert.Null(MissionPlayerLimits.Raise("[R6Game.R6MissionConfig]\nm_iId=1\n"));
    }

    [Fact]
    public void ApplyRebuildsCopiedFilesFromTheInstalledOriginal()
    {
        using var root = new TestDirectory();
        root.File("install/KellerGame/Content/CookedPc/Maps/MP_Casino/MP_Casino_01.ini", Shipped);
        root.File("install/KellerGame/Content/CookedPc/Maps/01_Mexico/01_Mexico_01.ini", "[GameMode1]\r\nm_eGameMode=GAMEMODE_STORY\r\nm_iMaxPlayers=4\r\n");
        root.File("game/KellerGame/Content/CookedPc/Maps/MP_Casino/MP_Casino_01.ini", Shipped);
        root.File("game/KellerGame/Content/CookedPc/Maps/01_Mexico/01_Mexico_01.ini", "[GameMode1]\r\nm_eGameMode=GAMEMODE_STORY\r\nm_iMaxPlayers=4\r\n");
        var copy = Path.Combine(root.Path, "game");

        MissionPlayerLimits.Apply(copy, Path.Combine(root.Path, "install"));
        MissionPlayerLimits.Apply(copy, Path.Combine(root.Path, "install"));

        var expected = MissionPlayerLimits.Raise(Shipped);
        Assert.Equal(expected, File.ReadAllText(Path.Combine(copy, "KellerGame/Content/CookedPc/Maps/MP_Casino/MP_Casino_01.ini")));
        Assert.Equal(Shipped, File.ReadAllText(Path.Combine(root.Path, "install/KellerGame/Content/CookedPc/Maps/MP_Casino/MP_Casino_01.ini")));
        Assert.Equal("[GameMode1]\r\nm_eGameMode=GAMEMODE_STORY\r\nm_iMaxPlayers=4\r\n",
            File.ReadAllText(Path.Combine(copy, "KellerGame/Content/CookedPc/Maps/01_Mexico/01_Mexico_01.ini")));
    }

    private const string Maps = "KellerGame/Content/CookedPc/Maps/";
    private const string MexicoStory = "[GameMode0]\r\r\nm_eGameMode=GAMEMODE_STORY\r\r\nm_iMaxPlayers=4\r\r\nm_iMissionOrderId=2\r\r\n";

    [Fact]
    public void ApplyRestoresAnUnraisableFileToTheOriginal()
    {
        using var root = new TestDirectory();
        const string original = "[GameMode1]\r\r\nm_eGameMode=GAMEMODE_STORY\r\r\nm_iMaxPlayers=4\r\r\n";
        root.File($"install/{Maps}01_Mexico/01_Mexico_01.ini", original);
        var target = root.File($"game/{Maps}01_Mexico/01_Mexico_01.ini", original + "[GameMode2]\r\r\nm_eGameMode=GAMEMODE_TERROHUNT\r\r\n");

        MissionPlayerLimits.Apply(Path.Combine(root.Path, "game"), Path.Combine(root.Path, "install"));

        Assert.Equal(original, File.ReadAllText(target));
    }

    [Fact]
    public void ApplyWithACustomFolderRaisesThatFolderAndIgnoresTheMapsFolder()
    {
        using var root = new TestDirectory();
        const string pack = "KellerGame/Content/CookedPc/MapsPack/";
        root.File($"install/{pack}PAK_A/PAK_A_01.ini", Shipped);
        var packTarget = root.File($"game/{pack}PAK_A/PAK_A_01.ini", Shipped);
        root.File($"install/{Maps}MP_Casino/MP_Casino_01.ini", Shipped);
        var mapsTarget = root.File($"game/{Maps}MP_Casino/MP_Casino_01.ini", Shipped);
        root.File($"install/{pack}01_Mexico/01_Mexico_04.ini", MexicoStory);
        var listedTarget = root.File($"game/{pack}01_Mexico/01_Mexico_04.ini", MexicoStory);

        MissionPlayerLimits.Apply(Path.Combine(root.Path, "game"), Path.Combine(root.Path, "install"), pack.TrimEnd('/'));

        Assert.Equal(MissionPlayerLimits.Raise(Shipped), File.ReadAllText(packTarget));
        Assert.Equal(MexicoStory, File.ReadAllText(listedTarget));
        Assert.Equal(Shipped, File.ReadAllText(mapsTarget));
    }

    [Fact]
    public void Vegas2RaisesEachOfItsMissionFolders()
    {
        using var root = new TestDirectory();
        const string story = "[GameMode0]\r\nm_eGameMode=GAMEMODE_STORY\r\nm_iMaxPlayers=2\r\n";
        const string options = "[Options]\r\nm_iMaxPlayers=14\r\n";
        var targets = new List<string>();
        foreach (var folder in MissionPlayerLimits.Vegas2Folders)
        {
            root.File($"install/{folder}/MP_Test/MP_Test.ini", Shipped);
            targets.Add(root.File($"game/{folder}/MP_Test/MP_Test.ini", Shipped));
        }
        root.File("install/KellerGame/CookedPC/MapsBishop/Story/Story_01.ini", story);
        var storyTarget = root.File("game/KellerGame/CookedPC/MapsBishop/Story/Story_01.ini", story);
        var serverOptions = root.File("game/KellerGame/Config/R6ServerOptions.ini", options);
        root.File("install/KellerGame/Config/R6ServerOptions.ini", options);

        GamePreparer.ApplyMissionContent(new AppPaths(Path.Combine(root.Path, "app")), TitleCatalog.Vegas2,
            Path.Combine(root.Path, "game"), Path.Combine(root.Path, "install"));

        foreach (var target in targets)
        {
            Assert.Equal(MissionPlayerLimits.Raise(Shipped), File.ReadAllText(target));
        }
        Assert.Equal(story, File.ReadAllText(storyTarget));
        Assert.Equal(options, File.ReadAllText(serverOptions));
    }

    [Fact]
    public void LargeAddressAwareSetsOnlyTheFlagAndKeepsTheIdentity()
    {
        using var root = new TestDirectory();
        var original = Synthetic.PeImage("client");
        var path = root.Bytes("game/client.exe", original);

        Assert.False(LargeAddressAware.IsSet(original));
        Assert.True(LargeAddressAware.Apply(path));
        Assert.True(LargeAddressAware.Apply(path));

        var patched = File.ReadAllBytes(path);
        Assert.True(LargeAddressAware.IsSet(patched));
        var differences = Enumerable.Range(0, original.Length).Where(i => original[i] != patched[i]).ToArray();
        Assert.Equal([0x96], differences);
        Assert.Equal(0x2e, patched[0x96]);
        Assert.True(LargeAddressAware.Matches(patched, Synthetic.Sha(original)));
        Assert.True(LargeAddressAware.Matches(original, Synthetic.Sha(original)));
        patched[^1] ^= 1;
        Assert.False(LargeAddressAware.Matches(patched, Synthetic.Sha(original)));
    }

    [Fact]
    public void NonPeFilesAreNotChanged()
    {
        using var root = new TestDirectory();
        var path = root.Bytes("game/client.exe", "not an executable"u8.ToArray());

        Assert.False(LargeAddressAware.Apply(path));
        Assert.Equal("not an executable"u8.ToArray(), File.ReadAllBytes(path));
        Assert.False(LargeAddressAware.Matches("MZ"u8.ToArray(), Synthetic.Sha("other"u8.ToArray())));
    }
}
