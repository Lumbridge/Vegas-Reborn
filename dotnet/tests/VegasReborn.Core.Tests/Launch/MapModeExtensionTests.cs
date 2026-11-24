using VegasReborn.Core.Launch;

namespace VegasReborn.Core.Tests.Launch;

/// <summary>Versus modes added to story-mission ini files.</summary>
public sealed class MapModeExtensionTests
{
    private const string Story = "[R6Game.R6MissionConfig]\r\nm_iId=65\r\n\r\n[GameMode0]\r\nm_eGameMode=GAMEMODE_STORY\r\nm_iMaxPlayers=4\r\nm_iMissionOrderId=2\r\n\r\n"
        + "[GameMode1]\r\nm_eGameMode=GAMEMODE_MISSION\r\nm_iMissionOrderId=20\r\n\r\n[PS3]\r\nm_bCanBeLaunch = TRUE\r\n";

    [Fact]
    public void OrderIdsMatchTheSpecTable()
    {
        Assert.Equal(18, MapModeExtension.OrderIds.Count);
        Assert.Equal(30, MapModeExtension.OrderIds["01_Mexico/01_Mexico_04.ini"]);
        Assert.Equal(35, MapModeExtension.OrderIds["02_Casino/02_Casino_06.ini"]);
        Assert.Equal(42, MapModeExtension.OrderIds["99_HeliRides/99_HeliRides_02InNear.ini"]);
        Assert.Equal(46, MapModeExtension.OrderIds["99_HeliRides/99_HeliRides_06In.ini"]);
        Assert.False(MapModeExtension.OrderIds.ContainsKey("02_Casino/02_Casino_03.ini"));
    }

    [Fact]
    public void ExtendNumbersAfterTheLastGameModeAndKeepsOtherSections()
    {
        var extended = MapModeExtension.Extend(Story, 30);

        Assert.Contains("[GameMode2]\r\nm_eGameMode=GAMEMODE_TERROHUNT\r\nm_iMissionOrderId=30\r\nm_iMaxPlayers=16\r\nm_iMaxPlayersListenServer=16\r\n", extended);
        Assert.Contains("[GameMode3]\r\nm_eGameMode=GAMEMODE_DEATHMATCH\r\n", extended);
        Assert.Contains("[GameMode4]\r\nm_eGameMode=GAMEMODE_TEAMDEATHMATCH\r\n", extended);
        Assert.Contains("[GameMode5]\r\nm_eGameMode=GAMEMODE_ATTACKANDDEFEND\r\nm_szMissionType=R6ObjectiveExtractionItem\r\nm_iMissionOrderId=30\r\nm_iMaxPlayers=16\r\nm_iMaxPlayersListenServer=16\r\n", extended);
        Assert.Contains("m_eGameMode=GAMEMODE_STORY\r\nm_iMaxPlayers=4\r\n", extended);
        Assert.Contains("[PS3]\r\nm_bCanBeLaunch = TRUE", extended);
        Assert.True(extended.IndexOf("[GameMode5]", StringComparison.Ordinal) < extended.IndexOf("[PS3]", StringComparison.Ordinal));
        Assert.Contains("m_iMissionOrderId=20\r\n\r\n[GameMode2]", extended);
    }

    [Fact]
    public void ExtendUsesTheFilesNewlineStyleAndHandlesAnEndOfFileSection()
    {
        var extended = MapModeExtension.Extend("[GameMode0]\nm_eGameMode=GAMEMODE_STORY\n", 31);

        Assert.DoesNotContain('\r', extended);
        Assert.Contains("m_eGameMode=GAMEMODE_STORY\n\n[GameMode1]\nm_eGameMode=GAMEMODE_TERROHUNT\n", extended);
    }

    [Fact]
    public void ExtendIsDeterministic()
    {
        Assert.Equal(MapModeExtension.Extend(Story, 30), MapModeExtension.Extend(Story, 30));
    }
}
