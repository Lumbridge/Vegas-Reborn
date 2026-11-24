using System.IO.Compression;
using System.Text;
using System.Text.Json;
using VegasReborn.Core.Content;
using VegasReborn.Core.Launch;
using VegasReborn.Core.Tests.Launch;
using VegasReborn.Core.Tests.Update;
using VegasReborn.Core.Update;

namespace VegasReborn.Core.Tests.Content;

public sealed class MapsPackInstallerTests : IDisposable
{
    private const string Base = "https://example.test/pack/";
    private const string KitchenPath = "KellerGame/Content/CookedPc/MapsPack/PAK_01_Kitchen/PAK_01_Kitchen.usdx";
    private const string ImagePath = "KellerGame/ImageMap/PAK_Kitchen.dds";
    private const string FailedText = "The Maps Pack download failed. Check your connection and try again.";

    private readonly TestDirectory _dir = new();
    private readonly UpdateFixture _fixture = new();
    private readonly FakeDownloader _downloader = new();

    public void Dispose()
    {
        _dir.Dispose();
        _fixture.Dispose();
    }

    private AppPaths Paths => new(_dir.Path);

    private MapsPackInstaller Installer => new(Paths, _downloader, _fixture.PublicKeyHex, Base);

    private static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(data);
            }
        }
        return memory.ToArray();
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private sealed record Spec(string Name, byte[] Zip, (string Path, byte[] Data)[] Files);

    private static Spec Archive(string name, string path, string content, string? entryName = null) =>
        new(name, Zip((entryName ?? path, Bytes(content))), [(path, Bytes(content))]);

    /// <summary>Publishes a signed manifest and the archives for <paramref name="specs"/>.</summary>
    private void Publish(int version, params Spec[] specs)
    {
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["schema"] = 1,
            ["kind"] = "content",
            ["id"] = "vegas1-maps-pack",
            ["version"] = version,
            ["title"] = "vegas1",
            ["archives"] = specs.Select(s => new Dictionary<string, object>
            {
                ["name"] = s.Name, ["bytes"] = s.Zip.Length, ["sha256"] = PrivateFile.Sha256(s.Zip),
            }).ToList(),
            ["files"] = specs.SelectMany(s => s.Files.Select(f => new Dictionary<string, object>
            {
                ["path"] = f.Path, ["bytes"] = f.Data.Length, ["sha256"] = PrivateFile.Sha256(f.Data), ["archive"] = s.Name,
            })).ToList(),
        });
        _downloader.Items[Base + "content.json"] = manifest;
        _downloader.Items[Base + "content.sig"] = _fixture.Sign(manifest);
        foreach (var spec in specs)
        {
            _downloader.Items[Base + spec.Name] = spec.Zip;
        }
    }

    private void AssertNothingInstalled()
    {
        Assert.Equal(MapsPackState.NotInstalled, MapsPackRecord.Check(Paths, out _));
        Assert.False(File.Exists(Path.Combine(Paths.MapsPack, "record.json")));
        Assert.False(Directory.Exists(Path.Combine(Paths.MapsPack, "files")));
    }

    private static string Read(string root, string path) =>
        File.ReadAllText(Path.Combine(root, "files", path.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public async Task InstallsVerifiedFilesAndWritesRecordLast()
    {
        Publish(3, Archive("a.zip", KitchenPath, "kitchen"), Archive("b.zip", ImagePath, "image"));
        var reports = new List<MapsPackProgress>();
        await Installer.InstallAsync(new SyncProgress<MapsPackProgress>(reports.Add), CancellationToken.None);

        Assert.Equal(MapsPackState.Installed, MapsPackRecord.Check(Paths, out var record));
        Assert.Equal(3, record!.Version);
        Assert.Equal(2, record.Files.Count);
        Assert.Equal("kitchen", Read(Paths.MapsPack, KitchenPath));
        Assert.Equal("image", Read(Paths.MapsPack, ImagePath));
        Assert.False(Directory.Exists(Path.Combine(Paths.MapsPack, "staging")));
        var total = _downloader.Items[Base + "a.zip"].Length + _downloader.Items[Base + "b.zip"].Length;
        Assert.NotEmpty(reports);
        Assert.All(reports, r => Assert.Equal(total, r.TotalBytes));
        Assert.Equal(total, reports[^1].Bytes);
        Assert.Equal(reports.Select(r => r.Bytes).Order(), reports.Select(r => r.Bytes));
        Assert.True(reports.Count >= 8);
    }

    [Fact]
    public async Task ArchiveHashMismatchInstallsNothing()
    {
        Publish(1, Archive("a.zip", KitchenPath, "kitchen"));
        var served = (byte[])_downloader.Items[Base + "a.zip"].Clone();
        served[^1] ^= 0xFF;
        _downloader.Items[Base + "a.zip"] = served;
        var error = await Assert.ThrowsAsync<ContentException>(() => Installer.InstallAsync(null, CancellationToken.None));
        Assert.StartsWith("The Maps Pack could not be installed:", error.Message);
        AssertNothingInstalled();
    }

    [Fact]
    public async Task ArchiveSizeMismatchInstallsNothing()
    {
        Publish(1, Archive("a.zip", KitchenPath, "kitchen"));
        _downloader.Items[Base + "a.zip"] = [.. _downloader.Items[Base + "a.zip"], 0];
        await Assert.ThrowsAsync<ContentException>(() => Installer.InstallAsync(null, CancellationToken.None));
        AssertNothingInstalled();
    }

    [Fact]
    public async Task ZipEntryNotInManifestInstallsNothing()
    {
        var good = Archive("a.zip", KitchenPath, "kitchen");
        var spec = good with { Zip = Zip((KitchenPath, Bytes("kitchen")), ("KellerGame/ImageMap/PAK_Extra.dds", Bytes("x"))) };
        Publish(1, spec);
        var error = await Assert.ThrowsAsync<ContentException>(() => Installer.InstallAsync(null, CancellationToken.None));
        Assert.StartsWith("The Maps Pack could not be installed:", error.Message);
        AssertNothingInstalled();
    }

    [Fact]
    public async Task EntryListedForAnotherArchiveInstallsNothing()
    {
        var a = Archive("a.zip", KitchenPath, "kitchen");
        var b = Archive("b.zip", ImagePath, "image");
        Publish(1, a with { Zip = Zip((KitchenPath, Bytes("kitchen")), (ImagePath, Bytes("image"))) }, b);
        await Assert.ThrowsAsync<ContentException>(() => Installer.InstallAsync(null, CancellationToken.None));
        AssertNothingInstalled();
    }

    [Fact]
    public async Task DuplicateEntryInstallsNothing()
    {
        var spec = Archive("a.zip", KitchenPath, "kitchen");
        Publish(1, spec with { Zip = Zip((KitchenPath, Bytes("kitchen")), (KitchenPath, Bytes("kitchen"))) });
        await Assert.ThrowsAsync<ContentException>(() => Installer.InstallAsync(null, CancellationToken.None));
        AssertNothingInstalled();
    }

    [Fact]
    public async Task TraversalEntryInstallsNothing()
    {
        var spec = Archive("a.zip", KitchenPath, "kitchen");
        Publish(1, spec with { Zip = Zip((KitchenPath, Bytes("kitchen")), ("../../escape.dds", Bytes("x"))) });
        await Assert.ThrowsAsync<ContentException>(() => Installer.InstallAsync(null, CancellationToken.None));
        AssertNothingInstalled();
        Assert.False(File.Exists(Path.Combine(Paths.MapsPack, "escape.dds")));
    }

    [Fact]
    public async Task MissingListedFileInstallsNothing()
    {
        var a = Archive("a.zip", KitchenPath, "kitchen");
        var spec = a with { Files = [(KitchenPath, Bytes("kitchen")), (ImagePath, Bytes("image"))] };
        Publish(1, spec);
        var error = await Assert.ThrowsAsync<ContentException>(() => Installer.InstallAsync(null, CancellationToken.None));
        Assert.StartsWith("The Maps Pack could not be installed:", error.Message);
        AssertNothingInstalled();
    }

    [Fact]
    public async Task ExtractedFileHashMismatchInstallsNothing()
    {
        var a = Archive("a.zip", KitchenPath, "kitchen");
        Publish(1, a with { Zip = Zip((KitchenPath, Bytes("kitchen!"))) });
        await Assert.ThrowsAsync<ContentException>(() => Installer.InstallAsync(null, CancellationToken.None));
        AssertNothingInstalled();
    }

    [Fact]
    public async Task EntriesMatchCaseInsensitivelyAndWriteManifestSpelling()
    {
        Publish(1, Archive("a.zip", KitchenPath, "kitchen", entryName: KitchenPath.ToUpperInvariant()));
        await Installer.InstallAsync(null, CancellationToken.None);
        Assert.Equal(MapsPackState.Installed, MapsPackRecord.Check(Paths, out _));
        var directory = Path.Combine(Paths.MapsPack, "files", "KellerGame", "Content", "CookedPc", "MapsPack", "PAK_01_Kitchen");
        Assert.Equal("PAK_01_Kitchen.usdx", Path.GetFileName(Directory.GetFiles(directory).Single()));
    }

    [Fact]
    public async Task CancelledInstallLeavesNoRecord()
    {
        Publish(1, Archive("a.zip", KitchenPath, "kitchen"), Archive("b.zip", ImagePath, "image"));
        using var cancel = new CancellationTokenSource();
        _downloader.OnDownload = url =>
        {
            if (url.EndsWith("b.zip", StringComparison.Ordinal))
            {
                cancel.Cancel();
            }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Installer.InstallAsync(null, cancel.Token));
        AssertNothingInstalled();

        _downloader.OnDownload = null;
        await Installer.InstallAsync(null, CancellationToken.None);
        Assert.Equal(MapsPackState.Installed, MapsPackRecord.Check(Paths, out _));
    }

    [Fact]
    public async Task DownloadFailureIsMappedToPlainText()
    {
        Publish(1, Archive("a.zip", KitchenPath, "kitchen"));
        _downloader.Items.Remove(Base + "a.zip");
        var error = await Assert.ThrowsAsync<ContentException>(() => Installer.InstallAsync(null, CancellationToken.None));
        Assert.Equal(FailedText, error.Message);
        AssertNothingInstalled();
    }

    [Fact]
    public async Task ManifestDownloadFailureIsMappedToPlainText()
    {
        var error = await Assert.ThrowsAsync<ContentException>(() => Installer.FetchAsync(CancellationToken.None));
        Assert.Equal(FailedText, error.Message);
    }

    [Fact]
    public async Task FetchRejectsABadSignature()
    {
        Publish(1, Archive("a.zip", KitchenPath, "kitchen"));
        var signature = _downloader.Items[Base + "content.sig"];
        signature[0] ^= 0xFF;
        await Assert.ThrowsAsync<ContentException>(() => Installer.FetchAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReinstallReplacesOldFiles()
    {
        Publish(1, Archive("a.zip", KitchenPath, "old"));
        await Installer.InstallAsync(null, CancellationToken.None);
        Publish(2, Archive("a.zip", ImagePath, "new"));
        await Installer.InstallAsync(null, CancellationToken.None);

        Assert.Equal(MapsPackState.Installed, MapsPackRecord.Check(Paths, out var record));
        Assert.Equal(2, record!.Version);
        Assert.Equal("new", Read(Paths.MapsPack, ImagePath));
        Assert.False(File.Exists(Path.Combine(Paths.MapsPack, "files", KitchenPath.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task FailedReinstallLeavesTheOldPackIntact()
    {
        Publish(1, Archive("a.zip", KitchenPath, "old"));
        await Installer.InstallAsync(null, CancellationToken.None);
        Publish(2, Archive("a.zip", ImagePath, "new"));
        _downloader.Items[Base + "a.zip"] = Bytes("garbage");
        await Assert.ThrowsAsync<ContentException>(() => Installer.InstallAsync(null, CancellationToken.None));
        Assert.Equal(MapsPackState.Installed, MapsPackRecord.Check(Paths, out var record));
        Assert.Equal(1, record!.Version);
    }

    [Fact]
    public async Task OrphanedFilesWithoutRecordAreReplaced()
    {
        Publish(1, Archive("a.zip", KitchenPath, "kitchen"));
        _dir.File("local/content/vegas1-maps-pack/files/orphan.dds", "orphan");
        await Installer.InstallAsync(null, CancellationToken.None);
        Assert.Equal(MapsPackState.Installed, MapsPackRecord.Check(Paths, out _));
        Assert.False(File.Exists(Path.Combine(Paths.MapsPack, "files", "orphan.dds")));
    }

    [Fact]
    public async Task StaleStagingIsDeletedBeforeInstall()
    {
        Publish(1, Archive("a.zip", KitchenPath, "kitchen"));
        _dir.File("local/content/vegas1-maps-pack/staging/a.zip", "stale");
        await Installer.InstallAsync(null, CancellationToken.None);
        Assert.Equal(MapsPackState.Installed, MapsPackRecord.Check(Paths, out _));
    }

    [Fact]
    public async Task RemoveDeletesRecordThenFiles()
    {
        Publish(1, Archive("a.zip", KitchenPath, "kitchen"));
        await Installer.InstallAsync(null, CancellationToken.None);
        _dir.File("local/content/vegas1-maps-pack/staging/leftover", "x");
        Installer.Remove();
        AssertNothingInstalled();
        Assert.False(Directory.Exists(Path.Combine(Paths.MapsPack, "staging")));
    }

    [Fact]
    public void RemoveWithNothingInstalledIsHarmless()
    {
        Installer.Remove();
        AssertNothingInstalled();
    }

    [Fact]
    public async Task UpdateAvailableWhenPublishedVersionIsNewer()
    {
        Publish(1, Archive("a.zip", KitchenPath, "kitchen"));
        Assert.False(await Installer.UpdateAvailableAsync(CancellationToken.None));
        await Installer.InstallAsync(null, CancellationToken.None);
        Assert.False(await Installer.UpdateAvailableAsync(CancellationToken.None));
        Publish(2, Archive("a.zip", KitchenPath, "kitchen2"));
        Assert.True(await Installer.UpdateAvailableAsync(CancellationToken.None));
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private sealed class FakeDownloader : IReleaseDownloader
    {
        public Dictionary<string, byte[]> Items { get; } = new();
        public Action<string>? OnDownload { get; set; }

        public Task<byte[]> DownloadBytesAsync(Uri url, long limit, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Items.TryGetValue(url.AbsoluteUri, out var data))
            {
                throw new UpdateException("Could not download the update. Check your connection and retry.");
            }
            return Task.FromResult(data);
        }

        public Task<long> DownloadFileAsync(Uri url, long limit, string destination, IProgress<long>? progress = null,
            CancellationToken cancellationToken = default)
        {
            OnDownload?.Invoke(url.AbsoluteUri);
            cancellationToken.ThrowIfCancellationRequested();
            if (!Items.TryGetValue(url.AbsoluteUri, out var data))
            {
                throw new UpdateException("Could not download the update. Check your connection and retry.");
            }
            using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write))
            {
                stream.Write(data);
            }
            for (var step = 1; step <= 4; step++)
            {
                progress?.Report(data.Length * step / 4);
            }
            return Task.FromResult((long)data.Length);
        }
    }
}
