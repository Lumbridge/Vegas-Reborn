using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VegasReborn.Core.Display;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Launch;

/// <summary>
/// Normal-play launch of an original PC game through Steam into the companion's own copy,
/// with the title's native adapter, then cloud save after the game exits.
/// </summary>
public sealed class GameLauncher
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    private readonly AppPaths _paths;
    private readonly ServiceConfiguration _configuration;
    private readonly ICredentialSource _credentials;
    private readonly IProfileSync _sync;
    private readonly ILaunchSystem _system;
    private readonly Func<IDesktop> _desktop;
    private readonly IStatisticsCarrier _carrier;
    private readonly Func<GameTitle, TitleLaunchProfile> _profiles;
    private int _running;

    public GameLauncher(AppPaths paths, ServiceConfiguration configuration, ICredentialSource credentials, IProfileSync sync)
        : this(paths, configuration, credentials, sync, new WindowsLaunchSystem(), () => new Desktop(), new StatisticsCarrier(), TitleLaunchProfile.For)
    {
    }

    internal GameLauncher(
        AppPaths paths,
        ServiceConfiguration configuration,
        ICredentialSource credentials,
        IProfileSync sync,
        ILaunchSystem system,
        Func<IDesktop> desktop,
        IStatisticsCarrier carrier,
        Func<GameTitle, TitleLaunchProfile> profiles)
    {
        _paths = paths;
        _configuration = configuration;
        _credentials = credentials;
        _sync = sync;
        _system = system;
        _desktop = desktop;
        _carrier = carrier;
        _profiles = profiles;
    }

    /// <summary>True from the start of <see cref="PlayAsync"/> until it returns.</summary>
    public bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>
    /// Prepares the copy, starts the game through Steam, verifies the adapter, waits for the
    /// game to exit and saves the character. Status text goes to <paramref name="progress"/>.
    /// Cancellation applies to setup; once the game is running the launcher waits for it.
    /// Expected failures are <see cref="LaunchException"/> with a user-facing message;
    /// account and sync failures come from <see cref="ICredentialSource"/> and <see cref="IProfileSync"/>.
    /// </summary>
    public async Task<LaunchOutcome> PlayAsync(LaunchRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var title = request.Title;
        var profile = _profiles(title);
        if (_system.IsGameRunning(title))
        {
            throw new LaunchException("Close the running retail game before launching another");
        }
        var adapter = _paths.NativeAdapter(title.NativeAdapter);
        NativeComponents.RequireComponents(_paths.NativeBin, title, adapter);
        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            throw new LaunchException("Another launcher is already launching the game");
        }
        IDisposable? mutex = null;
        CredentialIpc? ipc = null;
        JsonObject? gate = null;
        (int Pid, int Tid)? child = null;
        var started = false;
        int steamPid = 0;
        var control = Path.Combine(_paths.NativeBin, NativeComponents.SteamControl);
        var gateDll = "";
        try
        {
            mutex = _system.TryAcquireLaunchMutex() ?? throw new LaunchException("Another launcher is already launching the game");
            var source = Path.GetFullPath(request.GameFolder);
            var disabled = DisabledToggles(title, request.DisabledFixes);
            var hasSaves = title.SaveSlots.Count > 0;
            if (hasSaves)
            {
                progress?.Report("Checking the account's cloud character...");
            }
            // Rehydrate the claim from its owner-authenticated source. A cached
            // display name cannot authorize an in-game identity or rename.
            var account = await _credentials.RefreshAccountAsync(cancellationToken).ConfigureAwait(false);
            if (account.NameClaimsEnabled && account.ClaimRequired)
            {
                throw new LaunchException("Choose your player name in the launcher before playing");
            }
            if (hasSaves)
            {
                await _sync.PullAsync(title, cancellationToken).ConfigureAwait(false);
            }
            var game = await GamePreparer.PrepareAsync(_paths, title, profile, source, disabled, progress, cancellationToken, request.CopyProgress).ConfigureAwait(false);
            var statisticsCodec = "";
            if (profile.StatisticsCarrier)
            {
                try
                {
                    statisticsCodec = await Task.Run(() => _carrier.Ensure(source, game), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or Win32Exception)
                {
                    throw new LaunchException("The copied game's leaderboard compatibility setup failed", error);
                }
                if (statisticsCodec != StatisticsCarrier.Codec)
                {
                    throw new LaunchException("The copied game's leaderboard compatibility was not verified");
                }
            }
            StartupMovies.Configure(game, request.SkipIntros);
            var displayMode = request.DisplayMode;
            var desktop = displayMode is DisplayMode.Borderless or DisplayMode.Windowed ? _desktop() : null;
            var monitor = desktop is null ? null : DisplayConfiguration.SelectMonitor(desktop.Monitors(), request.MonitorDevice);
            DisplayConfiguration.Configure(game, displayMode, monitor);

            var output = Path.GetFullPath(_sync.AccountDirectory(title));
            Directory.CreateDirectory(output);
            // Both adapters read the claim; it changes only the displayed name.
            var claimFile = Path.Combine(output, "claimed-name.txt");
            if (account.ClaimedName is not null)
            {
                PrivateFile.Write(claimFile, Encoding.ASCII.GetBytes(account.ClaimedName + "\n"));
            }
            else
            {
                PrivateFile.Delete(claimFile);
            }
            // Both adapters complete the original startup intro sequence on request.
            PrivateFile.WriteText(Path.Combine(output, "skip-intros.txt"), request.SkipIntros ? "skip\n" : "play\n");
            // Both adapters apply Windowed and Borderless in the game.
            PrivateFile.WriteText(Path.Combine(output, "display-mode.txt"), displayMode switch
            {
                DisplayMode.Borderless => "borderless\n",
                DisplayMode.Windowed => "windowed\n",
                _ => "native\n",
            });
            // Both adapters scale the player's view to the requested field of view.
            PrivateFile.WriteText(Path.Combine(output, "field-of-view.txt"),
                request.FieldOfView > 0 ? FormattableString.Invariant($"{request.FieldOfView}\n") : "default\n");
            PrivateFile.Delete(Path.Combine(output, "launch-options-status.json"));
            if (profile.NativeGameHooks)
            {
                PrivateFile.WriteText(Path.Combine(output, NativeFeatures.FileName), NativeFeatures.Format(disabled));
            }
            var profileDirectory = LaunchLayout.ProfileDirectory(output, title);
            GamePreparer.InstallCdKey(game, profileDirectory, profile, _configuration.NativeLicenseId);
            GamePreparer.InstallTrustKey(game, title, profile, _configuration.PublicKeyPath);

            // Mint only for the stable alias here; this lease is revoked by the
            // on-demand game Login request. Its password is never saved.
            string alias;
            int ipcPort;
            string capability;
            if (_credentials.SupportsTitle(title))
            {
                var credentialRequest = new NativeCredentialRequest(title, statisticsCodec);
                var lease = await _credentials.MintNativeAsync(credentialRequest, cancellationToken).ConfigureAwait(false);
                alias = lease.Username;
                ipc = new CredentialIpc(_credentials, credentialRequest, progress);
                ipcPort = ipc.Port;
                capability = ipc.Capability;
            }
            else
            {
                // No native credentials for this title yet: sign in manually in the game.
                // The adapter's route format still needs inert IPC fields and an alias.
                alias = "v" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(7));
                ipcPort = 1;
                capability = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            }
            var address = await _system.ResolveIPv4Async(_configuration.Host, cancellationToken).ConfigureAwait(false);
            // service.json pins the Vegas 1 port (3074); each title has its own master port.
            var realmPort = title.RealmPort;
            PrivateFile.Write(Path.Combine(output, "native-route.txt"),
                Encoding.ASCII.GetBytes($"{address}\n{realmPort}\n{ipcPort}\n{capability}\n{alias}\n"));

            steamPid = _system.FindSteamProcessId() ?? throw new LaunchException("Start Steam and sign in before playing");
            var childFile = Path.Combine(output, "child.json");
            PrivateFile.Delete(childFile);
            var readyFile = Path.Combine(output, "compat-status.json");
            PrivateFile.Delete(readyFile);
            var launchConfig = Path.Combine(output, "launch.utf16");
            var sourceExecutable = Path.GetFullPath(Path.Combine(source, title.ClientExecutable));
            PrivateFile.Write(launchConfig, Encoding.Unicode.GetBytes(
                sourceExecutable + "\n" + Path.Combine(game, "Binaries") + "\nwinsta0\\Default\n" + output));
            gateDll = NativeComponents.RuntimeGate(Path.Combine(_paths.NativeBin, NativeComponents.SteamGate), _system.LaunchCacheDirectory);
            cancellationToken.ThrowIfCancellationRequested();
            gate = await _system.RunComponentAsync([control, "--configure", Argument(steamPid), gateDll, "StartGate", launchConfig]).ConfigureAwait(false);
            _system.StartSteam(_system.SteamExecutable(),
                ["-applaunch", Argument(title.SteamAppId), .. title.LaunchArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)]);
            await WaitForFileAsync(childFile, 45, "Steam did not start the game", cancellationToken).ConfigureAwait(false);
            child = ReadChild(childFile);
            await _system.RunComponentAsync(
            [
                Path.Combine(_paths.NativeBin, NativeComponents.Injector), Argument(child.Value.Pid), Argument(child.Value.Tid),
                adapter, output, game, source,
            ]).ConfigureAwait(false);
            // A timeout can arrive after the unload ran. Never retry a stale module handle.
            var module = ModuleArgument(gate);
            gate = null;
            await _system.RunComponentAsync([control, "--stop-gate", Argument(steamPid), module, gateDll]).ConfigureAwait(false);
            await WaitForFileAsync(readyFile, 65, "The game's compatibility module did not become ready", cancellationToken).ConfigureAwait(false);
            var compatibility = ReadObject(readyFile) ?? throw new LaunchException("The game's compatibility module did not become ready");
            CompatibilityValidation.Validate(title, compatibility, account.ClaimedName, displayMode, disabled, request.SkipIntros, request.FieldOfView);
            // Vegas 2 reports its intro hook in compat-status.json.
            var optionsIntros = profile.NativeGameHooks && request.SkipIntros;
            if (displayMode is DisplayMode.Windowed or DisplayMode.Borderless || optionsIntros)
            {
                progress?.Report("Verifying the game's requested display and intro settings...");
                var optionsFile = Path.Combine(output, "launch-options-status.json");
                var deadline = _system.Now + 15_000;
                while (!CompatibilityValidation.LaunchOptionsReady(ReadObject(optionsFile) ?? [], displayMode, optionsIntros))
                {
                    if (_system.Now > deadline)
                    {
                        throw new LaunchException("The game's requested display or intro settings were not verified. Close the game and retry.");
                    }
                    await _system.DelayAsync(Poll, cancellationToken).ConfigureAwait(false);
                }
            }
            started = true;
            progress?.Report(profile.RunningMessage);
            request.OnStarted?.Invoke();
            using (var process = _system.OpenGameProcess(child.Value.Pid) ?? throw new LaunchException("The game exited during startup"))
            {
                await Task.Run(() => WaitForExit(process, child.Value.Pid, displayMode, request.MonitorDevice, desktop, progress), CancellationToken.None).ConfigureAwait(false);
            }
            if (!hasSaves)
            {
                progress?.Report("Game closed.");
                return new LaunchOutcome(false);
            }
            progress?.Report("Saving the character and progression to your account...");
            var saved = title.SaveSlots.Values.Any(name => File.Exists(Path.Combine(profileDirectory, name)))
                && await _sync.PushAsync(title, CancellationToken.None).ConfigureAwait(false);
            progress?.Report(saved ? "Character saved to your account." : "Game closed. No character was created.");
            return new LaunchOutcome(saved);
        }
        finally
        {
            try
            {
                if (child is { } owned && !started)
                {
                    // Only a child reported by our active Steam gate is owned. Setup
                    // failures must not leave its main thread suspended.
                    _system.TerminateProcess(owned.Pid);
                }
                if (gate is not null)
                {
                    try
                    {
                        await _system.RunComponentAsync([control, "--stop-gate", Argument(steamPid), ModuleArgument(gate), gateDll]).ConfigureAwait(false);
                    }
                    catch (LaunchException)
                    {
                        // Keep the original failure; the gate unloads with Steam's next restart.
                    }
                }
            }
            finally
            {
                if (ipc is not null)
                {
                    await ipc.DisposeAsync().ConfigureAwait(false);
                }
                mutex?.Dispose();
                Volatile.Write(ref _running, 0);
            }
        }
    }

    private void WaitForExit(IGameProcess process, int pid, DisplayMode mode, string monitorDevice, IDesktop? desktop, IProgress<string>? progress)
    {
        while (!process.WaitForExit(500))
        {
            if (desktop is null)
            {
                continue;
            }
            try
            {
                if (mode == DisplayMode.Windowed)
                {
                    desktop.FitWindowed(pid);
                }
                else if (DisplayConfiguration.SelectMonitor(desktop.Monitors(), monitorDevice) is { } current)
                {
                    desktop.FitBorderless(pid, current);
                }
            }
            catch (Exception error) when (error is Win32Exception or IOException)
            {
                progress?.Report($"Could not apply {DisplayModes.Name(mode).ToLowerInvariant()} mode. The game is still running; restart it to retry.");
                desktop = null;
            }
        }
    }

    /// <summary>Only toggle fixes of this title can be disabled; AlwaysOn and unknown ids are ignored.</summary>
    internal static IReadOnlySet<string> DisabledToggles(GameTitle title, IReadOnlySet<string> requested) =>
        title.Fixes.Where(fix => fix.Kind == FixKind.Toggle && requested.Contains(fix.Id)).Select(fix => fix.Id).ToHashSet(StringComparer.Ordinal);

    private async Task WaitForFileAsync(string path, int seconds, string message, CancellationToken cancellationToken)
    {
        var deadline = _system.Now + seconds * 1000L;
        while (!File.Exists(path))
        {
            if (_system.Now > deadline)
            {
                throw new LaunchException(message);
            }
            await _system.DelayAsync(Poll, cancellationToken).ConfigureAwait(false);
        }
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

    private static (int Pid, int Tid) ReadChild(string path)
    {
        var value = ReadObject(path);
        try
        {
            if (value?["pid"] is JsonValue pid && value["tid"] is JsonValue tid)
            {
                return (pid.GetValue<int>(), tid.GetValue<int>());
            }
        }
        catch (Exception error) when (error is FormatException or InvalidOperationException)
        {
        }
        throw new LaunchException("Steam did not start the game");
    }

    private static string ModuleArgument(JsonObject gate) => gate["module"] switch
    {
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        JsonValue value when value.GetValueKind() == JsonValueKind.Number => value.ToJsonString(),
        _ => throw new NativeLaunchException($"{NativeComponents.SteamControl} returned an invalid launch result. Your local character was preserved."),
    };

    private static string Argument(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The account output folder's native-features.txt (disabled toggle ids, one per line).</summary>
public static class NativeFeatures
{
    public const string FileName = "native-features.txt";

    public static string Format(IEnumerable<string> disabledToggles) =>
        string.Concat(disabledToggles.Order(StringComparer.Ordinal).Select(id => id + "\n"));
}
