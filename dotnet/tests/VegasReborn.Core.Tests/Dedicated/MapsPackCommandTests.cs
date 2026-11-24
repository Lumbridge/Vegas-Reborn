using VegasReborn.Core.Content;
using VegasReborn.Core.Update;
using VegasReborn.Server;

namespace VegasReborn.Core.Tests.Dedicated;

public sealed class MapsPackCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vr-mapspack-cli-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Theory]
    [InlineData("install")]
    [InlineData("remove")]
    [InlineData("status")]
    public void ParsesEachVerb(string verb)
    {
        var options = Program.Options.Parse(["maps-pack", verb, "--root", @"C:\VR"]);
        Assert.Equal("maps-pack", options.Command);
        Assert.Equal(verb, options.Verb);
        Assert.Equal(@"C:\VR", options.Root);
    }

    [Theory]
    [InlineData("maps-pack")]
    [InlineData("maps-pack --root C:/VR")]
    public void RejectsMissingVerb(string line)
    {
        var error = Assert.Throws<Program.UsageException>(() => Program.Options.Parse(line.Split(' ')));
        Assert.Contains("install, remove or status", error.Message);
    }

    [Fact]
    public void RejectsUnknownVerb()
    {
        var error = Assert.Throws<Program.UsageException>(() => Program.Options.Parse(["maps-pack", "update"]));
        Assert.Contains("update", error.Message);
    }

    [Fact]
    public void RejectsVegas2()
    {
        var error = Assert.Throws<Program.UsageException>(() => Program.Options.Parse(["maps-pack", "install", "--game", "vegas2"]));
        Assert.Equal("The Maps Pack is only for Rainbow Six Vegas", error.Message);
    }

    [Fact]
    public void AcceptsExplicitVegas1()
    {
        Assert.Equal("install", Program.Options.Parse(["maps-pack", "install", "--game", "vegas1"]).Verb);
    }

    [Fact]
    public void OtherCommandsStillParse()
    {
        var options = Program.Options.Parse(["run", "--game", "vegas2", "--local-test"]);
        Assert.Equal("run", options.Command);
        Assert.Null(options.Verb);
        Assert.True(options.LocalTest);
    }

    [Theory]
    [InlineData("install")]
    [InlineData("remove")]
    public async Task InstallAndRemoveRefuseWhileTheServerRuns(string verb)
    {
        var error = new StringWriter();
        var code = await MapsPackCommand.RunAsync(verb, new AppPaths(_root), () => null,
            () => throw new InvalidOperationException("must not start"), new StringWriter(), error, CancellationToken.None);
        Assert.Equal(1, code);
        Assert.Contains("Stop the server first", error.ToString());
    }

    [Fact]
    public async Task StatusReportsNotInstalled()
    {
        var output = new StringWriter();
        var code = await MapsPackCommand.RunAsync("status", new AppPaths(_root), () => null,
            () => throw new InvalidOperationException("must not start"), output, new StringWriter(), CancellationToken.None);
        Assert.Equal(0, code);
        Assert.Equal("Not installed", output.ToString().Trim());
    }

    [Fact]
    public async Task RemoveHoldsTheHostLockAndReleasesIt()
    {
        var output = new StringWriter();
        var held = new Lock();
        var paths = new AppPaths(_root);
        var code = await MapsPackCommand.RunAsync("remove", paths, () => held,
            () => new MapsPackInstaller(paths, new ReleaseDownloader()), output, new StringWriter(), CancellationToken.None);
        Assert.Equal(0, code);
        Assert.True(held.Disposed);
        Assert.Contains("removed", output.ToString());
    }

    [Fact]
    public async Task CancellingAnInstallPrintsCancelledAndReleasesTheLock()
    {
        var error = new StringWriter();
        var held = new Lock();
        var paths = new AppPaths(_root);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var code = await MapsPackCommand.RunAsync("install", paths, () => held,
            () => new MapsPackInstaller(paths, new CancellingDownloader()), new StringWriter(), error, cancel.Token);
        Assert.Equal(1, code);
        Assert.Equal("Cancelled.", error.ToString().Trim());
        Assert.True(held.Disposed);
    }

    [Fact]
    public void DescribesEachState()
    {
        Assert.Equal("Not installed", MapsPackCommand.Describe(MapsPackState.NotInstalled, null));
        Assert.Equal("Damaged", MapsPackCommand.Describe(MapsPackState.Damaged, null));
        Assert.Equal("Installed (version 3)", MapsPackCommand.Describe(MapsPackState.Installed, new MapsPackRecord(3, [])));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 200, 0)]
    [InlineData(50, 200, 25)]
    [InlineData(200, 200, 100)]
    [InlineData(300, 200, 100)]
    public void ComputesPercent(long bytes, long total, int expected) =>
        Assert.Equal(expected, MapsPackCommand.Percent(new MapsPackProgress(bytes, total)));

    private sealed class CancellingDownloader : IReleaseDownloader
    {
        public Task<byte[]> DownloadBytesAsync(Uri url, long limit, CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException(cancellationToken);

        public Task<long> DownloadFileAsync(Uri url, long limit, string destination, IProgress<long>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException(cancellationToken);
    }

    private sealed class Lock : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
