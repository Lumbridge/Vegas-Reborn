using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using VegasReborn.Core.Display;
using VegasReborn.Core.Launch;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Tests.Launch;

internal sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vr-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string relative, string? text = null)
    {
        var path = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        if (text is not null)
        {
            System.IO.File.WriteAllText(path, text);
        }
        return path;
    }

    public string Bytes(string relative, byte[] data)
    {
        var path = File(relative);
        System.IO.File.WriteAllBytes(path, data);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class Synthetic
{
    public static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>A minimal PE header (MZ, e_lfanew, PE signature, COFF header) followed by a marker.</summary>
    public static byte[] PeImage(string marker)
    {
        var data = new byte[0x100];
        data[0] = (byte)'M';
        data[1] = (byte)'Z';
        data[0x3c] = 0x80;
        "PE"u8.CopyTo(data.AsSpan(0x80));
        data[0x84] = 0x4c;
        data[0x85] = 0x01;
        data[0x96] = 0x0e;
        data[0x97] = 0x01;
        return [.. data, .. Encoding.ASCII.GetBytes(marker)];
    }

    public static readonly byte[] KeyDer = MakeKey();

    private static byte[] MakeKey()
    {
        var data = new byte[270];
        data[0] = 0x30;
        for (var index = 1; index < data.Length; index++)
        {
            data[index] = (byte)index;
        }
        return data;
    }
}

internal sealed class FakeCredentials : ICredentialSource
{
    public LaunchAccount Account { get; set; } = new("199999999999999999");
    public bool Supported { get; set; } = true;
    public bool Fail { get; set; }
    public List<NativeCredentialRequest> Mints { get; } = [];
    public Func<NativeCredentialRequest, NativeCredential> Lease { get; set; } =
        request => new NativeCredential((request.Dedicated ? "h" : "d") + "0123456789abcd", "UseOnce123");

    public Task<LaunchAccount> RefreshAccountAsync(CancellationToken cancellationToken) => Task.FromResult(Account);

    public bool SupportsTitle(GameTitle title) => Supported;

    public Task<NativeCredential> MintNativeAsync(NativeCredentialRequest request, CancellationToken cancellationToken)
    {
        lock (Mints)
        {
            Mints.Add(request);
        }
        if (Fail)
        {
            throw new InvalidOperationException("private-token-must-not-leak");
        }
        return Task.FromResult(Lease(request));
    }
}

internal sealed class FakeSync(string accounts) : IProfileSync
{
    public int Pulls { get; private set; }
    public int Pushes { get; private set; }

    public string AccountDirectory(GameTitle title) => System.IO.Path.Combine(accounts, title.Key);

    public Task PullAsync(GameTitle title, CancellationToken cancellationToken)
    {
        Pulls++;
        return Task.CompletedTask;
    }

    public Task<bool> PushAsync(GameTitle title, CancellationToken cancellationToken)
    {
        Pushes++;
        return Task.FromResult(true);
    }
}

internal sealed class FakeProcess(int waits) : IGameProcess
{
    private int _remaining = waits;

    public bool WaitForExit(int milliseconds) => --_remaining < 0;

    public void Dispose()
    {
    }
}

/// <summary>Simulates Steam, the gate, the injector and the adapter by writing their files.</summary>
internal sealed class FakeSystem : ILaunchSystem
{
    public List<IReadOnlyList<string>> Components { get; } = [];
    public List<IReadOnlyList<string>> SteamLaunches { get; } = [];
    public List<int> Terminated { get; } = [];
    public bool Running { get; set; }
    public bool MutexHeld { get; set; }
    public int? SteamPid { get; set; } = 4321;
    public bool StartChild { get; set; } = true;
    public Func<JsonObject>? Status { get; set; }
    public JsonObject? LaunchOptions { get; set; }
    public string? Output { get; set; }
    public string Cache { get; set; } = "";
    public int ExitAfterWaits { get; set; } = 1;
    public Action? OnStartSteam { get; set; }

    public bool IsGameRunning(GameTitle title) => Running;

    public IDisposable? TryAcquireLaunchMutex() => MutexHeld ? null : new MemoryStream();

    public int? FindSteamProcessId() => SteamPid;

    public string SteamExecutable() => "steam.exe";

    public void StartSteam(string steamExecutable, IReadOnlyList<string> arguments)
    {
        SteamLaunches.Add(arguments);
        OnStartSteam?.Invoke();
        if (StartChild)
        {
            File.WriteAllText(System.IO.Path.Combine(Output!, "child.json"), "{\"pid\": 42, \"tid\": 7}");
        }
    }

    public Task<IPAddress> ResolveIPv4Async(string host, CancellationToken cancellationToken) => Task.FromResult(IPAddress.Parse("203.0.113.5"));

    public Task<JsonObject> RunComponentAsync(IReadOnlyList<string> arguments)
    {
        Components.Add(arguments);
        var name = System.IO.Path.GetFileName(arguments[0]);
        if (name == NativeComponents.SteamControl && arguments[1] == "--configure")
        {
            return Task.FromResult(new JsonObject { ["module"] = 140703128616960L });
        }
        if (name == NativeComponents.Injector && Status is not null)
        {
            File.WriteAllText(System.IO.Path.Combine(arguments[4], "compat-status.json"), Status().ToJsonString());
            if (LaunchOptions is not null)
            {
                File.WriteAllText(System.IO.Path.Combine(arguments[4], "launch-options-status.json"), LaunchOptions.ToJsonString());
            }
        }
        return Task.FromResult(new JsonObject());
    }

    public IGameProcess? OpenGameProcess(int processId) => new FakeProcess(ExitAfterWaits);

    public void TerminateProcess(int processId) => Terminated.Add(processId);

    public string LaunchCacheDirectory => Cache;

    public long Now { get; private set; }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Now += (long)delay.TotalMilliseconds;
        return Task.CompletedTask;
    }
}

internal sealed class FakeDesktop : IDesktop
{
    public List<string> Calls { get; } = [];

    public IReadOnlyList<DisplayMonitor> Monitors() =>
        [new DisplayMonitor("primary", new MonitorBounds(0, 0, 1920, 1080), true)];

    public bool FitBorderless(int processId, DisplayMonitor monitor)
    {
        Calls.Add("borderless:" + processId);
        return true;
    }

    public bool FitWindowed(int processId)
    {
        Calls.Add("windowed:" + processId);
        return true;
    }
}

internal sealed class FakeCarrier : IStatisticsCarrier
{
    public string Codec { get; set; } = StatisticsCarrier.Codec;

    public string Ensure(string sourceGameRoot, string ownedGameRoot) => Codec;
}

internal sealed class Messages : IProgress<string>
{
    public List<string> Items { get; } = [];

    public void Report(string value)
    {
        lock (Items)
        {
            Items.Add(value);
        }
    }

    public override string ToString()
    {
        lock (Items)
        {
            return string.Join("\n", Items);
        }
    }
}

internal static class Reports
{
    /// <summary>The document the Vegas 2 adapter writes once its game hooks are installed.</summary>
    public static JsonObject Vegas2Ready() => new()
    {
        ["ready"] = true,
        ["title"] = "vegas2",
        ["adapter"] = "vegas2",
        ["nativeVersion"] = 1,
        ["gameHooks"] = 11,
        ["proceduralAnimationVerified"] = true,
        ["proceduralAnimationStatus"] = "ok",
        ["discordSignInVerified"] = true,
        ["discordSignInStatus"] = "ok",
        ["claimedNameVerified"] = true,
        ["claimedNameStatus"] = "ok",
        ["soloMatchVerified"] = true,
        ["soloMatchStatus"] = "ok",
        ["fullXpVerified"] = true,
        ["skipIntrosVerified"] = true,
        ["fullXpStatus"] = "ok",
        ["punkBusterRemovedVerified"] = true,
        ["punkBusterRemovedStatus"] = "ok",
    };

    public static JsonObject Vegas1Ready() => new()
    {
        ["ready"] = true,
        ["signatures"] = 16383,
        ["nativeVersion"] = 14,
        ["proceduralAnimationVerified"] = true,
        ["nativeStatisticsVerified"] = true,
        ["nativeRankedHostVerified"] = true,
        ["nativeSoloHuntVerified"] = true,
        ["nativeUnlockPolicyVerified"] = true,
        ["nativePecDefaultsVerified"] = true,
        ["reportXpVerified"] = true,
        ["nativeRankVerified"] = true,
        ["rankedAccessVerified"] = true,
        ["playerLimitVerified"] = true,
        ["fireSoundLoopVerified"] = true,
        ["punkBusterRemovedVerified"] = true,
        ["disabledFeatures"] = new JsonArray(),
    };

    public static string Utf8(string path) => Encoding.UTF8.GetString(File.ReadAllBytes(path));
}
