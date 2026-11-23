using VegasReborn.Core.Update;

namespace VegasReborn.Core.Tests.Update;

public sealed class CommandLineTests : IDisposable
{
    private readonly UpdateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void AcceptsTheExactPythonHelperCommandLine()
    {
        // updater.launch_helper: [exe, "--apply-update", str(plan), "--parent-pid", str(parent_pid)]
        var plan = @"C:\Games\Vegas Reborn\.updates\staging\0123456789abcdef0123456789abcdef\plan.json";
        Assert.Equal(new ApplyUpdateCommand(plan, 5120), UpdateCommandLine.Parse(["--apply-update", plan, "--parent-pid", "5120"]));
        Assert.Equal(new ApplyUpdateCommand(plan, 5120), UpdateCommandLine.Parse(["--parent-pid=5120", $"--apply-update={plan}"]));
        Assert.Equal(new ApplyUpdateCommand(plan, 0), UpdateCommandLine.Parse(["--apply-update", plan]));
    }

    [Fact]
    public void RecognisesStagedHandOffAndIgnoresNormalStarts()
    {
        Assert.Equal(new InstallStagedUpdateCommand("plan.json"), UpdateCommandLine.Parse(["--install-staged-update", "plan.json"]));
        Assert.Null(UpdateCommandLine.Parse([]));
        Assert.Null(UpdateCommandLine.Parse(["--play"]));
        Assert.Null(UpdateCommandLine.Parse(["--parent-pid", "12"]));
        Assert.Null(UpdateCommandLine.TryRun(["--host"]));
    }

    [Fact]
    public void InvalidArgumentsExitWithArgparseStatus()
    {
        Assert.IsType<InvalidUpdateCommand>(UpdateCommandLine.Parse(["--apply-update", "plan.json", "--parent-pid", "abc"]));
        Assert.IsType<InvalidUpdateCommand>(UpdateCommandLine.Parse(["--apply-update"]));
        Assert.Equal(2, UpdateCommandLine.TryRun(["--apply-update", "plan.json", "--parent-pid", "x"]));
    }

    [Fact]
    public void HelperWithInvalidPlanFailsWithoutTouchingAnything()
    {
        var root = _fixture.InstallRoot();
        var plan = Path.Combine(root, ".updates", "staging", "x", "plan.json");
        Directory.CreateDirectory(Path.GetDirectoryName(plan)!);
        File.WriteAllText(plan, "{}");
        StagedUpdateHelper? shown = null;
        var code = UpdateCommandLine.TryRun(["--apply-update", plan, "--parent-pid", "1"], helper =>
        {
            shown = helper;
            return helper.RunAsync().GetAwaiter().GetResult();
        });
        Assert.Equal(1, code);
        Assert.NotNull(shown);
        Assert.Equal(UpdateHelperState.Failed, shown.State);
        Assert.Equal("The staged update plan is invalid", shown.ErrorMessage);
        Assert.Equal("Update not completed.", shown.Status);
        Assert.Equal(1, UpdateCommandLine.TryRun(["--install-staged-update", plan]));
        UpdateFixture.AssertSentinels(root);
    }

    [Fact]
    public void UpdaterOnlyPackageWithoutHelpersNeedsTheFullDownload()
    {
        var paths = new AppPaths(_fixture.InstallRoot());
        Assert.False(UpdaterOnlyPackage.NeedsFullInstall(paths, "0.5.0"));
        Directory.CreateDirectory(Path.Combine(paths.Root, "runtime-0.5.0"));
        File.WriteAllText(Path.Combine(paths.Root, "runtime-0.5.0", "updater-only"), "");
        Assert.True(UpdaterOnlyPackage.NeedsFullInstall(paths, "0.5.0"));
        Directory.CreateDirectory(paths.NativeBin);
        File.WriteAllText(Path.Combine(paths.NativeBin, "VegasSteamGate.dll"), "gate");
        Assert.False(UpdaterOnlyPackage.NeedsFullInstall(paths, "0.5.0"));
    }
}

public sealed class StagedUpdateHelperTests
{
    [Fact]
    public async Task ReportsProgressAndSucceeds()
    {
        var helper = new StagedUpdateHelper(progress =>
            progress.Report(new UpdateProgress(UpdateStage.Installing, "Installing update files: 1 of 2", 1, 2)));
        var seen = new List<string>();
        helper.ProgressChanged += (_, report) => seen.Add(report.Message);
        Assert.Equal("Preparing the update...", helper.Status);
        Assert.False(helper.CanClose);
        Assert.Equal(0, await helper.RunAsync());
        Assert.Equal(["Installing update files: 1 of 2"], seen);
        Assert.Equal(UpdateHelperState.Succeeded, helper.State);
        Assert.True(helper.CanClose);
    }

    [Fact]
    public async Task UnexpectedErrorsUseTheFixedMessage()
    {
        var helper = new StagedUpdateHelper(_ => throw new InvalidOperationException("internal detail"));
        var finished = 0;
        helper.Finished += (_, _) => finished++;
        Assert.Equal(1, await helper.RunAsync());
        Assert.Equal("The update could not finish. Open Vegas Reborn again to retry.", helper.ErrorMessage);
        Assert.Equal(1, finished);

        var blocked = new StagedUpdateHelper(_ => throw new GamesBlockingUpdateException("A Vegas game or server is blocking the update. Close it and retry."));
        Assert.Equal(1, await blocked.RunAsync());
        Assert.Equal("A Vegas game or server is blocking the update. Close it and retry.", blocked.ErrorMessage);
    }
}
