using System.Text;
using System.Text.Json.Nodes;
using VegasReborn.Core.Display;
using VegasReborn.Core.Launch;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Tests.Launch;

/// <summary>Launch lifecycle with fake Steam, gate, injector and adapter; no retail launch.</summary>
public sealed class GameLauncherTests : IDisposable
{
    private static readonly byte[] Vegas1Exe = Synthetic.PeImage("synthetic vegas 1 client");
    private const string MissionIni = "[R6Game.R6MissionConfig]\r\nm_iId=5\r\n\r\n[GameMode0]\r\nm_eGameMode=GAMEMODE_TEAMDEATHMATCH\r\nm_iMaxPlayers=16\r\nm_iMaxPlayersListenServer=14\r\n\r\n"
        + "[GameMode1]\r\nm_eGameMode=GAMEMODE_TERROHUNT\r\nm_iMaxPlayers=4\r\nm_iMissionOrderId=2\r\n";
    private static readonly byte[] Vegas2Exe = Encoding.ASCII.GetBytes("synthetic vegas 2 client");
    private static readonly byte[] OriginalSignature = Encoding.ASCII.GetBytes("0 synthetic original signature.pub");
    private const string AgoraConfig = "-- Agora\r\nUbiSigKeyPub = \"original.der\";\r\nOther = 1;\r\n";
    private const string EngineIni = "[URL]\r\nMap=Entry.rmpc\r\nLocalMap=Entry.rmpc\r\nMapExt=rmpc2\r\n[D3DDrv.D3DRenderDevice]\r\nDisableOcclusion=False\r\n"
        + "[WinDrv.WindowsClient]\r\nStartupFullscreen=True\r\nStartupResolutionX=800\r\nStartupResolutionY=600\r\n";

    private readonly TestDirectory _root = new();
    private readonly AppPaths _paths;
    private readonly FakeCredentials _credentials = new();
    private readonly FakeSync _sync;
    private readonly FakeSystem _system = new();
    private readonly FakeDesktop _desktop = new();
    private readonly Messages _messages = new();
    private readonly string _install;

    public GameLauncherTests()
    {
        _paths = new AppPaths(_root.Path);
        _sync = new FakeSync(Path.Combine(_paths.Local, "accounts"));
        _install = Path.Combine(_root.Path, "install");
        foreach (var name in NativeComponents.Required(TitleCatalog.Vegas1).Concat(NativeComponents.Required(TitleCatalog.Vegas2)))
        {
            _root.File(Path.Combine("local/companion/bin", name), "authored fixture");
        }
        _root.Bytes("local/companion/root-public.der", Synthetic.KeyDer);
        _root.File("local/companion/service.json",
            "{\"version\": 1, \"nativeLicenseId\": \"" + new string('a', 48) + "\", \"host\": \"vegas.lumbridge.co.uk\", \"rootPublicSha256\": \""
            + Synthetic.Sha(Synthetic.KeyDer) + "\", \"port\": 3074}");
        _system.Cache = Path.Combine(_root.Path, "cache");
    }

    public void Dispose() => _root.Dispose();

    private GameTitle Vegas1 => TitleCatalog.Vegas1 with { ClientSha256 = Synthetic.Sha(Vegas1Exe) };

    private GameTitle Vegas2 => TitleCatalog.Vegas2 with { ClientSha256 = Synthetic.Sha(Vegas2Exe) };

    private void InstallVegas1()
    {
        _root.Bytes("install/Binaries/R6Vegas_Game.exe", Vegas1Exe);
        _root.File("install/Binaries/PhysXLoader.dll", "loader");
        _root.File("install/Binaries/PhysXCore.dll", "core");
        _root.File("install/Binaries/session/state.bin", "excluded");
        _root.File("install/Binaries/Launch.log", "excluded");
        _root.File("install/KellerGame/Config/PC/AgoraConfig.lua", AgoraConfig);
        _root.File("install/KellerGame/Config/PCKellerEngine.ini", EngineIni);
        _root.File("install/KellerGame/Config/MovieLoading.ini", "[INTRO]\r\nEnable=TRUE\r\n");
        _root.File("install/KellerGame/Config/PC/MovieLoading.ini", "[INTRO]\r\nEnable=TRUE\r\n");
        _root.File("install/KellerGame/Content/CookedPc/Maps/MP_Casino/MP_Casino_01.ini", MissionIni);
    }

    private void InstallVegas2()
    {
        _root.Bytes("install/Binaries/R6Vegas2_Game.exe", Vegas2Exe);
        _root.Bytes("install/KellerGame/Config/PC/signature.pub", OriginalSignature);
        _root.File("install/KellerGame/Config/PC/PlayerProfile.ini", "[CDKEY]\r\n\r\n");
        _root.File("install/KellerGame/Config/PCKellerEngine.ini", EngineIni);
        _root.File("install/KellerGame/Config/MovieLoading.ini", "[INTRO]\r\nEnable=TRUE\r\n");
    }

    private GameLauncher Launcher(Func<GameTitle, TitleLaunchProfile>? profiles = null)
    {
        profiles ??= title => title.Key == "vegas1"
            ? TitleLaunchProfile.For(title) with
            {
                PinnedFiles = new Dictionary<string, string>
                {
                    ["Binaries/PhysXLoader.dll"] = Synthetic.Sha("loader"u8.ToArray()),
                    ["Binaries/PhysXCore.dll"] = Synthetic.Sha("core"u8.ToArray()),
                },
            }
            : TitleLaunchProfile.For(title) with { OriginalSignatureSha256 = Synthetic.Sha(OriginalSignature) };
        return new GameLauncher(_paths, ServiceConfiguration.Load(_paths), _credentials, _sync, _system, () => _desktop, new FakeCarrier(), profiles);
    }

    private LaunchRequest Request(GameTitle title, DisplayMode mode = DisplayMode.GameDefault, bool skipIntros = false,
        IReadOnlySet<string>? disabled = null, Action? started = null, int fieldOfView = 0) => new()
    {
        Title = title,
        GameFolder = _install,
        DisplayMode = mode,
        SkipIntros = skipIntros,
        FieldOfView = fieldOfView,
        DisabledFixes = disabled ?? new HashSet<string>(),
        OnStarted = started,
    };

    private string Output(GameTitle title) => _sync.AccountDirectory(title);

    [Fact]
    public async Task Vegas1LaunchPreparesCopyRoutesAndSavesAfterExit()
    {
        InstallVegas1();
        var title = Vegas1;
        var output = Output(title);
        _system.Output = output;
        _system.Status = Reports.Vegas1Ready;
        _system.ExitAfterWaits = 2;
        _root.File(Path.Combine(output, "profile/Ubisoft/R6Vegas/R6_PECPEC"), "synthetic character");
        var started = 0;
        var launcher = Launcher();

        var outcome = await launcher.PlayAsync(Request(title, started: () => started++), _messages);

        Assert.True(outcome.CharacterSaved);
        Assert.False(launcher.IsRunning);
        Assert.Equal(1, started);
        Assert.Equal((1, 1), (_sync.Pulls, _sync.Pushes));
        var game = _paths.GameCopy("vegas1");
        Assert.Equal(Path.Combine(_paths.CompanionConfig, "game"), game);
        Assert.Equal("{\"version\": 1, \"sourceSha256\": \"" + title.ClientSha256 + "\"}", File.ReadAllText(Path.Combine(game, ".companion-ready.json")));
        Assert.False(Directory.Exists(Path.Combine(game, "Binaries/session")));
        Assert.False(File.Exists(Path.Combine(game, "Binaries/Launch.log")));
        // Trust key and ini repairs only in the copy.
        var keyPath = _paths.RootPublicKey.Replace('\\', '/');
        Assert.Equal("-- Agora\nUbiSigKeyPub = \"" + keyPath + "\";\nOther = 1;\n",
            File.ReadAllText(Path.Combine(game, "KellerGame/Config/PC/AgoraConfig.lua")));
        Assert.Equal(AgoraConfig, File.ReadAllText(Path.Combine(_install, "KellerGame/Config/PC/AgoraConfig.lua")));
        var engine = File.ReadAllText(Path.Combine(game, "KellerGame/Config/PCKellerEngine.ini"));
        Assert.Contains("[URL]\nMap=Menu.rmpc\nLocalMap=Menu.rmpc\nMapExt=rmpc\n", engine);
        Assert.Contains("DisableOcclusion=True\n", engine);
        Assert.Equal(EngineIni, File.ReadAllText(Path.Combine(_install, "KellerGame/Config/PCKellerEngine.ini")));
        // 16-player limits and the 4 GB flag only in the copy.
        Assert.Equal(MissionIni.Replace("ListenServer=14", "ListenServer=16")
                .Replace("m_iMaxPlayers=4\r\n", "m_iMaxPlayers=16\r\nm_iMaxPlayersListenServer=16\r\n"),
            File.ReadAllText(Path.Combine(game, "KellerGame/Content/CookedPc/Maps/MP_Casino/MP_Casino_01.ini")));
        Assert.Equal(MissionIni, File.ReadAllText(Path.Combine(_install, "KellerGame/Content/CookedPc/Maps/MP_Casino/MP_Casino_01.ini")));
        Assert.True(LargeAddressAware.IsSet(File.ReadAllBytes(Path.Combine(game, "Binaries/R6Vegas_Game.exe"))));
        Assert.Equal(Vegas1Exe, File.ReadAllBytes(Path.Combine(_install, "Binaries/R6Vegas_Game.exe")));
        // Native output folder.
        Assert.Equal("[CDKEY]\nKEYID=" + new string('a', 48) + "\n", File.ReadAllText(Path.Combine(output, "profile/Ubisoft/R6Vegas/PlayerProfile.ini")));
        Assert.Equal("native\n", File.ReadAllText(Path.Combine(output, "display-mode.txt")));
        Assert.Equal("play\n", File.ReadAllText(Path.Combine(output, "skip-intros.txt")));
        Assert.Equal("default\n", File.ReadAllText(Path.Combine(output, "field-of-view.txt")));
        Assert.Equal("", File.ReadAllText(Path.Combine(output, "native-features.txt")));
        Assert.False(File.Exists(Path.Combine(output, "claimed-name.txt")));
        var route = File.ReadAllText(Path.Combine(output, "native-route.txt")).Split('\n');
        Assert.Equal("203.0.113.5", route[0]);
        Assert.Equal("3074", route[1]);
        Assert.True(int.Parse(route[2]) > 0);
        Assert.Matches("^[0-9a-f]{64}$", route[3]);
        Assert.Equal("d0123456789abcd", route[4]);
        Assert.Equal(StatisticsCarrier.Codec, Assert.Single(_credentials.Mints).StatisticsCodec);
        Assert.Equal(Path.Combine(_install, "Binaries", "R6Vegas_Game.exe") + "\n" + Path.Combine(game, "Binaries") + "\nwinsta0\\Default\n" + output,
            Encoding.Unicode.GetString(File.ReadAllBytes(Path.Combine(output, "launch.utf16"))));
        // Gate, Steam, injector, gate stop.
        Assert.Equal(["-applaunch", "13540", "Menu?GAME=R6Game.R6MenuGameInfo?PB=0", "-NoHomeDir"], Assert.Single(_system.SteamLaunches));
        Assert.Equal(3, _system.Components.Count);
        Assert.Equal("--configure", _system.Components[0][1]);
        Assert.Equal("4321", _system.Components[0][2]);
        Assert.StartsWith(_system.Cache, _system.Components[0][3]);
        Assert.Equal(["42", "7", Path.Combine(_paths.NativeBin, "VegasCompanion-14.dll"), output, game, _install], _system.Components[1].Skip(1));
        Assert.Equal(["--stop-gate", "4321", "140703128616960"], _system.Components[2].Skip(1).Take(3));
        Assert.Empty(_system.Terminated);
        Assert.Contains("Game running. Choose Multiplayer > Online and click Login. Sign-in is automatic.", _messages.Items);
        Assert.Equal("Character saved to your account.", _messages.Items[^1]);
    }

    [Fact]
    public async Task StaleDisableRequestsAreIgnoredForAlwaysOnFixes()
    {
        InstallVegas1();
        var title = Vegas1;
        _system.Output = Output(title);
        _system.Status = Reports.Vegas1Ready;
        var disabled = new HashSet<string> { TitleCatalog.ReportXp, TitleCatalog.OcclusionQueries, TitleCatalog.OnlineRoute, "unknown" };

        var outcome = await Launcher().PlayAsync(Request(title, disabled: disabled), _messages);

        Assert.False(outcome.CharacterSaved);
        Assert.Equal("Game closed. No character was created.", _messages.Items[^1]);
        Assert.Equal("", File.ReadAllText(Path.Combine(Output(title), "native-features.txt")));
        Assert.Contains("DisableOcclusion=True\n", File.ReadAllText(Path.Combine(_paths.GameCopy("vegas1"), "KellerGame/Config/PCKellerEngine.ini")));
    }

    [Fact]
    public async Task ClaimedNameAndDisplayOptionsAreWrittenAndVerified()
    {
        InstallVegas1();
        var title = Vegas1;
        _credentials.Account = new LaunchAccount("199999999999999999", "Operator", true, false);
        _system.Output = Output(title);
        _system.Status = () =>
        {
            var report = Reports.Vegas1Ready();
            report["claimedNameVerified"] = true;
            report["claimedSessionNameVerified"] = true;
            report["displayStyleVerified"] = true;
            return report;
        };
        _system.LaunchOptions = new JsonObject
        {
            ["viewportHookInstalled"] = true, ["deviceVerified"] = true, ["actualWindowed"] = true, ["introSequenceHookInstalled"] = true,
        };
        _system.ExitAfterWaits = 2;

        await Launcher().PlayAsync(Request(title, DisplayMode.Borderless, skipIntros: true), _messages);

        Assert.Equal("Operator\n", File.ReadAllText(Path.Combine(Output(title), "claimed-name.txt")));
        Assert.Equal("borderless\n", File.ReadAllText(Path.Combine(Output(title), "display-mode.txt")));
        Assert.Equal("skip\n", File.ReadAllText(Path.Combine(Output(title), "skip-intros.txt")));
        Assert.Contains("Verifying the game's requested display and intro settings...", _messages.Items);
        Assert.Equal(["borderless:42", "borderless:42"], _desktop.Calls);
        var engine = File.ReadAllText(Path.Combine(_paths.GameCopy("vegas1"), "KellerGame/Config/PCKellerEngine.ini"));
        Assert.Contains("StartupResolutionX=1920\nStartupResolutionY=1080\n", engine);
        Assert.Contains("[INTRO]\r\nEnable=FALSE", File.ReadAllText(Path.Combine(_paths.GameCopy("vegas1"), "KellerGame/Config/MovieLoading.ini")));
    }

    [Fact]
    public async Task FieldOfViewIsWrittenAndVerified()
    {
        InstallVegas2();
        var title = Vegas2;
        _credentials.Supported = false;
        _system.Output = Output(title);
        _system.Status = () =>
        {
            var report = Reports.Vegas2Ready();
            report["fieldOfViewRequested"] = 80;
            report["fieldOfViewVerified"] = true;
            return report;
        };

        await Launcher().PlayAsync(Request(title, fieldOfView: 80), _messages);

        Assert.Equal("80\n", File.ReadAllText(Path.Combine(Output(title), "field-of-view.txt")));
    }

    [Fact]
    public async Task UnverifiedFieldOfViewStopsTheLaunch()
    {
        InstallVegas2();
        var title = Vegas2;
        _credentials.Supported = false;
        _system.Output = Output(title);
        _system.Status = Reports.Vegas2Ready;

        var error = await Assert.ThrowsAsync<LaunchException>(() => Launcher().PlayAsync(Request(title, fieldOfView: 80), _messages));

        Assert.Contains("Field of view", error.Message);
    }

    [Fact]
    public async Task Vegas2LaunchSwapsSignatureKeyAndUsesItsRealmPort()
    {
        InstallVegas2();
        var title = Vegas2;
        _credentials.Supported = false;
        _system.Output = Output(title);
        _system.Status = Reports.Vegas2Ready;

        var outcome = await Launcher().PlayAsync(Request(title, skipIntros: true), _messages);

        Assert.False(outcome.CharacterSaved);
        Assert.Equal((0, 0), (_sync.Pulls, _sync.Pushes));
        Assert.Empty(_credentials.Mints);
        var game = _paths.GameCopy("vegas2");
        Assert.Equal(Path.Combine(_paths.Local, "vegas2", "game"), game);
        Assert.Contains("\"title\": \"vegas2\"", File.ReadAllText(Path.Combine(game, ".companion-ready.json")));
        Assert.Equal(Synthetic.KeyDer, File.ReadAllBytes(Path.Combine(game, "KellerGame/Config/PC/signature.pub")));
        Assert.Equal(OriginalSignature, File.ReadAllBytes(Path.Combine(_install, "KellerGame/Config/PC/signature.pub")));
        // The license is already the encoded KEYID (KeyIdCodec).
        var cdKey = "[CDKEY]\nKEYID=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n";
        Assert.Equal(cdKey, File.ReadAllText(Path.Combine(game, "KellerGame/Config/PC/PlayerProfile.ini")));
        Assert.Equal(cdKey, File.ReadAllText(Path.Combine(Output(title), "profile/My Games/Ubisoft/R6Vegas2/PlayerProfile.ini")));
        Assert.Equal("[CDKEY]\r\n\r\n", File.ReadAllText(Path.Combine(_install, "KellerGame/Config/PC/PlayerProfile.ini")));
        // No Vegas 1 copy repairs or native option files for the generic adapter.
        // Vegas 2 gets only the lobby travel switch; its [URL] start map is left as shipped.
        Assert.Equal(EngineIni.Replace("\r\n", "\n").Replace("DisableOcclusion=False", "DisableOcclusion=True"),
            File.ReadAllText(Path.Combine(game, "KellerGame/Config/PCKellerEngine.ini")));
        Assert.False(File.Exists(Path.Combine(Output(title), "native-features.txt")));
        Assert.Equal("native\n", File.ReadAllText(Path.Combine(Output(title), "display-mode.txt")));
        Assert.DoesNotContain("Verifying the game's requested display and intro settings...", _messages.Items);
        Assert.Contains("Enable=FALSE", File.ReadAllText(Path.Combine(game, "KellerGame/Config/MovieLoading.ini")));
        var route = File.ReadAllText(Path.Combine(Output(title), "native-route.txt")).Split('\n');
        Assert.Equal(["203.0.113.5", "3080", "1"], route.Take(3));
        Assert.Matches("^v[0-9a-f]{14}$", route[4]);
        Assert.Equal(["-applaunch", "15120"], Assert.Single(_system.SteamLaunches));
        Assert.Equal(Path.Combine(_paths.NativeBin, "VegasCompanionVegas2.dll"), _system.Components[1][3]);
        Assert.Contains("Game running. Choose Login from the main menu and click Login. Sign-in is automatic.", _messages.Items);
        Assert.DoesNotContain("being tested", _messages.ToString());
        Assert.Equal("Game closed.", _messages.Items[^1]);
        // A second launch accepts the already-installed key.
        _system.SteamLaunches.Clear();
        await Launcher().PlayAsync(Request(title), _messages);
        Assert.Single(_system.SteamLaunches);
    }

    [Fact]
    public async Task Vegas2BorderlessWaitsForWindowedDeviceAndFitsMonitor()
    {
        InstallVegas2();
        var title = Vegas2;
        _system.Output = Output(title);
        _system.Status = () =>
        {
            var report = Reports.Vegas2Ready();
            report["displayViewportVerified"] = true;
            report["displayStyleVerified"] = true;
            return report;
        };
        // The Vegas 2 intro hook is reported in compat-status.json, not here.
        _system.LaunchOptions = new JsonObject
        {
            ["viewportHookInstalled"] = true, ["deviceVerified"] = true, ["actualWindowed"] = true,
        };
        _system.ExitAfterWaits = 2;

        await Launcher().PlayAsync(Request(title, DisplayMode.Borderless, skipIntros: true), _messages);

        Assert.Equal("borderless\n", File.ReadAllText(Path.Combine(Output(title), "display-mode.txt")));
        Assert.Contains("Verifying the game's requested display and intro settings...", _messages.Items);
        Assert.Equal(["borderless:42", "borderless:42"], _desktop.Calls);
        Assert.Contains("StartupFullscreen=False",
            File.ReadAllText(Path.Combine(_paths.GameCopy("vegas2"), "KellerGame/Config/PCKellerEngine.ini")));
    }

    [Fact]
    public async Task Vegas2UsesAccountLeasesWhenTheServiceSupportsThem()
    {
        InstallVegas2();
        var title = Vegas2;
        _system.Output = Output(title);
        _system.Status = Reports.Vegas2Ready;
        await Launcher().PlayAsync(Request(title), _messages);
        Assert.Equal("vegas2", Assert.Single(_credentials.Mints).Title.Key);
        Assert.Equal("", _credentials.Mints[0].StatisticsCodec);
        Assert.Equal("d0123456789abcd", File.ReadAllText(Path.Combine(Output(title), "native-route.txt")).Split('\n')[4]);
    }

    [Fact]
    public async Task ReplacedSignatureInCopyIsRejected()
    {
        InstallVegas2();
        var title = Vegas2;
        _system.Output = Output(title);
        _root.File("install/KellerGame/Config/PC/signature.pub", "0 someone else's key");
        var error = await Assert.ThrowsAsync<LaunchException>(() => Launcher().PlayAsync(Request(title), _messages));
        Assert.Equal("The copied game's trust configuration is unsupported", error.Message);
        Assert.Empty(_system.SteamLaunches);
    }

    [Fact]
    public async Task FailedReadinessTerminatesOwnedChildAndReleasesLauncher()
    {
        InstallVegas1();
        var title = Vegas1;
        _system.Output = Output(title);
        _system.Status = () => new JsonObject { ["ready"] = false, ["signatures"] = 8191 };
        var launcher = Launcher();
        var error = await Assert.ThrowsAsync<LaunchException>(() => launcher.PlayAsync(Request(title), _messages));
        Assert.Contains("does not match", error.Message);
        Assert.Equal([42], _system.Terminated);
        Assert.Equal(3, _system.Components.Count);
        Assert.False(launcher.IsRunning);
        Assert.Equal(0, _sync.Pushes);
    }

    [Fact]
    public async Task SteamTimeoutStopsGateWithoutTerminating()
    {
        InstallVegas1();
        var title = Vegas1;
        _system.Output = Output(title);
        _system.StartChild = false;
        var error = await Assert.ThrowsAsync<LaunchException>(() => Launcher().PlayAsync(Request(title), _messages));
        Assert.Equal("Steam did not start the game", error.Message);
        Assert.Empty(_system.Terminated);
        Assert.Equal(["--stop-gate", "4321", "140703128616960"], _system.Components[^1].Skip(1).Take(3));
    }

    [Fact]
    public async Task PreconditionsAreReportedBeforeAnyLaunch()
    {
        InstallVegas1();
        var title = Vegas1;
        _system.Output = Output(title);
        _system.Running = true;
        Assert.Equal("Close the running retail game before launching another",
            (await Assert.ThrowsAsync<LaunchException>(() => Launcher().PlayAsync(Request(title)))).Message);
        _system.Running = false;
        _system.MutexHeld = true;
        Assert.Equal("Another launcher is already launching the game",
            (await Assert.ThrowsAsync<LaunchException>(() => Launcher().PlayAsync(Request(title)))).Message);
        _system.MutexHeld = false;
        _credentials.Account = new LaunchAccount("199999999999999999", null, true, true);
        Assert.Equal("Choose your player name in the launcher before playing",
            (await Assert.ThrowsAsync<LaunchException>(() => Launcher().PlayAsync(Request(title)))).Message);
        _credentials.Account = new LaunchAccount("199999999999999999");
        _system.SteamPid = null;
        Assert.Equal("Start Steam and sign in before playing",
            (await Assert.ThrowsAsync<LaunchException>(() => Launcher().PlayAsync(Request(title)))).Message);
        Assert.Empty(_system.SteamLaunches);
        Assert.Empty(_system.Components);
    }

    [Fact]
    public async Task UnsupportedVersionAndMissingGameAreReported()
    {
        _root.Bytes("install/Binaries/R6Vegas_Game.exe", "different build"u8.ToArray());
        Assert.Equal("This game version is not supported by the compatibility module",
            (await Assert.ThrowsAsync<LaunchException>(() => Launcher().PlayAsync(Request(Vegas1)))).Message);
        Assert.Contains("was not found",
            (await Assert.ThrowsAsync<LaunchException>(() => Launcher().PlayAsync(Request(Vegas2)))).Message);
        Assert.False(Directory.Exists(_paths.GameCopy("vegas1")));
    }

    [Fact]
    public async Task UnfinishedCopyIsPreserved()
    {
        InstallVegas1();
        _root.File("local/companion/game/partial.bin", "partial");
        Assert.Equal("An unfinished game copy exists; preserve it before preparing another",
            (await Assert.ThrowsAsync<LaunchException>(() => Launcher().PlayAsync(Request(Vegas1)))).Message);
        Assert.True(File.Exists(Path.Combine(_paths.GameCopy("vegas1"), "partial.bin")));
    }

    [Fact]
    public async Task CancellationDuringStartupStopsGate()
    {
        InstallVegas1();
        var title = Vegas1;
        _system.Output = Output(title);
        _system.StartChild = false;
        using var cancel = new CancellationTokenSource();
        _system.OnStartSteam = cancel.Cancel;
        var launcher = Launcher();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launcher.PlayAsync(Request(title), _messages, cancel.Token));
        Assert.Equal("--stop-gate", _system.Components[^1][1]);
        Assert.False(launcher.IsRunning);
    }
}
