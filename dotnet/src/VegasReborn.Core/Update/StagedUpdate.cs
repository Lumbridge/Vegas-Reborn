using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VegasReborn.Core.Update;

/// <summary>A verified staged update: the installation root, the extracted payload and its release.</summary>
public sealed record StagedPlan(string PlanPath, string Root, string Payload, Release Release);

/// <summary>
/// Staging, hand-off and installation. The <c>plan.json</c> format and the helper command line
/// (<c>VegasReborn.exe --apply-update &lt;plan.json&gt; --parent-pid &lt;pid&gt;</c>, started from the
/// payload folder) are exactly those of the 0.4.x Python updater, which hands its update to
/// the new release's executable.
/// </summary>
public static partial class StagedUpdate
{
    /// <summary>
    /// Downloads and extracts <paramref name="release"/> into <c>.updates/staging/&lt;id&gt;/VegasReborn</c>
    /// and writes its <c>plan.json</c>. The installation itself is not touched.
    /// </summary>
    /// <returns>The plan path.</returns>
    public static async Task<string> PrepareAsync(string root, Release release, IReleaseDownloader downloader,
        IProgress<UpdateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(downloader);
        root = SafePaths.Resolve(root);
        var stage = SafePaths.Beneath(root, ".updates/staging/" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(stage))
        {
            throw new UpdateException("The update could not be staged");
        }
        Directory.CreateDirectory(stage);
        var archive = Path.Combine(stage, release.Archive);
        var payload = Path.Combine(stage, "VegasReborn");
        var message = "Downloading version " + release.Version + "...";
        progress?.Report(new UpdateProgress(UpdateStage.Downloading, message, 0, release.Size));
        var bytes = progress is null ? null
            : new InlineProgress<long>(count => progress.Report(new UpdateProgress(UpdateStage.Downloading, message, count, release.Size)));
        await downloader.DownloadFileAsync(new Uri(release.BaseUrl + release.Archive), release.Size, archive, bytes, cancellationToken)
            .ConfigureAwait(false);
        progress?.Report(new UpdateProgress(UpdateStage.Verifying, "Verifying and extracting the signed update..."));
        Directory.CreateDirectory(payload);
        await Task.Run(() => UpdateArchive.Extract(archive, payload, release), cancellationToken).ConfigureAwait(false);
        var plan = Path.Combine(stage, "plan.json");
        using (var stream = new FileStream(plan, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", 1);
            writer.WriteString("root", root);
            writer.WriteString("payload", payload);
            writer.WriteString("manifest", Convert.ToBase64String(release.Manifest));
            writer.WriteString("signature", Convert.ToBase64String(release.Signature));
            writer.WriteEndObject();
        }
        return plan;
    }

    /// <summary>Re-verifies a plan: location, signature with the pinned key, and the whole payload tree.</summary>
    /// <exception cref="UpdateException">The plan or its payload is invalid.</exception>
    public static StagedPlan LoadPlan(string planPath) => LoadPlan(planPath, UpdateKey.PublicKeyHex);

    [GeneratedRegex(@"\A[A-Za-z0-9+/]*={0,2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex StrictBase64();

    internal static StagedPlan LoadPlan(string planPath, string publicKeyHex)
    {
        try
        {
            var path = SafePaths.Resolve(planPath);
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var value = document.RootElement;
            var root = SafePaths.Resolve(Json.String(value, "root"));
            var stageRoot = SafePaths.Beneath(root, ".updates/staging");
            var payload = SafePaths.Resolve(Json.String(value, "payload"));
            var stage = Path.GetDirectoryName(path)!;
            if (!Json.IsInteger(value, "schema", 1) || Path.GetFileName(path) != "plan.json"
                || Path.GetDirectoryName(stage) is not { } parent || !SafePaths.Same(parent, stageRoot)
                || !SafePaths.Same(payload, Path.Combine(stage, "VegasReborn")))
            {
                throw new FormatException();
            }
            var release = ReleaseManifest.Verify(DecodeBase64(Json.String(value, "manifest")),
                DecodeBase64(Json.String(value, "signature")), publicKeyHex);
            UpdateArchive.VerifyTree(payload, release);
            return new StagedPlan(path, root, payload, release);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or FormatException
                                          or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new UpdateException("The staged update plan is invalid");
        }
    }

    /// <summary>
    /// Verifies the plan and starts the staged executable as the install helper for the
    /// companion process <paramref name="parentProcessId"/>, which must then exit.
    /// </summary>
    /// <exception cref="GamesBlockingUpdateException">A Vegas game or server is running.</exception>
    /// <exception cref="UpdateException">The plan is invalid.</exception>
    public static void HandOff(string planPath, int parentProcessId) =>
        HandOff(planPath, parentProcessId, LoadPlan, GameProcesses.AnyRunning, StartProcess);

    internal static void HandOff(string planPath, int parentProcessId, Func<string, StagedPlan> load, Func<bool> gamesRunning,
        Func<ProcessStartInfo, int> start)
    {
        var plan = load(planPath);
        if (gamesRunning())
        {
            throw new GamesBlockingUpdateException(
                "A Vegas game or server is blocking the update. Close it or choose Force-close games and update.");
        }
        var helper = new ProcessStartInfo(Path.Combine(plan.Payload, "VegasReborn.exe"))
        {
            WorkingDirectory = plan.Payload,
            ArgumentList = { "--apply-update", planPath, "--parent-pid", parentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        };
        start(helper);
    }

    /// <summary>
    /// The install helper (<c>--apply-update</c>): verifies the plan, waits for the companion to
    /// exit, takes the install lock, installs, restarts the companion and records the result.
    /// Blocks for up to the companion's 90 s exit wait.
    /// </summary>
    /// <exception cref="UpdateException">The update was not installed.</exception>
    public static void Apply(string planPath, int parentProcessId, IProgress<UpdateProgress>? progress = null) =>
        Apply(planPath, parentProcessId, progress, LoadPlan, ParentProcess.WaitForExit,
            (root, payload, release) => UpdateInstaller.Install(root, payload, release, progress: progress), StartProcess);

    internal static void Apply(string planPath, int parentProcessId, IProgress<UpdateProgress>? progress,
        Func<string, StagedPlan> load, Action<int, string> waitForParent, Action<string, string, Release> install,
        Func<ProcessStartInfo, int> start)
    {
        progress?.Report(new UpdateProgress(UpdateStage.Verifying, "Verifying the staged update..."));
        var plan = load(planPath);
        progress?.Report(new UpdateProgress(UpdateStage.WaitingForCompanion, "Waiting for Vegas Reborn to close..."));
        waitForParent(parentProcessId, plan.Root);
        var lockPath = SafePaths.Beneath(plan.Root, ".updates/install.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        // Windows releases this byte-range lock if the helper crashes, so a stale file cannot
        // block a later update. The Python helper locks the same byte, so they exclude each other.
        using (var owner = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            if (owner.Length == 0)
            {
                owner.Seek(0, SeekOrigin.End);
                owner.WriteByte((byte)'0');
                owner.Flush();
            }
            try
            {
                owner.Lock(0, 1);
            }
            catch (IOException)
            {
                throw new UpdateException("Another update is already being installed");
            }
            try
            {
                install(plan.Root, plan.Payload, plan.Release);
            }
            finally
            {
                owner.Unlock(0, 1);
            }
        }
        progress?.Report(new UpdateProgress(UpdateStage.Restarting, "Restarting Vegas Reborn..."));
        var restarted = start(new ProcessStartInfo(Path.Combine(plan.Root, "VegasReborn.exe")) { WorkingDirectory = plan.Root });
        var record = SafePaths.Beneath(plan.Root, ".updates/last-update.json");
        File.WriteAllText(record, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["version"] = plan.Release.Version,
            ["status"] = "installed",
            ["restartedPid"] = restarted,
        }));
    }

    private static byte[] DecodeBase64(string value) =>
        StrictBase64().IsMatch(value) && value.Length % 4 == 0 ? Convert.FromBase64String(value) : throw new FormatException();

    private static int StartProcess(ProcessStartInfo info)
    {
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        // Harmless for .NET; resets a PyInstaller-built child the way 0.4.x does.
        info.Environment["PYINSTALLER_RESET_ENVIRONMENT"] = "1";
        using var process = Process.Start(info) ?? throw new UpdateException("The update helper could not be started");
        return process.Id;
    }
}

/// <summary>Synchronous <see cref="IProgress{T}"/> (no context capture), for chaining progress reports.</summary>
internal sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
