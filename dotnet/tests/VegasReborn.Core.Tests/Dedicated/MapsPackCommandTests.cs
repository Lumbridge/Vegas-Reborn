using System.IO.Compression;
using System.Text;
using System.Text.Json;
using VegasReborn.Core.Content;
using VegasReborn.Core.Launch;
using VegasReborn.Core.Tests.Content;
using VegasReborn.Core.Tests.Update;
using VegasReborn.Core.Update;
using VegasReborn.Server;

namespace VegasReborn.Core.Tests.Dedicated;

public sealed class MapsPackCommandTests : IDisposable
{
    private const string Base = "https://example.test/pack/";
    private const string KitchenPath = "KellerGame/Content/CookedPc/MapsPack/PAK_01_Kitchen/PAK_01_Kitchen.usdx";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "vr-mapspack-cli-" + Guid.NewGuid().ToString("N"));
    private readonly UpdateFixture _fixture = new();
    private readonly ServingDownloader _server = new();

    public void Dispose()
    {
        _fixture.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    /// <summary>Publishes a signed one-archive Maps Pack at <see cref="Base"/>.</summary>
    private void Publish(int version, string content)
    {
        var data = Encoding.UTF8.GetBytes(content);
        byte[] zip;
        using (var memory = new MemoryStream())
        {
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            using (var stream = archive.CreateEntry(KitchenPath).Open())
            {
                stream.Write(data);
            }
            zip = memory.ToArray();
        }
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["schema"] = 1,
            ["kind"] = "content",
            ["id"] = "vegas1-maps-pack",
            ["version"] = version,
            ["title"] = "vegas1",
            ["archives"] = new[] { new Dictionary<string, object> { ["name"] = "a.zip", ["bytes"] = zip.Length, ["sha256"] = PrivateFile.Sha256(zip) } },
            ["files"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["path"] = KitchenPath, ["bytes"] = data.Length, ["sha256"] = PrivateFile.Sha256(data), ["archive"] = "a.zip",
                },
            },
        });
        _server.Items[Base + "content.json"] = manifest;
        _server.Items[Base + "content.sig"] = _fixture.Sign(manifest);
        _server.Items[Base + "a.zip"] = zip;
    }

    private async Task<(int Code, string Output, string Error)> InstallAsync(bool force = false)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var paths = new AppPaths(_root);
        var code = await MapsPackCommand.RunAsync("install", paths, () => new Lock(),
            () => new MapsPackInstaller(paths, _server, _fixture.PublicKeyHex, Base), output, error, CancellationToken.None, force);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task InstallSkipsTheDownloadWhenThePublishedVersionIsInstalled()
    {
        Publish(1, "kitchen");
        Assert.Equal(0, (await InstallAsync()).Code);
        Assert.Equal(1, _server.Archives);

        var (code, output, _) = await InstallAsync();

        Assert.Equal(0, code);
        Assert.Equal("Maps Pack already installed (version 1).", output.Trim());
        Assert.Equal(1, _server.Archives);
    }

    [Fact]
    public async Task ForceReinstallsTheInstalledVersion()
    {
        Publish(1, "kitchen");
        await InstallAsync();

        var (code, output, _) = await InstallAsync(force: true);

        Assert.Equal(0, code);
        Assert.Equal(2, _server.Archives);
        Assert.Contains("Installed (version 1)", output);
    }

    [Fact]
    public async Task NewerPublishedVersionIsInstalled()
    {
        Publish(1, "kitchen");
        await InstallAsync();
        Publish(2, "kitchen2");

        var (code, output, _) = await InstallAsync();

        Assert.Equal(0, code);
        Assert.Equal(2, _server.Archives);
        Assert.Contains("Installed (version 2)", output);
    }

    [Fact]
    public void DamagedPackAfterInstallFails()
    {
        var paths = new AppPaths(_root);
        MapsPackFixture.Install(paths, MapsPackFixture.Default());
        File.WriteAllText(Path.Combine(paths.MapsPack, "files", MapsPackFixture.Map), "map data!");
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(1, MapsPackCommand.ReportInstall(paths, output, error));

        Assert.Equal("The Maps Pack is damaged. Download it again or remove it.", error.ToString().Trim());
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void InstalledPackAfterInstallSucceeds()
    {
        var paths = new AppPaths(_root);
        MapsPackFixture.Install(paths, MapsPackFixture.Default());
        var output = new StringWriter();

        Assert.Equal(0, MapsPackCommand.ReportInstall(paths, output, new StringWriter()));

        Assert.Contains("Installed (version 1)", output.ToString());
    }

    [Fact]
    public void ParsesForceForInstall()
    {
        Assert.True(Program.Options.Parse(["maps-pack", "install", "--force"]).Force);
        Assert.False(Program.Options.Parse(["maps-pack", "install"]).Force);
    }

    [Theory]
    [InlineData("maps-pack remove --force")]
    [InlineData("maps-pack status --force")]
    [InlineData("run --force")]
    public void RejectsForceElsewhere(string line)
    {
        var error = Assert.Throws<Program.UsageException>(() => Program.Options.Parse(line.Split(' ')));
        Assert.Equal("Unknown option: --force", error.Message);
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

    /// <summary>Serves published bytes and counts archive downloads.</summary>
    private sealed class ServingDownloader : IReleaseDownloader
    {
        public Dictionary<string, byte[]> Items { get; } = new();
        public int Archives { get; private set; }

        public Task<byte[]> DownloadBytesAsync(Uri url, long limit, CancellationToken cancellationToken = default) =>
            Items.TryGetValue(url.AbsoluteUri, out var data)
                ? Task.FromResult(data)
                : throw new UpdateException("Could not download the update. Check your connection and retry.");

        public async Task<long> DownloadFileAsync(Uri url, long limit, string destination, IProgress<long>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Archives++;
            var data = Items[url.AbsoluteUri];
            await File.WriteAllBytesAsync(destination, data, cancellationToken);
            progress?.Report(data.Length);
            return data.Length;
        }
    }

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
