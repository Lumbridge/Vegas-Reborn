using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using VegasReborn.Core.Content;
using VegasReborn.Core.Dedicated;
using VegasReborn.Core.Launch;
using VegasReborn.Core.Tests.Content;
using VegasReborn.Core.Tests.Launch;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Tests.Dedicated;

public sealed class DedicatedSettingsTests
{
    private static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void TemplateRoundTripsWithDefaults()
    {
        var settings = DedicatedSettings.Parse(Json(DedicatedSettings.Template(TitleCatalog.Vegas1, @"C:\Games\Rainbow Six Vegas")));
        Assert.Equal(TitleCatalog.Vegas1, settings.Title);
        Assert.Equal(@"C:\Games\Rainbow Six Vegas", settings.GameFolder);
        Assert.Equal("MP_Streets_01", settings.Map);
        Assert.Equal("R6Game.R6TeamDeathmatchGame", settings.GameType);
        Assert.Equal(16, settings.MaxPlayers);
        Assert.False(settings.FriendlyFire);
        Assert.Equal("", settings.Password);
    }

    [Theory]
    [InlineData("""{"version": 1, "game": "vegas1", "gameFolder": "C:\\G", "extra": 1}""", "unknown setting: extra")]
    [InlineData("""{"version": 2, "game": "vegas1", "gameFolder": "C:\\G"}""", "version must be 1")]
    [InlineData("""{"version": 1, "game": "vegas3", "gameFolder": "C:\\G"}""", "game to vegas1 or vegas2")]
    [InlineData("""{"version": 1, "game": "vegas1", "gameFolder": "relative"}""", "full path")]
    [InlineData("""{"version": 1, "game": "vegas1", "gameFolder": "C:\\G", "map": "MP?PW=x"}""", "map must be")]
    [InlineData("""{"version": 1, "game": "vegas1", "gameFolder": "C:\\G", "gameType": "Game?X=1"}""", "gameType must be")]
    [InlineData("""{"version": 1, "game": "vegas1", "gameFolder": "C:\\G", "maxPlayers": 17}""", "between 2 and 16")]
    [InlineData("""{"version": 1, "game": "vegas1", "gameFolder": "C:\\G", "maxPlayers": "8"}""", "whole number")]
    [InlineData("""{"version": 1, "game": "vegas1", "gameFolder": "C:\\G", "password": "a?b"}""", "letters and digits")]
    [InlineData("""{"version": 1, "game": "vegas1", "gameFolder": "C:\\G", "friendlyFire": 1}""", "true or false")]
    [InlineData("""[1]""", "JSON object")]
    [InlineData("""{""", "not valid JSON")]
    public void RejectsInvalidSettingsWithPlainMessages(string json, string expected)
    {
        var error = Assert.Throws<DedicatedException>(() => DedicatedSettings.Parse(Json(json)));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void MissingFileNamesInit()
    {
        var error = Assert.Throws<DedicatedException>(() => DedicatedSettings.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "server.json")));
        Assert.Contains("VegasServer init", error.Message);
    }
}

public sealed class Vegas1DedicatedAdapterTests
{
    private static DedicatedSettings Settings => new() { Title = TitleCatalog.Vegas1, GameFolder = @"C:\G" };

    [Fact]
    public void LocalTestMatchesTheOriginalTemplate()
    {
        // The command line src/vegas_reborn/dedicated.py starts for a LAN run.
        Assert.Equal(
            [
                "TEXTSERVER",
                "MP_Streets_01?AgO=0?AgU=?AgP=?SrvOptionFile=R6VegasDedicatedLAN?RG=0?RC=3?TBR=0?PB=0?GAME=R6Game.R6TeamDeathmatchGame?TKPM=5?FF=0?TL=180?MP=16?AR=1?ML=3?TR=0?GPL=0?LT=0?TKP=1?TB=5?GD=2?PW=",
                "MULTIHOME=127.0.0.3",
            ],
            new Vegas1DedicatedAdapter().Arguments(Settings, null, localTest: true));
    }

    [Fact]
    public void OnlineUsesTheDiscordHostAliasAndRoutedInterface()
    {
        var arguments = new Vegas1DedicatedAdapter().Arguments(
            Settings with { Map = "MP_Casino_01", MaxPlayers = 8, FriendlyFire = true, Password = "abc1" }, "h0123456789abcd", localTest: false);
        Assert.Equal(2, arguments.Count);
        Assert.StartsWith("MP_Casino_01?AgO=1?AgU=h0123456789abcd?AgP=UseDiscord?SrvOptionFile=R6VegasDedicatedLAN?", arguments[1]);
        Assert.Contains("?FF=1?", arguments[1]);
        Assert.Contains("?MP=8?", arguments[1]);
        Assert.EndsWith("?PW=abc1", arguments[1]);
    }

    [Fact]
    public void RejectsAnUnexpectedAlias() =>
        Assert.Throws<DedicatedException>(() => new Vegas1DedicatedAdapter().Arguments(Settings, "d0123456789abcd?X=1", localTest: false));

    [Fact]
    public void ReadinessNeedsEveryVerifiedFlag()
    {
        var adapter = new Vegas1DedicatedAdapter();
        adapter.ValidateReady(Ready());
        foreach (var name in new[] { "ready", "nativeStatisticsVerified", "nativeRankedHostVerified", "nativeSoloHuntVerified" })
        {
            var status = Ready();
            status[name] = false;
            Assert.Throws<DedicatedException>(() => adapter.ValidateReady(status));
        }
        var version = Ready();
        version["nativeVersion"] = 3;
        Assert.Throws<DedicatedException>(() => adapter.ValidateReady(version));
    }

    [Fact]
    public void OptionsCopyTurnsPunkBusterOffAndKeepsTheShippedFile()
    {
        using var directory = new TestDirectory();
        var shipped = directory.File("source/" + Vegas1DedicatedAdapter.ShippedOptions, "[Engine]\r\nm_bPunkBusterSv=True\r\nOther=1\r\n");
        new Vegas1DedicatedAdapter().PrepareCopy(Path.Combine(directory.Path, "source"), Path.Combine(directory.Path, "game"));
        Assert.Equal("[Engine]\r\nm_bPunkBusterSv=True\r\nOther=1\r\n", File.ReadAllText(shipped));
        Assert.Equal("[Engine]\r\nm_bPunkBusterSv=False\r\nOther=1\r\n",
            File.ReadAllText(Path.Combine(directory.Path, "game/KellerGame/Config/PC/R6VegasDedicatedLAN.ini")));
    }

    [Fact]
    public void VegasTwoIsNotAvailableYet() =>
        Assert.Contains("not available", Assert.Throws<DedicatedException>(() => DedicatedAdapters.For(TitleCatalog.Vegas2)).Message);

    internal static JsonObject Ready() => new()
    {
        ["ready"] = true, ["nativeVersion"] = 4, ["nativeStatisticsVerified"] = true,
        ["nativeRankedHostVerified"] = true, ["nativeSoloHuntVerified"] = true,
    };
}

public sealed class CommandLineTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData(@"C:\Program Files\R6\R6VegasServer.exe", "\"C:\\Program Files\\R6\\R6VegasServer.exe\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData(@"ends in space\ ", "\"ends in space\\ \"")]
    [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
    public void QuotesAsCommandLineToArgvParses(string argument, string expected) => Assert.Equal(expected, CommandLine.Quote(argument));

    [Fact]
    public void ReceiptRedactsTheServerPassword()
    {
        Assert.Equal("MP?PW=<set>", DedicatedHost.RedactPassword("MP?PW=secret"));
        Assert.Equal("MP?PW=", DedicatedHost.RedactPassword("MP?PW="));
        Assert.Equal("TEXTSERVER", DedicatedHost.RedactPassword("TEXTSERVER"));
    }
}

public sealed class DedicatedHostTests : IDisposable
{
    private readonly TestDirectory _directory = new();
    private readonly AppPaths _paths;
    private readonly string _source;
    private readonly FakeSystem _system = new();

    public DedicatedHostTests()
    {
        _paths = new AppPaths(Path.Combine(_directory.Path, "install"));
        _source = Path.Combine(_directory.Path, "Rainbow Six Vegas");
        _directory.File("Rainbow Six Vegas/Binaries/R6VegasServer.exe", "server");
        _directory.File("Rainbow Six Vegas/Binaries/Ignored.log", "log");
        _directory.File("Rainbow Six Vegas/" + Vegas1DedicatedAdapter.ShippedOptions, "m_bPunkBusterSv=True\n");
        _directory.File("Rainbow Six Vegas/KellerGame/Config/PC/AgoraConfig.lua", "UbiSigKeyPub = \"original\";\n");
        _directory.File("install/local/companion/bin/VegasInjector.exe", "injector");
        _directory.File("install/local/companion/bin/VegasDedicated-4.dll", "module");
        _directory.Bytes("install/local/companion/root-public.der", Synthetic.KeyDer);
    }

    private DedicatedHost Host() => new(_paths,
        () => new ServiceConfiguration(ServiceConfiguration.ServiceHost, 3074, new string('a', 48), Synthetic.Sha(Synthetic.KeyDer), _paths.RootPublicKey),
        _system, _ => new TestAdapter());

    private DedicatedRunRequest Request(ICredentialSource? credentials) => new()
    {
        Settings = new DedicatedSettings { Title = TitleCatalog.Vegas1, GameFolder = _source },
        Credentials = credentials,
        OnStarted = _ => DedicatedHost.RequestStop(_paths, TitleCatalog.Vegas1),
    };

    [Fact]
    public async Task LocalTestRunsFromItsOwnCopyUntilStopped()
    {
        var result = await Host().RunAsync(Request(null));

        var game = Path.GetFullPath(Path.Combine(_paths.Local, "dedicated", "vegas1", "game"));
        Assert.True(File.Exists(Path.Combine(game, "Binaries/R6VegasServer.exe")));
        Assert.False(File.Exists(Path.Combine(game, "Binaries/Ignored.log")));
        Assert.Equal("UbiSigKeyPub = \"original\";\n", File.ReadAllText(Path.Combine(game, "KellerGame/Config/PC/AgoraConfig.lua")));
        Assert.Equal(Path.Combine(game, "Binaries/R6VegasServer.exe"), _system.Started!.Executable);
        Assert.Equal("MULTIHOME=127.0.0.3", _system.Started.Arguments[^1]);
        Assert.Equal(
            [Path.Combine(_paths.NativeBin, "VegasInjector.exe"), "41", "42", Path.Combine(_paths.NativeBin, "VegasDedicated-4.dll"), result.RunDirectory, game, _source],
            _system.Injected);
        Assert.StartsWith("35.178.236.164\n3074\n9\n", File.ReadAllText(Path.Combine(result.RunDirectory, "native-route.txt")));
        Assert.Equal("stop_request", result.StopReason);
        Assert.True(_system.Process!.Stopped);
        var receipt = JsonNode.Parse(File.ReadAllText(Path.Combine(result.RunDirectory, "receipt.json")))!;
        Assert.Equal("original-dedicated-local-test", receipt["kind"]!.GetValue<string>());
        Assert.True(receipt["injected"]!.GetValue<bool>());
        Assert.False(File.Exists(Path.Combine(result.RunDirectory, "running.json")));
        Assert.False(File.Exists(Path.Combine(_paths.Local, "dedicated/vegas1/stop.request")));
    }

    [Fact]
    public async Task OnlineRunMintsAHostAliasAndRoutesToTheRealm()
    {
        var credentials = new FakeCredentials();
        var result = await Host().RunAsync(Request(credentials));

        Assert.Equal([true], credentials.Requests.Select(request => request.Dedicated).Distinct());
        Assert.Contains("?AgO=1?AgU=h0123456789abcd?AgP=UseDiscord?", _system.Started!.Arguments[1]);
        Assert.Equal(2, _system.Started.Arguments.Count);
        var route = File.ReadAllText(Path.Combine(result.RunDirectory, "native-route.txt")).Split('\n');
        Assert.Equal("192.0.2.10", route[0]);
        Assert.Equal("3074", route[1]);
        Assert.Matches("^[0-9a-f]{64}$", route[3]);
        Assert.Equal("h0123456789abcd", route[4]);
        Assert.DoesNotContain("one-use-password", File.ReadAllText(Path.Combine(result.RunDirectory, "receipt.json")));
        Assert.Equal("[CDKEY]\nKEYID=" + new string('a', 48) + "\n",
            File.ReadAllText(Path.Combine(result.RunDirectory, "profile/Ubisoft/R6Vegas/PlayerProfile.ini")));
        Assert.Contains(_paths.RootPublicKey.Replace('\\', '/'),
            File.ReadAllText(Path.Combine(_paths.Local, "dedicated/vegas1/game/KellerGame/Config/PC/AgoraConfig.lua")));
    }

    [Fact]
    public async Task DedicatedCopyGetsPackWithoutStoryModes()
    {
        const string story = "[GameMode0]\r\nm_eGameMode=GAMEMODE_STORY\r\nm_iMaxPlayers=4\r\nm_iMissionOrderId=2\r\n";
        _directory.File("Rainbow Six Vegas/" + MissionPlayerLimits.MapsFolder + "/01_Mexico/01_Mexico_04.ini", story);
        MapsPackFixture.Install(_paths, MapsPackFixture.Default());

        await Host().RunAsync(Request(null));

        var game = Path.Combine(_paths.Local, "dedicated/vegas1/game");
        Assert.Equal("map data", File.ReadAllText(Path.Combine(game, MapsPackFixture.Map)));
        Assert.Equal(MissionPlayerLimits.Raise(MapsPackFixture.ShippedIni), File.ReadAllText(Path.Combine(game, MapsPackFixture.MapIni)));
        var mission = File.ReadAllText(Path.Combine(game, MissionPlayerLimits.MapsFolder, "01_Mexico/01_Mexico_04.ini"));
        Assert.Equal(story, mission);
        Assert.Equal(story, File.ReadAllText(Path.Combine(_source, MissionPlayerLimits.MapsFolder, "01_Mexico/01_Mexico_04.ini")));
    }

    [Fact]
    public async Task RemovedPackRestoresTheServerStoryMissions()
    {
        var originals = new Dictionary<string, string>();
        foreach (var key in MissionPlayerLimits.FormerlyExtendedStoryMissions)
        {
            var text = "[GameMode0]\r\nm_eGameMode=GAMEMODE_STORY\r\nm_iMaxPlayers=4\r\nm_iMissionOrderId=2\r\n; " + key + "\r\n";
            originals[key] = text;
            _directory.File("Rainbow Six Vegas/" + MissionPlayerLimits.MapsFolder + "/" + key, text);
        }
        var game = Path.Combine(_paths.Local, "dedicated/vegas1/game");
        MapsPackFixture.Install(_paths, MapsPackFixture.Default());
        await Host().RunAsync(Request(null));
        foreach (var key in originals.Keys)
        {
            // A server copy extended by an earlier launcher version.
            File.AppendAllText(Path.Combine(game, MissionPlayerLimits.MapsFolder, key), "[GameMode1]\r\nm_eGameMode=GAMEMODE_DEATHMATCH\r\n");
        }

        MapsPackFixture.Remove(_paths);
        await Host().RunAsync(Request(null));

        Assert.Equal(18, originals.Count);
        Assert.All(originals, pair => Assert.Equal(
            File.ReadAllBytes(Path.Combine(_source, MissionPlayerLimits.MapsFolder, pair.Key)),
            File.ReadAllBytes(Path.Combine(game, MissionPlayerLimits.MapsFolder, pair.Key))));
        Assert.False(File.Exists(Path.Combine(game, MapsPackFixture.Map)));
        Assert.False(Directory.Exists(Path.Combine(game, MapsPackLink.PackFolder)));
    }

    [Fact]
    public async Task DamagedPackStopsTheServerBeforeStarting()
    {
        MapsPackFixture.Install(_paths, MapsPackFixture.Default());
        File.WriteAllText(Path.Combine(_paths.MapsPack, "files", MapsPackFixture.Map), "map data!");

        var error = await Assert.ThrowsAsync<DedicatedException>(() => Host().RunAsync(Request(null)));

        Assert.Equal("Extra Maps is damaged. Download it again or remove it.", error.Message);
        Assert.Null(_system.Started);
    }

    [Fact]
    public async Task MapsPackFileErrorIsReportedNotThrownRaw()
    {
        MapsPackFixture.Install(_paths, MapsPackFixture.Default());
        await Host().RunAsync(Request(null));
        // A reinstall gives the store new files, so the next start must relink the copy's file.
        MapsPackFixture.Install(_paths, MapsPackFixture.Default());
        var held = Path.Combine(_paths.Local, "dedicated/vegas1/game", MapsPackFixture.Map);

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Assert.ThrowsAsync<DedicatedException>(() => Host().RunAsync(Request(null)));
            Assert.StartsWith("Extra Maps could not be added to the server's game copy: ", error.Message);
        }
    }

    [Fact]
    public async Task ChangedServerExecutableIsRefusedBeforeCopying()
    {
        File.WriteAllText(Path.Combine(_source, "Binaries/R6VegasServer.exe"), "patched");
        var error = await Assert.ThrowsAsync<DedicatedException>(() => Host().RunAsync(Request(null)));
        Assert.Contains("not the supported original version", error.Message);
        Assert.False(Directory.Exists(Path.Combine(_paths.Local, "dedicated/vegas1/game")));
        Assert.Null(_system.Started);
    }

    [Fact]
    public async Task UnverifiedModuleStopsTheServer()
    {
        _system.Status = new JsonObject { ["ready"] = true, ["nativeVersion"] = 4 };
        await Assert.ThrowsAsync<DedicatedException>(() => Host().RunAsync(Request(null)));
        Assert.True(_system.Process!.Stopped);
    }

    [Fact]
    public async Task SecondHostOnTheSameCopyIsRefused()
    {
        _system.LockHeld = true;
        var error = await Assert.ThrowsAsync<DedicatedException>(() => Host().RunAsync(Request(null)));
        Assert.Contains("already running", error.Message);
    }

    [Fact]
    public void CheckReportsMissingComponentsAndChangedFiles()
    {
        File.Delete(Path.Combine(_paths.NativeBin, "VegasDedicated-4.dll"));
        File.WriteAllText(Path.Combine(_source, "Binaries/R6VegasServer.exe"), "patched");
        var problems = Host().Check(new DedicatedSettings { Title = TitleCatalog.Vegas1, GameFolder = _source });
        Assert.Contains(problems, problem => problem.Contains("R6VegasServer.exe") && problem.Contains("not the supported"));
        Assert.Contains(problems, problem => problem.Contains("VegasDedicated-4.dll"));
    }

    public void Dispose() => _directory.Dispose();

    /// <summary>Vegas 1 behavior with the synthetic server file pinned.</summary>
    private sealed class TestAdapter : IDedicatedAdapter
    {
        private readonly Vegas1DedicatedAdapter _inner = new();
        public GameTitle Title => _inner.Title;
        public string ModuleName => _inner.ModuleName;
        public IReadOnlyDictionary<string, string> PinnedFiles { get; } = new Dictionary<string, string>
        {
            ["Binaries/R6VegasServer.exe"] = Synthetic.Sha(Encoding.UTF8.GetBytes("server")),
        };
        public string NativeLog => _inner.NativeLog;
        public void PrepareCopy(string source, string game) => _inner.PrepareCopy(source, game);
        public IReadOnlyList<string> Arguments(DedicatedSettings settings, string? onlineAlias, bool localTest) => _inner.Arguments(settings, onlineAlias, localTest);
        public void ValidateReady(JsonObject status) => _inner.ValidateReady(status);
    }

    private sealed class FakeCredentials : ICredentialSource
    {
        public List<NativeCredentialRequest> Requests { get; } = [];
        public Task<LaunchAccount> RefreshAccountAsync(CancellationToken cancellationToken) => Task.FromResult(new LaunchAccount("1"));
        public bool SupportsTitle(GameTitle title) => true;
        public Task<NativeCredential> MintNativeAsync(NativeCredentialRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new NativeCredential("h0123456789abcd", "one-use-password"));
        }
    }

    private sealed class FakeSystem : IDedicatedSystem
    {
        public bool LockHeld { get; set; }
        public JsonObject Status { get; set; } = Vegas1DedicatedAdapterTests.Ready();
        public StartedServer? Started { get; private set; }
        public FakeProcess? Process { get; private set; }
        public IReadOnlyList<string>? Injected { get; private set; }

        public IDisposable? TryAcquireHostLock(string ownedRoot) => LockHeld ? null : new MemoryStream();

        public IServerProcess StartSuspended(string executable, IReadOnlyList<string> arguments, string workingDirectory)
        {
            Started = new StartedServer(executable, arguments);
            return Process = new FakeProcess();
        }

        public Task<JsonObject> RunComponentAsync(IReadOnlyList<string> arguments)
        {
            Injected = arguments;
            File.WriteAllText(Path.Combine(arguments[4], "compat-status.json"), Status.ToJsonString());
            return Task.FromResult(new JsonObject { ["injected"] = true });
        }

        public Task<IPAddress> ResolveIPv4Async(string host, CancellationToken cancellationToken) => Task.FromResult(IPAddress.Parse("192.0.2.10"));

        public long Now => Environment.TickCount64;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
    }

    private sealed record StartedServer(string Executable, IReadOnlyList<string> Arguments);

    private sealed class FakeProcess : IServerProcess
    {
        public bool Stopped { get; private set; }
        public int Id => 41;
        public int ThreadId => 42;
        public bool WaitForExit(int milliseconds) => Stopped;
        public int? ExitCode => Stopped ? 0 : null;
        public bool Stop()
        {
            Stopped = true;
            return false;
        }
        public void Dispose()
        {
        }
    }
}
