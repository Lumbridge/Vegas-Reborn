using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VegasReborn.Core.Content;
using VegasReborn.Core.Launch;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Dedicated;

/// <summary>How a dedicated run ended.</summary>
public sealed record DedicatedResult(int ProcessId, string RunDirectory, int? ExitCode, string StopReason, bool ForcedTermination);

/// <summary>One dedicated run.</summary>
public sealed record DedicatedRunRequest
{
    public required DedicatedSettings Settings { get; init; }

    /// <summary>
    /// The signed-in account that owns the server. Null only for a local test, which binds
    /// loopback and does not sign in.
    /// </summary>
    public ICredentialSource? Credentials { get; init; }

    public bool LocalTest => Credentials is null;

    /// <summary>Called once the server is running and verified.</summary>
    public Action<DedicatedResult>? OnStarted { get; init; }
}

/// <summary>
/// Runs a title's original dedicated server from a companion-owned copy with its native
/// module, until it exits or is stopped. No time limit is applied. The installed game is
/// only read; each run writes its route, profile and receipt to its own run folder.
/// </summary>
public sealed class DedicatedHost
{
    public const string StopFileName = "stop.request";
    private const string ReadyMarker = ".dedicated-ready.json";
    // Inert route fields for a local test: no IPC listener, session or password is supplied.
    private const string LocalTestRoute = "35.178.236.164\n3074\n9\n" + "0000000000000000000000000000000000000000000000000000000000000000" + "\nlanfixture00001\n";
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    private readonly AppPaths _paths;
    private readonly Func<ServiceConfiguration> _configuration;
    private readonly IDedicatedSystem _system;
    private readonly Func<GameTitle, IDedicatedAdapter> _adapters;

    public DedicatedHost(AppPaths paths)
        : this(paths, () => ServiceConfiguration.Load(paths), new WindowsDedicatedSystem(), DedicatedAdapters.For)
    {
    }

    internal DedicatedHost(AppPaths paths, Func<ServiceConfiguration> configuration, IDedicatedSystem system, Func<GameTitle, IDedicatedAdapter> adapters)
    {
        _paths = paths;
        _configuration = configuration;
        _system = system;
        _adapters = adapters;
    }

    /// <summary>The title's dedicated folder: server.json, the game copy and run folders.</summary>
    public static string Root(AppPaths paths, GameTitle title) => Path.Combine(paths.Local, "dedicated", title.Key);

    public static string SettingsFile(AppPaths paths, GameTitle title) => Path.Combine(Root(paths, title), "server.json");

    /// <summary>
    /// Checks the installed server files and the native components without copying or
    /// starting anything. Returns the problems found; empty means ready to run.
    /// </summary>
    public IReadOnlyList<string> Check(DedicatedSettings settings)
    {
        var problems = new List<string>();
        IDedicatedAdapter adapter;
        try
        {
            adapter = _adapters(settings.Title);
        }
        catch (DedicatedException error)
        {
            return [error.Message];
        }
        foreach (var (relative, expected) in adapter.PinnedFiles)
        {
            var path = Path.Combine(settings.GameFolder, relative);
            try
            {
                if (PrivateFile.Sha256(path) != expected)
                {
                    problems.Add($"{relative} in the game folder is not the supported original version");
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{relative} was not found in the game folder");
            }
        }
        foreach (var path in new[] { Path.Combine(_paths.NativeBin, NativeComponents.Injector), _paths.NativeAdapter(adapter.ModuleName) })
        {
            if (!File.Exists(path))
            {
                problems.Add($"Required component is missing: {Path.GetFileName(path)}");
            }
        }
        try
        {
            _configuration();
        }
        catch (LaunchException error)
        {
            problems.Add(error.Message);
        }
        return problems;
    }

    public async Task<DedicatedResult> RunAsync(DedicatedRunRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new DedicatedException("Dedicated hosting runs on Windows, or under Wine on Linux");
        }
        var settings = request.Settings;
        settings.Validate();
        var title = settings.Title;
        var adapter = _adapters(title);
        var root = Path.GetFullPath(Root(_paths, title));
        var source = Path.GetFullPath(settings.GameFolder);
        if (PrivateFile.IsWithin(root, source) || PrivateFile.IsWithin(source, root))
        {
            throw new DedicatedException("The server's game copy must be separate from the installation");
        }
        var injector = Path.Combine(_paths.NativeBin, NativeComponents.Injector);
        var module = _paths.NativeAdapter(adapter.ModuleName);
        foreach (var component in new[] { injector, module })
        {
            if (!File.Exists(component))
            {
                throw new DedicatedException($"Required component is missing: {Path.GetFileName(component)}. Reinstall a complete official release.");
            }
        }
        var configuration = _configuration();
        Directory.CreateDirectory(root);
        using var hostLock = _system.TryAcquireHostLock(root)
            ?? throw new DedicatedException($"A {title.DisplayName} dedicated server is already running from this installation");
        var stopFile = Path.Combine(root, StopFileName);
        PrivateFile.Delete(stopFile);

        progress?.Report("Checking the original dedicated server files...");
        var game = await PrepareCopyAsync(adapter, source, root, progress, cancellationToken).ConfigureAwait(false);
        if (title.Key == TitleCatalog.Vegas1.Key)
        {
            await Task.Run(() => LinkMapsPack(game, source), cancellationToken).ConfigureAwait(false);
        }
        await Task.Run(() => GamePreparer.ApplyHuntEnemies(title, game, source), cancellationToken).ConfigureAwait(false);
        var profile = TitleLaunchProfile.For(title);
        if (!request.LocalTest)
        {
            GamePreparer.InstallTrustKey(game, title, profile, configuration.PublicKeyPath);
        }

        var run = Path.Combine(root, "runs", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
        Directory.CreateDirectory(run);
        var receipt = new JsonObject
        {
            ["kind"] = request.LocalTest ? "original-dedicated-local-test" : "original-dedicated-online-run",
            ["title"] = title.Key,
            ["startedUtc"] = Timestamp(),
            ["serverSha256"] = title.ServerSha256,
            ["moduleSha256"] = PrivateFile.Sha256(module),
            ["injected"] = false,
            ["secretsInArguments"] = false,
        };
        CredentialIpc? ipc = null;
        IServerProcess? process = null;
        var stopReason = "setup_failed";
        var forced = false;
        int? exitCode = null;
        DedicatedResult? started = null;
        try
        {
            string? alias = null;
            if (request.Credentials is { } credentials)
            {
                progress?.Report("Signing in for dedicated hosting...");
                var credentialRequest = new NativeCredentialRequest(title, Dedicated: true);
                // Mint only for the stable host alias. The game requests its own one-use
                // credential through the IPC endpoint; no password is saved or put on a command line.
                alias = (await credentials.MintNativeAsync(credentialRequest, cancellationToken).ConfigureAwait(false)).Username;
                ipc = new CredentialIpc(credentials, credentialRequest, progress);
                GamePreparer.InstallCdKey(game, LaunchLayout.ProfileDirectory(run, title), profile, configuration.NativeLicenseId);
                var address = await _system.ResolveIPv4Async(configuration.Host, cancellationToken).ConfigureAwait(false);
                PrivateFile.Write(Path.Combine(run, "native-route.txt"), Encoding.ASCII.GetBytes(
                    $"{address}\n{title.RealmPort.ToString(CultureInfo.InvariantCulture)}\n{ipc.Port.ToString(CultureInfo.InvariantCulture)}\n{ipc.Capability}\n{alias}\n"));
                receipt["credentialMode"] = "verified-Discord-dedicated-role/on-demand-native-one-use-lease";
            }
            else
            {
                Directory.CreateDirectory(Path.Combine(run, "profile"));
                PrivateFile.Write(Path.Combine(run, "native-route.txt"), Encoding.ASCII.GetBytes(LocalTestRoute));
            }
            var arguments = adapter.Arguments(settings, alias, request.LocalTest);
            receipt["arguments"] = new JsonArray(arguments.Select(argument => (JsonNode?)RedactPassword(argument)).ToArray());

            cancellationToken.ThrowIfCancellationRequested();
            var executable = Path.Combine(game, title.ServerExecutable!);
            process = _system.StartSuspended(executable, arguments, Path.GetDirectoryName(executable)!);
            receipt["pid"] = process.Id;
            PrivateFile.WriteText(Path.Combine(run, "child.json"),
                $"{{\"pid\": {process.Id.ToString(CultureInfo.InvariantCulture)}, \"tid\": {process.ThreadId.ToString(CultureInfo.InvariantCulture)}}}\n");
            progress?.Report("Starting the original dedicated server...");
            receipt["injector"] = await _system.RunComponentAsync(
            [
                injector, process.Id.ToString(CultureInfo.InvariantCulture), process.ThreadId.ToString(CultureInfo.InvariantCulture),
                module, run, game, source,
            ]).ConfigureAwait(false);
            receipt["injected"] = true;

            var statusFile = Path.Combine(run, "compat-status.json");
            var deadline = _system.Now + 65_000;
            while (!File.Exists(statusFile))
            {
                if (process.WaitForExit(0))
                {
                    throw new DedicatedException("The original server exited during setup. Its log is in " + run);
                }
                if (_system.Now > deadline)
                {
                    throw new DedicatedException("The server's compatibility module did not become ready");
                }
                await _system.DelayAsync(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
            var status = ReadObject(statusFile) ?? throw new DedicatedException("The server's compatibility module did not become ready");
            receipt["compatibility"] = status.DeepClone();
            adapter.ValidateReady(status);

            started = new DedicatedResult(process.Id, run, null, "running", false);
            PrivateFile.WriteText(Path.Combine(run, "running.json"),
                new JsonObject { ["pid"] = process.Id, ["stopRequest"] = stopFile }.ToJsonString() + "\n");
            progress?.Report(request.LocalTest
                ? "Local test server running on 127.0.0.3. Press Ctrl+C to stop."
                : "Dedicated server running. Players join through Online > Custom Match. Press Ctrl+C to stop.");
            request.OnStarted?.Invoke(started);

            stopReason = "native_process_exit";
            while (!process.WaitForExit(0))
            {
                if (cancellationToken.IsCancellationRequested || File.Exists(stopFile))
                {
                    stopReason = "stop_request";
                    break;
                }
                try
                {
                    await _system.DelayAsync(Poll, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        catch (OperationCanceledException) when (started is null)
        {
            stopReason = "cancelled";
            throw new DedicatedException("Server start was cancelled");
        }
        finally
        {
            try
            {
                if (process is not null)
                {
                    using (process)
                    {
                        progress?.Report("Stopping the dedicated server...");
                        forced = process.Stop();
                        exitCode = process.ExitCode;
                        receipt["exitCode"] = exitCode;
                    }
                }
            }
            finally
            {
                if (ipc is not null)
                {
                    await ipc.DisposeAsync().ConfigureAwait(false);
                }
                receipt["stopReason"] = stopReason;
                receipt["forcedTermination"] = forced;
                receipt["finishedUtc"] = Timestamp();
                CopyNativeLog(Path.Combine(game, adapter.NativeLog), run, receipt);
                PrivateFile.WriteText(Path.Combine(run, "receipt.json"),
                    receipt.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
                PrivateFile.Delete(Path.Combine(run, "running.json"));
                PrivateFile.Delete(stopFile);
            }
        }
        progress?.Report("Dedicated server stopped.");
        return new DedicatedResult(started!.ProcessId, run, exitCode, stopReason, forced);
    }

    /// <summary>Asks a running server for this title to stop (used by service wrappers and containers).</summary>
    public static void RequestStop(AppPaths paths, GameTitle title) =>
        PrivateFile.WriteText(Path.Combine(Root(paths, title), StopFileName), "stop\n");

    /// <summary>
    /// Links the installed Maps Pack into the server copy and adds the story-mission versus modes;
    /// without the pack, unlinks it and restores those story missions to the originals.
    /// </summary>
    private void LinkMapsPack(string game, string source)
    {
        try
        {
            if (MapsPackLink.Apply(_paths, game))
            {
                MissionPlayerLimits.Apply(game, source, extendModes: true);
            }
            else
            {
                MissionPlayerLimits.RestoreStoryModes(game, source);
            }
        }
        catch (LaunchException error)
        {
            throw new DedicatedException(error.Message, error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new DedicatedException($"The Maps Pack could not be added to the server's game copy: {error.Message}", error);
        }
    }

    private static async Task<string> PrepareCopyAsync(IDedicatedAdapter adapter, string source, string root, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await Task.Run(() => RequirePinned(adapter, source, "in the game folder"), cancellationToken).ConfigureAwait(false);
        var game = Path.Combine(root, "game");
        var marker = Path.Combine(game, ReadyMarker);
        if (!File.Exists(marker))
        {
            if (Directory.Exists(game) || File.Exists(game))
            {
                throw new DedicatedException($"An unfinished server game copy exists at {game}. Delete it, then retry.");
            }
            progress?.Report("Preparing the server's own game copy. The installed game is preserved.");
            await Task.Run(() => GamePreparer.CopyTree(source, game, cancellationToken), cancellationToken).ConfigureAwait(false);
            PrivateFile.WriteText(marker, $"{{\"version\": 1, \"title\": \"{adapter.Title.Key}\", \"serverSha256\": \"{adapter.Title.ServerSha256}\"}}\n");
        }
        await Task.Run(() => RequirePinned(adapter, game, "in the server's game copy"), cancellationToken).ConfigureAwait(false);
        adapter.PrepareCopy(source, game);
        return game;
    }

    private static void RequirePinned(IDedicatedAdapter adapter, string folder, string where)
    {
        foreach (var (relative, expected) in adapter.PinnedFiles)
        {
            string actual;
            try
            {
                actual = PrivateFile.Sha256(Path.Combine(folder, relative));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new DedicatedException($"{relative} was not found {where}", error);
            }
            if (actual != expected)
            {
                throw new DedicatedException($"{relative} {where} is not the supported original version");
            }
        }
    }

    private static void CopyNativeLog(string log, string run, JsonObject receipt)
    {
        try
        {
            if (File.Exists(log))
            {
                var data = File.ReadAllBytes(log);
                PrivateFile.Write(Path.Combine(run, "native-server.log"), data);
                receipt["nativeLogSha256"] = PrivateFile.Sha256(data);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            receipt["nativeLogError"] = error.GetType().Name;
        }
    }

    internal static string RedactPassword(string argument)
    {
        var index = argument.LastIndexOf("?PW=", StringComparison.Ordinal);
        return index < 0 || index + 4 == argument.Length ? argument : argument[..(index + 4)] + "<set>";
    }

    private static JsonObject? ReadObject(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string Timestamp() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
