using System.Diagnostics;
using System.Text.Json;
using VegasReborn.Core.Update;

namespace VegasReborn.Core.Tests.Update;

public sealed class InstallTests : IDisposable
{
    private readonly UpdateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void InstallPreservesAccountsSettingsGameAndExistingConfig()
    {
        var (payload, release) = _fixture.Payload();
        var root = _fixture.InstallRoot();
        var reports = new List<UpdateProgress>();
        UpdateInstaller.Install(root, payload, release, () => false, new InlineProgressFor(reports));
        Assert.Equal("new companion", File.ReadAllText(Path.Combine(root, "VegasReborn.exe")));
        Assert.Equal("0.5.1", UpdateArchive.ReadVersion(Path.Combine(root, "version.json")));
        Assert.Equal("new runtime", File.ReadAllText(Path.Combine(root, "runtime-0.5.1/VegasReborn.Core.dll")));
        UpdateFixture.AssertSentinels(root);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(root, ".updates/backups"), "VegasReborn.exe", SearchOption.AllDirectories));
        Assert.Equal("Installing update files: 5 of 5", reports[^1].Message);
        Assert.Empty(Directory.GetFiles(root, "*.update-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void SeedFilesAreInstalledOnlyWhenAbsent()
    {
        var (payload, release) = _fixture.Payload();
        var root = _fixture.InstallRoot();
        File.Delete(Path.Combine(root, "local/companion/service.json"));
        UpdateInstaller.Install(root, payload, release, () => false);
        Assert.Equal("public defaults", File.ReadAllText(Path.Combine(root, "local/companion/service.json")));
    }

    [Fact]
    public void ExecutableIsInstalledLast()
    {
        var (payload, release) = _fixture.Payload();
        var root = _fixture.InstallRoot();
        var reports = new List<UpdateProgress>();
        var seenExecutable = new List<string>();
        UpdateInstaller.Install(root, payload, release, () => false, new InlineProgressFor(reports, _ =>
            seenExecutable.Add(File.ReadAllText(Path.Combine(root, "VegasReborn.exe")))));
        // Before the last file is installed the old executable is still in place.
        Assert.All(seenExecutable, text => Assert.Equal("old companion", text));
    }

    [Fact]
    public void ActiveGameBlocksAllInstallChanges()
    {
        var (payload, release) = _fixture.Payload();
        var root = _fixture.InstallRoot();
        Assert.Throws<GamesBlockingUpdateException>(() => UpdateInstaller.Install(root, payload, release, () => true));
        Assert.Equal("old companion", File.ReadAllText(Path.Combine(root, "VegasReborn.exe")));
        Assert.False(Directory.Exists(Path.Combine(root, "runtime-0.5.1")));
    }

    [Fact]
    public void FailedReplaceRollsBackManagedFiles()
    {
        var (payload, release) = _fixture.Payload();
        var root = _fixture.InstallRoot();
        // A running executable can be read but not replaced: the last step fails.
        using (new FileStream(Path.Combine(root, "VegasReborn.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Assert.Throws<UpdateException>(() => UpdateInstaller.Install(root, payload, release, () => false));
            Assert.Equal("Update could not be installed. The previous app was restored; close other companion windows and retry.", error.Message);
        }
        Assert.Equal("old readme", File.ReadAllText(Path.Combine(root, "README.txt")));
        Assert.Equal("old companion", File.ReadAllText(Path.Combine(root, "VegasReborn.exe")));
        Assert.Equal("0.4.14", UpdateArchive.ReadVersion(Path.Combine(root, "version.json")));
        Assert.False(File.Exists(Path.Combine(root, "runtime-0.5.1/VegasReborn.Core.dll")));
        UpdateFixture.AssertSentinels(root);
    }

    [Fact]
    public void PayloadTamperingExtraFileAndDowngradeRejected()
    {
        var (payload, release) = _fixture.Payload();
        var root = _fixture.InstallRoot();
        var extra = Path.Combine(payload, "runtime-0.5.1/unsigned.dll");
        File.WriteAllText(extra, "unsigned");
        Assert.Throws<UpdateException>(() => UpdateInstaller.Install(root, payload, release, () => false));
        File.Delete(extra);
        File.WriteAllText(Path.Combine(payload, "README.txt"), "new readmf");
        Assert.Throws<UpdateException>(() => UpdateInstaller.Install(root, payload, release, () => false));
        File.WriteAllText(Path.Combine(payload, "README.txt"), "new readme");
        File.WriteAllText(Path.Combine(root, "version.json"), """{"version":"0.5.2"}""");
        var error = Assert.Throws<UpdateException>(() => UpdateInstaller.Install(root, payload, release, () => false));
        Assert.Contains("not newer", error.Message);
        File.WriteAllText(Path.Combine(root, "version.json"), """{"version":"0.5.1"}""");
        Assert.Throws<UpdateException>(() => UpdateInstaller.Install(root, payload, release, () => false));
        File.WriteAllText(Path.Combine(root, "version.json"), "not json");
        Assert.Equal("Could not verify the installed companion version",
            Assert.Throws<UpdateException>(() => UpdateInstaller.Install(root, payload, release, () => false)).Message);
        Assert.Equal("old companion", File.ReadAllText(Path.Combine(root, "VegasReborn.exe")));
    }

    [Fact]
    public void JunctionInPayloadIsRejected()
    {
        var (payload, release) = _fixture.Payload();
        var outside = Directory.CreateDirectory(Path.Combine(_fixture.Root, "outside")).FullName;
        var link = Path.Combine(payload, "runtime-0.5.1", "linked");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (IOException)
        {
            return; // Symbolic links need Developer Mode or elevation on this machine.
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        Assert.Throws<UpdateException>(() => UpdateArchive.VerifyTree(payload, release));
    }

    [Fact]
    public async Task PrepareRechecksSignedPlanAndDoesNotTouchInstallation()
    {
        var (archive, release) = _fixture.Build();
        var root = _fixture.InstallRoot();
        var downloader = new FileDownloader(archive, release.BaseUrl + release.Archive);
        var reports = new List<UpdateProgress>();
        var plan = await StagedUpdate.PrepareAsync(root, release, downloader, new InlineProgressFor(reports));
        Assert.Equal("Downloading version 0.5.1...", reports[0].Message);
        Assert.Contains(reports, report => report.Message == "Verifying and extracting the signed update...");

        var staged = StagedUpdate.LoadPlan(plan, _fixture.PublicKeyHex);
        Assert.True(SafePaths.Same(root, staged.Root));
        Assert.Equal("0.5.1", staged.Release.Version);
        // The production key does not trust the synthetic release.
        Assert.Throws<UpdateException>(() => StagedUpdate.LoadPlan(plan));
        File.WriteAllText(Path.Combine(staged.Payload, "VegasReborn.exe"), "tampered");
        Assert.Throws<UpdateException>(() => StagedUpdate.LoadPlan(plan, _fixture.PublicKeyHex));
        UpdateFixture.AssertSentinels(root);
        Assert.Equal("old companion", File.ReadAllText(Path.Combine(root, "VegasReborn.exe")));
    }

    [Fact]
    public async Task PlanFormatMatchesThePythonUpdater()
    {
        var (archive, release) = _fixture.Build();
        var root = _fixture.InstallRoot();
        var plan = await StagedUpdate.PrepareAsync(root, release, new FileDownloader(archive, release.BaseUrl + release.Archive));
        using var document = JsonDocument.Parse(File.ReadAllBytes(plan));
        var value = document.RootElement;
        Assert.Equal(["schema", "root", "payload", "manifest", "signature"], value.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, value.GetProperty("schema").GetInt32());
        Assert.Equal(Path.Combine(Path.GetDirectoryName(plan)!, "VegasReborn"), value.GetProperty("payload").GetString());
        Assert.Equal(release.Manifest, Convert.FromBase64String(value.GetProperty("manifest").GetString()!));
        Assert.Equal("plan.json", Path.GetFileName(plan));
        Assert.True(SafePaths.Same(Path.Combine(root, ".updates", "staging"), Path.GetDirectoryName(Path.GetDirectoryName(plan))!));
    }

    [Fact]
    public void PythonWrittenPlanLoadsAndAppliesEndToEnd()
    {
        // Exactly what 0.4.x prepare_update writes: json.dumps with ", "/": " separators and
        // backslash-escaped Windows paths, the payload extracted next to it.
        var (archive, release) = _fixture.Build();
        var root = _fixture.InstallRoot();
        var stage = Path.Combine(root, ".updates", "staging", Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(stage, "VegasReborn");
        Directory.CreateDirectory(payload);
        UpdateArchive.Extract(archive, payload, release);
        var plan = Path.Combine(stage, "plan.json");
        File.WriteAllText(plan,
            "{\"schema\": 1, \"root\": " + JsonSerializer.Serialize(root) + ", \"payload\": " + JsonSerializer.Serialize(payload)
            + ", \"manifest\": \"" + Convert.ToBase64String(release.Manifest) + "\", \"signature\": \""
            + Convert.ToBase64String(release.Signature) + "\"}");

        var reports = new List<UpdateProgress>();
        ProcessStartInfo? restarted = null;
        int waitedFor = 0;
        StagedUpdate.Apply(plan, 4242, new InlineProgressFor(reports),
            path => StagedUpdate.LoadPlan(path, _fixture.PublicKeyHex),
            (pid, _) => waitedFor = pid,
            (installRoot, installPayload, installRelease) => UpdateInstaller.Install(installRoot, installPayload, installRelease, () => false),
            info => { restarted = info; return 777; });

        Assert.Equal(4242, waitedFor);
        Assert.Equal("new companion", File.ReadAllText(Path.Combine(root, "VegasReborn.exe")));
        UpdateFixture.AssertSentinels(root);
        Assert.NotNull(restarted);
        Assert.True(SafePaths.Same(Path.Combine(root, "VegasReborn.exe"), restarted.FileName));
        Assert.True(SafePaths.Same(root, restarted.WorkingDirectory));
        Assert.Empty(restarted.ArgumentList);
        using var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".updates", "last-update.json")));
        Assert.Equal("0.5.1", record.RootElement.GetProperty("version").GetString());
        Assert.Equal("installed", record.RootElement.GetProperty("status").GetString());
        Assert.Equal(777, record.RootElement.GetProperty("restartedPid").GetInt32());
        Assert.Equal(["Verifying the staged update...", "Waiting for Vegas Reborn to close..."], reports.Take(2).Select(r => r.Message));
        Assert.Equal("Restarting Vegas Reborn...", reports[^1].Message);
    }

    [Fact]
    public async Task PlanOutsideStagingOrWithForeignPayloadIsInvalid()
    {
        var (archive, release) = _fixture.Build();
        var root = _fixture.InstallRoot();
        var plan = await StagedUpdate.PrepareAsync(root, release, new FileDownloader(archive, release.BaseUrl + release.Archive));
        var text = File.ReadAllText(plan);

        var moved = Path.Combine(root, ".updates", "plan.json");
        File.WriteAllText(moved, text);
        Assert.Equal("The staged update plan is invalid",
            Assert.Throws<UpdateException>(() => StagedUpdate.LoadPlan(moved, _fixture.PublicKeyHex)).Message);

        using var document = JsonDocument.Parse(text);
        var foreign = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schema"] = 1,
            ["root"] = root,
            ["payload"] = _fixture.Root,
            ["manifest"] = document.RootElement.GetProperty("manifest").GetString(),
            ["signature"] = document.RootElement.GetProperty("signature").GetString(),
        });
        File.WriteAllText(plan, foreign);
        Assert.Throws<UpdateException>(() => StagedUpdate.LoadPlan(plan, _fixture.PublicKeyHex));

        File.WriteAllText(plan, text.Replace("\"manifest\":\"", "\"manifest\":\" "));
        Assert.Throws<UpdateException>(() => StagedUpdate.LoadPlan(plan, _fixture.PublicKeyHex));
    }

    [Fact]
    public async Task InstallLockExcludesASecondHelper()
    {
        var (archive, release) = _fixture.Build();
        var root = _fixture.InstallRoot();
        var plan = await StagedUpdate.PrepareAsync(root, release, new FileDownloader(archive, release.BaseUrl + release.Archive));
        var lockPath = Path.Combine(root, ".updates", "install.lock");
        using var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        held.WriteByte((byte)'0');
        held.Flush();
        held.Lock(0, 1);
        var installed = false;
        var error = Assert.Throws<UpdateException>(() => StagedUpdate.Apply(plan, 1, null,
            path => StagedUpdate.LoadPlan(path, _fixture.PublicKeyHex), (_, _) => { },
            (_, _, _) => installed = true, _ => 0));
        Assert.Equal("Another update is already being installed", error.Message);
        Assert.False(installed);
    }

    [Fact]
    public void HandOffStartsStagedHelperWithThePythonCommandLine()
    {
        var plan = new StagedPlan(@"C:\Vegas\.updates\staging\a\plan.json", @"C:\Vegas", @"C:\Vegas\.updates\staging\a\VegasReborn", null!);
        ProcessStartInfo? started = null;
        StagedUpdate.HandOff(plan.PlanPath, 1234, _ => plan, () => false, info => { started = info; return 1; });
        Assert.NotNull(started);
        Assert.Equal(@"C:\Vegas\.updates\staging\a\VegasReborn\VegasReborn.exe", started.FileName);
        Assert.Equal(plan.Payload, started.WorkingDirectory);
        Assert.Equal(["--apply-update", plan.PlanPath, "--parent-pid", "1234"], started.ArgumentList);

        started = null;
        Assert.Throws<GamesBlockingUpdateException>(() =>
            StagedUpdate.HandOff(plan.PlanPath, 1234, _ => plan, () => true, info => { started = info; return 1; }));
        Assert.Null(started);
    }

    [Fact]
    public void RecordCheckWritesPublicStateOnly()
    {
        var root = _fixture.InstallRoot();
        ReleaseFeed.RecordCheck(root, null, "0.5.0");
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".updates", "last-check.json")));
        Assert.Equal(["checkedVersion", "availableVersion", "status", "checkedAt"], document.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("up-to-date", document.RootElement.GetProperty("status").GetString());
    }
}

internal sealed class InlineProgressFor(List<UpdateProgress> reports, Action<UpdateProgress>? also = null) : IProgress<UpdateProgress>
{
    public void Report(UpdateProgress value)
    {
        reports.Add(value);
        also?.Invoke(value);
    }
}

/// <summary>Serves one local archive for one expected URL.</summary>
internal sealed class FileDownloader(string archive, string expectedUrl) : IReleaseDownloader
{
    public Task<byte[]> DownloadBytesAsync(Uri url, long limit, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Unexpected metadata download");

    public Task<long> DownloadFileAsync(Uri url, long limit, string destination, IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Assert.Equal(expectedUrl, url.AbsoluteUri);
        File.Copy(archive, destination);
        progress?.Report(new FileInfo(destination).Length);
        return Task.FromResult(new FileInfo(destination).Length);
    }
}
