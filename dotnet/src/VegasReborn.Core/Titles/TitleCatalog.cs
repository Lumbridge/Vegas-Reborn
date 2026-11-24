namespace VegasReborn.Core.Titles;

/// <summary>The original PC games the companion supports.</summary>
public static class TitleCatalog
{
    // Fix ids are shared with tools/companion (native-features.txt). Keep them stable.
    public const string HeadAnimation = "procedural-animation";
    public const string SoloHunt = "solo-terrorist-hunt";
    public const string ReportXp = "report-xp";
    public const string PlayerRank = "player-rank";
    public const string RankedAccess = "ranked-access";
    public const string OcclusionQueries = "occlusion-queries";
    public const string PhysicsLoader = "physics-loader";
    public const string StartupReadiness = "startup-readiness";
    public const string OnlineRoute = "online-route";
    public const string DiscordSignIn = "discord-sign-in";
    public const string ClaimedName = "claimed-name";
    public const string ItemAvailability = "item-availability";
    public const string AppearanceDefaults = "appearance-defaults";
    public const string LeaderboardStatistics = "leaderboard-statistics";
    public const string SoloMatch = "solo-match";
    public const string FullXp = "full-xp";
    public const string SixteenPlayers = "sixteen-players";
    public const string HuntEnemies = "terrorist-hunt-enemies";
    public const string LargeAddressAware = "large-address-aware";
    public const string FireSoundLoop = "fire-sound-loop";
    public const string Controller = "controller";
    public const string PunkBusterRemoved = "punkbuster-removed";
    public const string LeaderboardLabels = "leaderboard-labels";
    public const string PlayerAwards = "player-awards";

    public static GameTitle Vegas1 { get; } = new()
    {
        Key = "vegas1",
        DisplayName = "Rainbow Six Vegas",
        Support = TitleSupport.Supported,
        SteamAppId = 13540,
        DefaultInstallFolder = "E:/SteamLibrary/steamapps/common/Rainbow Six Vegas",
        ClientExecutable = "Binaries/R6Vegas_Game.exe",
        ClientSha256 = "667f659b3c128ab14506a464a462a592e16aca906084cabfc2d890e9e4bd5acc",
        ServerExecutable = "Binaries/R6VegasServer.exe",
        ServerSha256 = "f4388d309da94b010fb23999c09c8ba92270831d0fd2432b78a21299bd6684cb",
        ProfileSubdirectory = "Ubisoft/R6Vegas",
        NativeAdapter = "VegasCompanion-14.dll",
        NativeVersion = 14,
        LaunchArguments = "Menu?GAME=R6Game.R6MenuGameInfo?PB=0 -NoHomeDir",
        TrustKey = TrustKeyKind.AgoraConfigLua,
        RedirectedHosts =
        [
            "gconnect.ubi.com",
            "rainbow6vegas-pc.stun.eu.ubi.com",
            "rainbow6vegas-pc.stun.us.ubi.com",
            "rainbow6vegas-pc.stun.jp.ubi.com",
            "rainbow6vegas-pc.stun.au.ubi.com",
        ],
        RealmPort = 3074,
        SaveSlots = new Dictionary<string, string>
        {
            ["VegasRetailPEC_v1"] = "R6_PECPEC",
            ["VegasRetailEquipment_v1"] = "R6_EquipEquipmentTemplate",
        },
        Fixes =
        [
            new(HeadAnimation, "Head and face animation",
                "Smooth head turns, blinking and eyebrows at any frame rate.",
                "The original head controller ties its gaze, head motion, noise and blinking to the frame rate, so faces twitch on current PCs. This replays each controller's history at a fixed rate.",
                FixSection.GameFixes, FixKind.AlwaysOn),
            new(SoloHunt, "Solo Terrorist Hunt online",
                "Start an online Coop Terrorist Hunt with one player.",
                "The original Coop Terrorist Hunt inherits a two-player launch check. This lets one qualifying player start it. Lobby, ready checks and authority stay as in the original.",
                FixSection.GameFixes, FixKind.AlwaysOn),
            new(SixteenPlayers, "16-player matches",
                "Host Terrorist Hunt and adversarial matches for up to 16 players.",
                "The original limits Terrorist Hunt to 4 players and adversarial matches hosted from the game to 14. This raises both to 16 in the launcher's game copy, including the Terrorist Hunt team size. Story co-op is unchanged.",
                FixSection.GameFixes, FixKind.AlwaysOn),
            new(HuntEnemies, "Terrorist Hunt enemy types",
                "Terrorist Hunt can spawn snipers as well as the original enemy types.",
                "Every Terrorist Hunt map draws each terrorist's main weapon from one shared list that has no sniper rifles. The launcher's game copy adds the SV-98 and PSG1 used by the campaign's snipers, each picked for 5% of terrorists. The other weapons keep their original proportions. The host's game copy decides each terrorist's weapon.",
                FixSection.GameFixes, FixKind.AlwaysOn),
            new(FireSoundLoop, "Stuck gunfire sound",
                "Stop other players' automatic fire sounds from looping after they stop shooting.",
                "Online, the host sends the start and end of another player's automatic fire sound as two messages that can be lost or skipped at a distance. A lost end message leaves the sound looping. When that player has stopped firing and no end arrived, the game's own end sound is played in your game.",
                FixSection.GameFixes, FixKind.AlwaysOn),
            // The adapter reports punkBusterRemovedVerified (Create Match CB_PBServer).
            new(PunkBusterRemoved, "PunkBuster settings removed",
                "Remove the PunkBuster options, match filter, match list column and match details entry and keep PunkBuster off.",
                "PunkBuster's online service no longer exists. This hides the Enable PunkBuster Server checkbox in Create Match (as the game already does in one of its own match screens), the PunkBuster client checkbox in the match browser and Quick Match, the PunkBuster match filter, the PunkBuster icons in the match list, and the PunkBuster entry in match details. It stops the game from starting PunkBuster, turns the client off if it was on, and sets the filter to Any.",
                FixSection.GameFixes, FixKind.AlwaysOn),
            new(ReportXp, "Match XP report",
                "Show the XP earned at the end of a match.",
                "Reads your character's XP before and after the original end-of-match award and shows the difference. It does not change rewards.",
                FixSection.GameFixes, FixKind.AlwaysOn),
            new(LeaderboardLabels, "Leaderboard map labels",
                "Stop the map names under the leaderboards from overlapping.",
                "The PC arrow buttons under each leaderboard keep their placeholder text, which is drawn over the map name the game shows beside them. The launcher's game copy blanks the placeholder text. The arrows still work.",
                FixSection.GameFixes, FixKind.AlwaysOn),
            new(PlayerRank, "Rank in Player Info",
                "Add your rank to the Player Info menu.",
                "Adds a Player Info entry that opens the original Trophy and Service Record panels with your account's rank.",
                FixSection.GameFixes, FixKind.AlwaysOn),
            // The adapter reports nativeAwardsVerified (award manager getter and unlock).
            new(PlayerAwards, "Player awards",
                "Track ribbons, medals, badges and trophies in Player Info.",
                "The PC game never creates its award list and its unlock step does nothing, so Player Info shows 0 OF 0. This creates the game's own awards for your profile, lets the game's own checks unlock them, and saves them on this PC for your account.",
                FixSection.GameFixes, FixKind.AlwaysOn),
            new(RankedAccess, "Ranked match option",
                "Restore the Ranked choice in online menus.",
                "Reconnects the original Ranked option and filter to the menus. Matchmaking messages are unchanged.",
                FixSection.GameFixes, FixKind.AlwaysOn),
            new(Controller, "Controller support",
                "Use current Xbox controllers and Steam Input controllers.",
                "The original game uses its XInput controller support only for the wired Xbox 360 controller's device ID. This identifies every XInput controller as that controller and uses the first connected one.",
                FixSection.GameFixes, FixKind.AlwaysOn),
            new(OcclusionQueries, "Lobby travel hang",
                "Avoid a freeze when moving between lobby and match.",
                "The original waits on Direct3D 9 occlusion queries, which can hang lobby travel on current drivers. This sets the game's own DisableOcclusion switch in the launcher's copy.",
                FixSection.KeptFromCrashing, FixKind.AlwaysOn),
            new(PhysicsLoader, "PhysX loader",
                "Load the PhysX version the game ships with.",
                "The game ships the correct PhysX 2.5.1 core, but its loader otherwise requires an obsolete AGEIA system install. The launcher enables the loader's own attempt to use the copy next to it.",
                FixSection.KeptFromCrashing, FixKind.AlwaysOn),
            new(LargeAddressAware, "4 GB memory limit",
                "Let the game use up to 4 GB of memory.",
                "The original executable is limited to 2 GB of memory and can crash when it runs out. The launcher marks its own copy large-address aware, which raises the limit to 4 GB on 64-bit Windows. The game's code is unchanged and the installed game is not modified.",
                FixSection.KeptFromCrashing, FixKind.AlwaysOn),
            new(StartupReadiness, "Wait for Steam at startup",
                "Wait until Steam has finished preparing the game before changing anything.",
                "Every launcher change is checked against the original code and applied only once Steam has made it executable.",
                FixSection.KeptFromCrashing, FixKind.AlwaysOn),
            new(OnlineRoute, "Revived online service",
                "Connect the game's online menus to the Vegas Reborn service.",
                "The game copy's Ubisoft service addresses lead to the Vegas Reborn service, and its trust key is the service's key. The installed game is not changed.",
                FixSection.OnlineServices, FixKind.AlwaysOn),
            new(DiscordSignIn, "Sign in with Discord",
                "Online Login uses your Discord-linked account.",
                "When you choose Login, the launcher supplies a one-use credential. Your Discord password and token never reach the game.",
                FixSection.OnlineServices, FixKind.AlwaysOn),
            new(ClaimedName, "Player name",
                "Show your claimed player name in game and sessions.",
                "Display only. The account identity used by the service is unchanged.",
                FixSection.OnlineServices, FixKind.AlwaysOn),
            new(ItemAvailability, "Item unlocks",
                "Unlock items from your real XP.",
                "Restores the original item and bundle availability path, which the retail game replaced with a fixed value.",
                FixSection.OnlineServices, FixKind.AlwaysOn),
            new(AppearanceDefaults, "Multiplayer appearance",
                "Keep your character's appearance consistent online.",
                "Applies the game's default appearance policy and saves it with your account.",
                FixSection.OnlineServices, FixKind.AlwaysOn),
            new(LeaderboardStatistics, "Leaderboards",
                "Report match results for the leaderboards.",
                "Uses the original statistics service operations so ranked results reach the leaderboards.",
                FixSection.OnlineServices, FixKind.AlwaysOn),
        ],
    };

    public static GameTitle Vegas2 { get; } = new()
    {
        Key = "vegas2",
        DisplayName = "Rainbow Six Vegas 2",
        Support = TitleSupport.Supported,
        SteamAppId = 15120,
        DefaultInstallFolder = "E:/SteamLibrary/steamapps/common/Rainbow Six Vegas 2",
        ClientExecutable = "Binaries/R6Vegas2_Game.exe",
        ClientSha256 = "e8ab943062b2e66efad9f3de0e693b35068d3359a9b99fdf390d587cc3284383",
        ServerExecutable = "Binaries/RainbowSixVegas2_SADS.exe",
        ServerSha256 = "3da96a5bb3960f68bef241e4299581019b38e1778c8c14651cf1ff7187fa66f6",
        ProfileSubdirectory = "My Games/Ubisoft/R6Vegas2",
        NativeAdapter = "VegasCompanionVegas2.dll",
        NativeVersion = 1,
        LaunchArguments = "",
        TrustKey = TrustKeyKind.SignaturePub,
        RedirectedHosts = ["gconnect.ubi.com", "stun.apac.ubi.com", "stun.emea.ubi.com", "stun.ncsa.ubi.com"],
        RealmPort = 3080,
        // Account slots are added once the Vegas 2 save format is grounded.
        SaveSlots = new Dictionary<string, string>(),
        Fixes =
        [
            // Same controller as Vegas 1 (UR6AnimNodeFrankHeadControl); the adapter reports
            // proceduralAnimationVerified.
            new(HeadAnimation, "Head and face animation",
                "Smooth head turns, blinking and eyebrows at any frame rate.",
                "The original head controller ties its gaze, head motion, noise and blinking to the frame rate, so faces twitch on current PCs. This replays each controller's history at a fixed rate.",
                FixSection.GameFixes, FixKind.AlwaysOn),
            // The adapter reports soloMatchVerified (versus CanLaunchGame predicates).
            new(SoloMatch, "Start matches alone",
                "Start any unranked multiplayer match with one player.",
                "The original versus modes need at least two players, or one per team, before the host can launch. This lets the host launch with one qualifying player. Co-op modes already allow this. Ranked matches keep their original four-player rule.",
                FixSection.GameFixes, FixKind.AlwaysOn),
            // The adapter reports fullXpVerified (online player-count and team-balance factors).
            new(FullXp, "Full match XP",
                "Earn full XP online whatever the player count or team balance.",
                "Online, the original game cuts XP when there are few players or the teams are uneven, down to 1%, and shows the cut as XP MODIFIER in the lobby. This keeps both factors at 100%.",
                FixSection.GameFixes, FixKind.AlwaysOn),
            new(HuntEnemies, "Terrorist Hunt enemy types",
                "Terrorist Hunt can spawn snipers and shield terrorists as well as the original enemy types.",
                "Each Terrorist Hunt map draws each terrorist's main weapon from a list with no sniper rifles or shields. The launcher's game copy adds a campaign sniper rifle and the riot shield used by the campaign's shield terrorists to each list, each picked for 6% of terrorists. The other weapons keep their original proportions. The host's game copy decides each terrorist's weapon.",
                FixSection.GameFixes, FixKind.AlwaysOn),
            // The adapter reports punkBusterRemovedVerified (settings menu row 2).
            new(PunkBusterRemoved, "PunkBuster settings removed",
                "Remove the PunkBuster options, match filter, sort button and server list column and keep PunkBuster off.",
                "PunkBuster's online service no longer exists. This removes the Enable PunkBuster Server and Enable PunkBuster Client rows from the settings menu, runs the game's own routines to turn them off each time the menu opens, and stops the game from turning the client on. It also hides the PunkBuster match filter, sets it to Any, and hides the PunkBuster sort button and column in the server browser.",
                FixSection.GameFixes, FixKind.AlwaysOn),
            // Same Joysticks.ini table as Vegas 1; the adapter reports gamepadVerified.
            new(Controller, "Controller support",
                "Use current Xbox controllers and Steam Input controllers.",
                "The original game uses its XInput controller support only for the wired Xbox 360 controller's device ID. This identifies every XInput controller as that controller and uses the first connected one.",
                FixSection.GameFixes, FixKind.AlwaysOn),
            // Same engine switch and section as Vegas 1 (PCKellerEngine.ini, DefaultEngine.ini;
            // read by the client as DisableOcclusion).
            new(OcclusionQueries, "Lobby travel hang",
                "Avoid a freeze when moving between lobby and match.",
                "The original waits on Direct3D 9 occlusion queries, which can hang lobby travel on current drivers. This sets the game's own DisableOcclusion switch in the launcher's copy.",
                FixSection.KeptFromCrashing, FixKind.AlwaysOn),
            new(StartupReadiness, "Wait for Steam at startup",
                "Wait until Steam has finished preparing the game before changing anything.",
                "Every launcher change is checked against the original code and applied only once Steam has made it executable.",
                FixSection.KeptFromCrashing, FixKind.AlwaysOn),
            new(OnlineRoute, "Revived online service",
                "Connect the game's online menus to the Vegas Reborn service.",
                "The game copy's Ubisoft service addresses lead to the Vegas Reborn service, and its signature.pub is the service's key. The installed game is not changed.",
                FixSection.OnlineServices, FixKind.AlwaysOn),
            // The adapter reports discordSignInVerified.
            new(DiscordSignIn, "Sign in with Discord",
                "Online Login uses your Discord-linked account.",
                "When you choose Login, the launcher supplies a one-use credential. Your Discord password and token never reach the game.",
                FixSection.OnlineServices, FixKind.AlwaysOn),
            // The adapter reports claimedNameVerified (online host and join URL name).
            new(ClaimedName, "Player name",
                "Show your claimed player name in game and sessions.",
                "Display only. The account identity used by the service is unchanged.",
                FixSection.OnlineServices, FixKind.AlwaysOn),
        ],
    };

    public static IReadOnlyList<GameTitle> All { get; } = [Vegas1, Vegas2];

    public static GameTitle ByKey(string key) =>
        All.FirstOrDefault(title => title.Key == key) ?? throw new ArgumentException($"Unknown title '{key}'", nameof(key));
}
