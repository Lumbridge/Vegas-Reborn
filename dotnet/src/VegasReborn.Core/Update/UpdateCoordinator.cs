namespace VegasReborn.Core.Update;

/// <summary>What the companion is doing, as far as updates care.</summary>
/// <param name="Busy">A play, host or account operation is running.</param>
/// <param name="Playing">The companion's own game launch is running (and will save the character).</param>
/// <param name="Hosting">The companion is running or starting a dedicated server.</param>
public sealed record CompanionActivity(bool Busy = false, bool Playing = false, bool Hosting = false);

/// <summary>The update operations the coordinator drives; replaceable by tests.</summary>
public interface IUpdateOperations
{
    Task<Release?> CheckLatestAsync(CancellationToken cancellationToken);
    void RecordCheck(Release? release);
    Task<string> PrepareAsync(Release release, IProgress<UpdateProgress> progress, CancellationToken cancellationToken);
    /// <summary>Starts the staged helper for this process; the App must exit afterwards.</summary>
    void HandOff(string planPath);
    /// <summary>Re-verifies a staged plan before anything is closed.</summary>
    void VerifyPlan(string planPath);
    void CloseRunningGames(IProgress<UpdateProgress> progress);
}

/// <summary>The real update operations for an installation.</summary>
public sealed class UpdateOperations(AppPaths paths, IReleaseDownloader downloader) : IUpdateOperations
{
    public Task<Release?> CheckLatestAsync(CancellationToken cancellationToken) =>
        ReleaseFeed.CheckLatestAsync(downloader, cancellationToken: cancellationToken);

    public void RecordCheck(Release? release) => ReleaseFeed.RecordCheck(paths.Root, release);

    public Task<string> PrepareAsync(Release release, IProgress<UpdateProgress> progress, CancellationToken cancellationToken) =>
        StagedUpdate.PrepareAsync(paths.Root, release, downloader, progress, cancellationToken);

    public void HandOff(string planPath) => StagedUpdate.HandOff(planPath, Environment.ProcessId);

    public void VerifyPlan(string planPath) => StagedUpdate.LoadPlan(planPath);

    public void CloseRunningGames(IProgress<UpdateProgress> progress) => GameProcesses.CloseRunning(progress);
}

/// <summary>
/// The Updates page state machine of the 0.4.x companion, without presentation. Use it from the
/// UI thread: every member and event is raised on the caller's synchronization context.
/// Render <see cref="Status"/>, show an activity bar while <see cref="Updating"/>, enable buttons
/// from <see cref="CanCheck"/>/<see cref="CanInstall"/>/<see cref="CanForceUpdate"/>, show the
/// force-close option while <see cref="UpdateBlocked"/>, and close the App (saving as on a normal
/// exit) when <see cref="ShutdownRequested"/> is raised.
/// </summary>
public sealed class UpdateCoordinator
{
    private const string GenericFailure = "The update failed. Your current app and character were preserved.";

    private readonly IUpdateOperations _operations;
    private readonly Func<CompanionActivity> _activity;
    private readonly bool _isPackaged;
    private readonly string _installedVersion;

    /// <param name="activity">Current companion activity, read whenever a decision depends on it.</param>
    /// <param name="isPackaged">
    /// False for development runs: checks are not recorded and installs are refused, as in the
    /// unfrozen Python companion.
    /// </param>
    public UpdateCoordinator(IUpdateOperations operations, Func<CompanionActivity> activity, bool isPackaged,
        string? installedVersion = null)
    {
        _operations = operations;
        _activity = activity;
        _isPackaged = isPackaged;
        _installedVersion = installedVersion ?? ReleaseInfo.CurrentVersion;
        Status = "Version " + _installedVersion + " is installed.";
    }

    public string Status { get; private set; }
    public UpdateProgress? Progress { get; private set; }
    public bool Updating { get; private set; }
    public Release? AvailableRelease { get; private set; }
    public string? StagedPlan { get; private set; }
    /// <summary>A staged update is waiting for games, a server or the companion's own launch to end.</summary>
    public bool UpdateBlocked { get; private set; }
    public bool ForceUpdatePending { get; private set; }
    public bool ForceCloseFinished { get; private set; }

    public bool CanCheck => !Updating;
    public bool CanInstall => AvailableRelease is not null && !Updating && (_activity() is var a && (!a.Busy || a.Playing));
    public bool CanForceUpdate => UpdateBlocked && StagedPlan is not null && !Updating && (_activity() is var a && (!a.Busy || a.Playing));

    /// <summary>Any displayed state changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The install helper started; the App must exit now so it can replace files.</summary>
    public event EventHandler? ShutdownRequested;

    /// <summary>Checks GitHub for a newer signed release.</summary>
    public Task CheckAsync(CancellationToken cancellationToken = default)
    {
        if (Updating)
        {
            return Task.CompletedTask;
        }
        SetStatus("Checking GitHub Releases...");
        return RunAsync(async () =>
        {
            var release = await _operations.CheckLatestAsync(cancellationToken);
            if (_isPackaged)
            {
                _operations.RecordCheck(release);
            }
            AvailableRelease = release;
            StagedPlan = null;
            UpdateBlocked = false;
            SetStatus(release is not null
                ? "Version " + release.Version + " is available. Update and restart when you are ready."
                : "Version " + _installedVersion + " · You are up to date.");
        });
    }

    /// <summary>Downloads and stages the available release if needed, then hands off to its helper.</summary>
    public Task UpdateAndRestartAsync(CancellationToken cancellationToken = default)
    {
        var activity = _activity();
        if ((activity.Busy && !activity.Playing) || Updating || AvailableRelease is not { } release)
        {
            return Task.CompletedTask;
        }
        if (!_isPackaged)
        {
            SetStatus("Run the packaged companion to install updates. Source checkouts update through Git.");
            return Task.CompletedTask;
        }
        if (StagedPlan is not null)
        {
            TryApplyStaged();
            return Task.CompletedTask;
        }
        SetStatus("Downloading and verifying " + release.Version + "...");
        return RunAsync(async () =>
        {
            StagedPlan = await _operations.PrepareAsync(release, ReportTo(), cancellationToken);
            TryApplyStaged();
        });
    }

    /// <summary>
    /// Starts the helper for the staged plan unless the companion is busy or a game blocks it.
    /// Returns true when <see cref="ShutdownRequested"/> was raised.
    /// </summary>
    public bool TryApplyStaged()
    {
        if (StagedPlan is null)
        {
            return false;
        }
        var activity = _activity();
        if (activity.Busy || activity.Hosting)
        {
            UpdateBlocked = activity.Playing || activity.Hosting;
            SetStatus(UpdateBlocked
                ? "Update ready. Close the game, or choose Force-close games and update."
                : "Update ready. Wait for the current operation to finish, then retry.");
            return false;
        }
        try
        {
            _operations.HandOff(StagedPlan);
        }
        catch (GamesBlockingUpdateException error)
        {
            UpdateBlocked = true;
            SetStatus(error.Message);
            return false;
        }
        catch (UpdateException error)
        {
            SetStatus(error.Message);
            return false;
        }
        catch (Exception)
        {
            SetStatus(GenericFailure);
            return false;
        }
        ShutdownRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// The explicit "Force-close games and update" action: re-verifies the plan, stops the
    /// companion's server through <paramref name="stopHost"/>, terminates Vegas games and servers,
    /// then hands off once the companion's own launch has saved and ended
    /// (see <see cref="ActivityChanged"/>). Automatic checks never close games.
    /// </summary>
    public Task ForceUpdateAsync(Action? stopHost = null)
    {
        var activity = _activity();
        if (Updating || StagedPlan is not { } plan || !UpdateBlocked || (activity.Busy && !activity.Playing))
        {
            return Task.CompletedTask;
        }
        ForceUpdatePending = true;
        ForceCloseFinished = false;
        SetStatus("Verifying the update before closing games...");
        return RunAsync(async () =>
        {
            var progress = ReportTo();
            await Task.Run(() =>
            {
                _operations.VerifyPlan(plan);
                if (activity.Hosting)
                {
                    stopHost?.Invoke();
                }
                _operations.CloseRunningGames(progress);
            });
            ForceCloseFinished = true;
            SetStatus("Games closed. Waiting for character saving and server shutdown...");
        });
    }

    /// <summary>Call whenever <see cref="CompanionActivity"/> changes; completes a pending force update.</summary>
    public void ActivityChanged()
    {
        var activity = _activity();
        if (ForceUpdatePending && ForceCloseFinished && !activity.Busy && !activity.Hosting)
        {
            ForceUpdatePending = false;
            Updating = false;
            SetStatus("Starting the update installer...");
            if (TryApplyStaged())
            {
                return;
            }
        }
        OnChanged();
    }

    private async Task RunAsync(Func<Task> operation)
    {
        Updating = true;
        OnChanged();
        try
        {
            await operation();
        }
        catch (Exception error)
        {
            ForceUpdatePending = false;
            ForceCloseFinished = false;
            SetStatus(error is UpdateException ? error.Message : GenericFailure);
        }
        finally
        {
            if (!ForceUpdatePending)
            {
                Updating = false;
            }
        }
        ActivityChanged();
    }

    private InlineProgress<UpdateProgress> ReportTo()
    {
        var context = SynchronizationContext.Current;
        return new InlineProgress<UpdateProgress>(report =>
        {
            void Apply()
            {
                Progress = report;
                SetStatus(report.Message);
            }
            if (context is null)
            {
                Apply();
            }
            else
            {
                context.Post(_ => Apply(), null);
            }
        });
    }

    private void SetStatus(string status)
    {
        Status = status;
        OnChanged();
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
