using VegasReborn.Core.Update;

namespace VegasReborn.Core.Tests.Update;

/// <summary>Updates page behaviour with synthetic signed-release operations.</summary>
public sealed class CoordinatorTests
{
    private static Release Release(string version) => new(version, $"VegasReborn-{version}-win64.zip", new string('0', 64), 1, [], [], []);

    private sealed class FakeOperations : IUpdateOperations
    {
        public Release? Latest { get; set; }
        public List<string> HandOffs { get; } = [];
        public List<string> Recorded { get; } = [];
        public Exception? HandOffError { get; set; }
        public Exception? VerifyError { get; set; }
        public Exception? CloseError { get; set; }
        public int ClosedGames { get; private set; }
        public Release? Prepared { get; private set; }

        public Task<Release?> CheckLatestAsync(CancellationToken cancellationToken) => Task.FromResult(Latest);
        public void RecordCheck(Release? release) => Recorded.Add(release?.Version ?? "none");

        public Task<string> PrepareAsync(Release release, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
        {
            Prepared = release;
            return Task.FromResult("synthetic-plan.json");
        }

        public void HandOff(string planPath)
        {
            if (HandOffError is not null)
            {
                throw HandOffError;
            }
            HandOffs.Add(planPath);
        }

        public void VerifyPlan(string planPath)
        {
            if (VerifyError is not null)
            {
                throw VerifyError;
            }
        }

        public void CloseRunningGames(IProgress<UpdateProgress> progress)
        {
            if (CloseError is not null)
            {
                throw CloseError;
            }
            ClosedGames++;
        }
    }

    private CompanionActivity _activity = new();
    private readonly FakeOperations _operations = new();

    private UpdateCoordinator Create(bool packaged = true)
    {
        var coordinator = new UpdateCoordinator(_operations, () => _activity, packaged, "0.5.0");
        return coordinator;
    }

    [Fact]
    public async Task CheckDownloadVerifyHandOffWithoutTouchingARealInstall()
    {
        var coordinator = Create();
        var shutdown = 0;
        coordinator.ShutdownRequested += (_, _) => shutdown++;
        Assert.False(coordinator.CanInstall);
        _operations.Latest = Release("0.5.1");
        await coordinator.CheckAsync();
        Assert.Equal("Version 0.5.1 is available. Update and restart when you are ready.", coordinator.Status);
        Assert.Equal(["0.5.1"], _operations.Recorded);
        Assert.True(coordinator.CanInstall);
        await coordinator.UpdateAndRestartAsync();
        Assert.Same(_operations.Latest, _operations.Prepared);
        Assert.Equal(["synthetic-plan.json"], _operations.HandOffs);
        Assert.Equal(1, shutdown);
    }

    [Fact]
    public async Task UpToDateAndDevelopmentRuns()
    {
        var coordinator = Create(packaged: false);
        Assert.Equal("Version 0.5.0 is installed.", coordinator.Status);
        await coordinator.CheckAsync();
        Assert.Equal("Version 0.5.0 · You are up to date.", coordinator.Status);
        Assert.Empty(_operations.Recorded);
        _operations.Latest = Release("0.5.1");
        await coordinator.CheckAsync();
        await coordinator.UpdateAndRestartAsync();
        Assert.Equal("Run the packaged launcher to install updates. Source checkouts update through Git.", coordinator.Status);
        Assert.Null(_operations.Prepared);
    }

    [Fact]
    public async Task HandOffIsBlockedDuringGameOrHost()
    {
        var coordinator = Create();
        _operations.Latest = Release("0.5.1");
        await coordinator.CheckAsync();
        _activity = new CompanionActivity(Busy: true);
        await coordinator.UpdateAndRestartAsync();
        Assert.False(coordinator.TryApplyStaged());
        _activity = new CompanionActivity(Hosting: true);
        await coordinator.UpdateAndRestartAsync();
        Assert.False(coordinator.TryApplyStaged());
        Assert.True(coordinator.UpdateBlocked);
        Assert.Equal("Update ready. Close the game, or choose Force-close games and update.", coordinator.Status);
        Assert.Empty(_operations.HandOffs);
    }

    [Fact]
    public async Task PlayingCanStageAndForceUpdateWaitsForCharacterSave()
    {
        var coordinator = Create();
        var shutdown = 0;
        coordinator.ShutdownRequested += (_, _) => shutdown++;
        _operations.Latest = Release("99.0.0");
        await coordinator.CheckAsync();
        _activity = new CompanionActivity(Busy: true, Playing: true);
        Assert.True(coordinator.CanInstall);
        await coordinator.UpdateAndRestartAsync();
        Assert.Equal("synthetic-plan.json", coordinator.StagedPlan);
        Assert.False(coordinator.Updating);
        Assert.True(coordinator.UpdateBlocked);
        Assert.True(coordinator.CanForceUpdate);
        Assert.Equal(0, _operations.ClosedGames);

        await coordinator.ForceUpdateAsync();
        Assert.True(coordinator.ForceCloseFinished);
        Assert.True(coordinator.Updating);
        Assert.Equal("Games closed. Waiting for character saving and server shutdown...", coordinator.Status);
        Assert.Empty(_operations.HandOffs); // Own launcher still saving the character.

        _activity = new CompanionActivity();
        coordinator.ActivityChanged();
        Assert.Equal(["synthetic-plan.json"], _operations.HandOffs);
        Assert.Equal(1, _operations.ClosedGames);
        Assert.Equal(1, shutdown);
    }

    [Fact]
    public async Task FailedForceCloseKeepsCompanionAndStagedUpdate()
    {
        var coordinator = await BlockedWithStagedPlan();
        _operations.CloseError = new UpdateException("Close it manually");
        await coordinator.ForceUpdateAsync();
        Assert.False(coordinator.Updating);
        Assert.Equal("Close it manually", coordinator.Status);
        Assert.Equal("synthetic-plan.json", coordinator.StagedPlan);
        Assert.Empty(_operations.HandOffs);
    }

    [Fact]
    public async Task TamperedStagedUpdateDoesNotCloseGames()
    {
        var coordinator = await BlockedWithStagedPlan();
        _operations.VerifyError = new UpdateException("The staged update plan is invalid");
        await coordinator.ForceUpdateAsync();
        Assert.Equal(0, _operations.ClosedGames);
        Assert.False(coordinator.Updating);
        Assert.Equal("The staged update plan is invalid", coordinator.Status);
    }

    [Fact]
    public async Task ExternalGameBlockExposesForceAction()
    {
        _operations.HandOffError = new GamesBlockingUpdateException("Game blocked");
        var coordinator = Create();
        _operations.Latest = Release("0.5.1");
        await coordinator.CheckAsync();
        await coordinator.UpdateAndRestartAsync();
        Assert.True(coordinator.UpdateBlocked);
        Assert.True(coordinator.CanForceUpdate);
        Assert.Equal("Game blocked", coordinator.Status);
    }

    private async Task<UpdateCoordinator> BlockedWithStagedPlan()
    {
        var coordinator = Create();
        _operations.Latest = Release("0.5.1");
        await coordinator.CheckAsync();
        _activity = new CompanionActivity(Busy: true, Playing: true);
        await coordinator.UpdateAndRestartAsync();
        Assert.True(coordinator.UpdateBlocked);
        return coordinator;
    }
}
