using System.Diagnostics;
using VegasReborn.Core.Update;

namespace VegasReborn.Core.Tests.Update;

/// <summary>Force-close safety with fake process handles; never terminates a real game.</summary>
public sealed class CloseGameTests
{
    private sealed class FakeApi(string? image, params uint[] waits) : IProcessApi
    {
        private readonly Queue<uint> _waits = new(waits);
        public int OpenError { get; init; }
        public bool TerminateResult { get; init; } = true;
        public List<nint> Terminated { get; } = [];
        public List<nint> Closed { get; } = [];
        public int Opened { get; private set; }

        public nint Open(uint access, int processId, out int error)
        {
            Opened++;
            error = OpenError;
            return OpenError == 0 ? 100 : 0;
        }

        public uint Wait(nint handle, uint milliseconds) => _waits.Dequeue();
        public string? QueryImage(nint handle) => image;
        public bool Terminate(nint handle, uint exitCode) { Terminated.Add(handle); return TerminateResult; }
        public void Close(nint handle) => Closed.Add(handle);
    }

    [Fact]
    public void ClosesVerifiedImageAndWaitsOnTheSameHandle()
    {
        var api = new FakeApi(@"C:\Owned Vegas\R6Vegas_Game.exe", 258, 0);
        GameProcesses.CloseRunning(null, api, () => [123], () => false);
        Assert.Equal([100], api.Terminated);
        Assert.Equal([100], api.Closed);
    }

    [Theory]
    [InlineData(@"C:\Owned Vegas 2\Binaries\R6Vegas2_Game.exe")]
    [InlineData(@"C:\Owned Vegas 2\Binaries\RainbowSixVegas2_SADS.exe")]
    [InlineData(@"C:\Owned Vegas\Binaries\r6vegasserver.EXE")]
    public void Vegas2AndServerImagesAreClosedToo(string image)
    {
        var api = new FakeApi(image, 258, 0);
        GameProcesses.CloseRunning(null, api, () => [123], () => false);
        Assert.Equal([100], api.Terminated);
    }

    [Fact]
    public void ReusedPidWithOtherImageIsNotTerminated()
    {
        var api = new FakeApi(@"C:\Other\Steam.exe", 258, 0);
        GameProcesses.CloseRunning(null, api, () => [123], () => false);
        Assert.Empty(api.Terminated);
        Assert.Equal([100], api.Closed);
    }

    [Fact]
    public void EmptySnapshotHasNoTerminationSideEffect()
    {
        var api = new FakeApi(null);
        var reports = new List<UpdateProgress>();
        GameProcesses.CloseRunning(new InlineProgressFor(reports), api, () => [], () => false);
        Assert.Equal(0, api.Opened);
        Assert.Empty(reports);
    }

    [Fact]
    public void GameExitingDuringTerminationIsAlreadyClosed()
    {
        var api = new FakeApi(@"C:\Owned Vegas\R6VegasServer.exe", 258, 0, 0) { TerminateResult = false };
        GameProcesses.CloseRunning(null, api, () => [123], () => false);
        Assert.Equal([100], api.Closed);
    }

    [Fact]
    public void ExitedProcessIsSkippedAndStillRunningGamesBlock()
    {
        var gone = new FakeApi(null) { OpenError = 87 };
        var error = Assert.Throws<GamesBlockingUpdateException>(() => GameProcesses.CloseRunning(null, gone, () => [123], () => true));
        Assert.Equal("A Vegas game is still running. Close it or retry force-close.", error.Message);
        var denied = new FakeApi(null) { OpenError = 5 };
        Assert.Throws<UpdateException>(() => GameProcesses.CloseRunning(null, denied, () => [123], () => false));
    }

    [Fact]
    public void GameThatDoesNotExitIsReported()
    {
        var api = new FakeApi(@"C:\Owned Vegas\R6Vegas_Game.exe", 258, 258);
        Assert.Equal("A Vegas game did not close. Close it manually and retry.",
            Assert.Throws<UpdateException>(() => GameProcesses.CloseRunning(null, api, () => [123], () => false)).Message);
        Assert.Equal([100], api.Closed);
    }

    [Fact]
    public void BlockingListCoversBothGames()
    {
        Assert.Equal(new HashSet<string> { "R6Vegas_Game.exe", "R6VegasServer.exe", "R6Vegas2_Game.exe", "RainbowSixVegas2_SADS.exe" },
            GameProcesses.BlockingImageNames.ToHashSet());
        Assert.DoesNotContain(Environment.ProcessId, GameProcesses.FindRunning());
    }
}

public sealed class ParentProcessTests : IDisposable
{
    private readonly UpdateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static Process StartHidden(string arguments) => Process.Start(new ProcessStartInfo("cmd.exe", arguments)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
    })!;

    [Fact]
    public void ExitedParentWithOpenCreatorHandleIsAlreadySafe()
    {
        using var child = StartHidden("/c exit 0");
        child.WaitForExit(10000);
        // The Process object keeps its handle, reproducing the real updater race.
        ParentProcess.WaitForExit(child.Id, Path.Combine(_fixture.Root, "installation"));
    }

    [Fact]
    public void LiveUnrelatedParentIsRejectedWithoutWaiting()
    {
        using var child = StartHidden("/c ping -n 30 127.0.0.1 >nul");
        try
        {
            var timer = Stopwatch.StartNew();
            var error = Assert.Throws<UpdateException>(() => ParentProcess.WaitForExit(child.Id, Path.Combine(_fixture.Root, "installation")));
            Assert.Equal("The updater parent is not this companion installation", error.Message);
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10));
        }
        finally
        {
            child.Kill(entireProcessTree: true);
            child.WaitForExit(10000);
        }
    }

    [Fact]
    public void MissingParentPidIsRefused()
    {
        Assert.Equal("The updater requires a Windows companion process",
            Assert.Throws<UpdateException>(() => ParentProcess.WaitForExit(0, _fixture.Root)).Message);
    }

    [Fact]
    public void ParentImageMustBeTheInstalledExecutable()
    {
        // The test host's own image, compared with its own folder, is accepted as the parent;
        // a live parent then has to exit, so only check the image comparison via a fake.
        var image = Path.Combine(_fixture.Root, "VegasReborn.exe");
        var api = new SingleWaitApi(image);
        ParentProcess.WaitForExit(42, _fixture.Root, api);
        Assert.Equal([0u, 90000u], api.Waits);
        Assert.Throws<UpdateException>(() => ParentProcess.WaitForExit(42, Path.Combine(_fixture.Root, "other"), new SingleWaitApi(image)));
    }

    private sealed class SingleWaitApi(string image) : IProcessApi
    {
        public List<uint> Waits { get; } = [];
        public nint Open(uint access, int processId, out int error) { error = 0; return 7; }
        public uint Wait(nint handle, uint milliseconds) { Waits.Add(milliseconds); return milliseconds == 0 ? 258u : 0u; }
        public string? QueryImage(nint handle) => image;
        public bool Terminate(nint handle, uint exitCode) => throw new InvalidOperationException();
        public void Close(nint handle) { }
    }
}
