using System.IO.Compression;
using VegasReborn.Core.Update;

namespace VegasReborn.Core.Tests.Update;

public sealed class ManifestTests : IDisposable
{
    private readonly UpdateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void SignatureTamperingAndWrongKeyRejected()
    {
        var (_, release) = _fixture.Build();
        Assert.Throws<UpdateException>(() => ReleaseManifest.Verify([.. release.Manifest, (byte)' '], release.Signature, _fixture.PublicKeyHex));
        Assert.Throws<UpdateException>(() => ReleaseManifest.Verify(release.Manifest, new byte[64], _fixture.PublicKeyHex));
        Assert.Throws<UpdateException>(() => ReleaseManifest.Verify(release.Manifest, release.Signature, new string('0', 64)));
        // The pinned production key never accepts a synthetic signature.
        Assert.Throws<UpdateException>(() => ReleaseManifest.Verify(release.Manifest, release.Signature));
        Assert.Throws<UpdateException>(() => ReleaseManifest.Verify(release.Manifest, release.Signature[..63], _fixture.PublicKeyHex));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("runtime-0.5.1/../outside")]
    [InlineData("runtime-0.5.1/a:b")]
    [InlineData("runtime-0.5.1/CON.txt")]
    [InlineData("runtime-0.5.1/lpt1")]
    [InlineData("runtime-0.5.1/trailing.")]
    [InlineData("runtime-0.5.1/trailing ")]
    [InlineData("runtime-0.5.1\\escape")]
    [InlineData("runtime-0.5.1//empty")]
    [InlineData("runtime-0.5.0/old-runtime.dll")]
    [InlineData("local/companion/account-session.json")]
    [InlineData("local/companion/preferences.json")]
    [InlineData("local/companion/accounts/test/profile/R6_PECPEC")]
    [InlineData("local/companion/game/asset")]
    [InlineData("local/vegas2/game/asset")]
    [InlineData("local/companion/bin/VegasCompanionVegas2.dll")]
    [InlineData("LICENSE-DotNet.txt")]
    [InlineData("VegasReborn.dll")]
    [InlineData("server.py")]
    public void LocalDataAndWindowsPathAttacksRejectedEvenWhenSigned(string path)
    {
        var contents = new Dictionary<string, byte[]>(_fixture.Contents) { [path] = "bad"u8.ToArray() };
        Assert.Throws<UpdateException>(() => _fixture.Build(contents));
    }

    [Fact]
    public void DuplicateWindowsCasePathsRejected()
    {
        var contents = new Dictionary<string, byte[]>(_fixture.Contents) { ["runtime-0.5.1/VEGASREBORN.CORE.DLL"] = "shadow"u8.ToArray() };
        Assert.Throws<UpdateException>(() => _fixture.Build(contents));
    }

    [Fact]
    public void ExecutableAndVersionAreRequired()
    {
        foreach (var required in new[] { "VegasReborn.exe", "version.json" })
        {
            var contents = new Dictionary<string, byte[]>(_fixture.Contents);
            contents.Remove(required);
            Assert.Throws<UpdateException>(() => _fixture.Build(contents));
        }
    }

    [Theory]
    [InlineData("version", "0.5")]
    [InlineData("version", "01.5.1")]
    [InlineData("schema", 2)]
    [InlineData("repository", "someone/else")]
    [InlineData("platform", "win32")]
    [InlineData("archive", "other.zip")]
    [InlineData("bytes", 0)]
    [InlineData("bytes", 209715201L)]
    [InlineData("bytes", "12")]
    [InlineData("sha256", "ABC")]
    public void UnsafeSignedManifestFieldsRejected(string field, object value)
    {
        var (archive, _) = _fixture.Build();
        var manifest = UpdateFixture.ManifestFor(archive, _fixture.Contents);
        manifest[field] = value;
        var data = UpdateFixture.ManifestBytes(manifest);
        Assert.Throws<UpdateException>(() => ReleaseManifest.Verify(data, _fixture.Sign(data), _fixture.PublicKeyHex));
    }

    [Fact]
    public void SeedModeMustMatchThePath()
    {
        var (archive, _) = _fixture.Build();
        var manifest = UpdateFixture.ManifestFor(archive, _fixture.Contents);
        var files = (List<Dictionary<string, object>>)manifest["files"];
        files.Single(file => (string)file["path"] == "local/companion/service.json")["mode"] = "replace";
        var data = UpdateFixture.ManifestBytes(manifest);
        Assert.Throws<UpdateException>(() => ReleaseManifest.Verify(data, _fixture.Sign(data), _fixture.PublicKeyHex));
    }

    [Fact]
    public void UnpackedSizeLimitApplies()
    {
        var (archive, _) = _fixture.Build();
        var manifest = UpdateFixture.ManifestFor(archive, _fixture.Contents);
        ((List<Dictionary<string, object>>)manifest["files"])[1]["bytes"] = ReleaseManifest.MaxUnpacked;
        var data = UpdateFixture.ManifestBytes(manifest);
        Assert.Throws<UpdateException>(() => ReleaseManifest.Verify(data, _fixture.Sign(data), _fixture.PublicKeyHex));
    }

    [Fact]
    public void ArchiveHashAndExtraEntriesRejected()
    {
        var (archive, release) = _fixture.Build();
        File.AppendAllText(archive, "tampered");
        Assert.Throws<UpdateException>(() => UpdateArchive.Extract(archive, Path.Combine(_fixture.Root, "unpack"), release));

        (archive, release) = _fixture.Build(zipHook: bundle =>
        {
            using var stream = bundle.CreateEntry("VegasReborn/../outside").Open();
            stream.Write("bad"u8);
        });
        Assert.Throws<UpdateException>(() => UpdateArchive.Extract(archive, Path.Combine(_fixture.Root, "unpack2"), release));
        Assert.False(File.Exists(Path.Combine(_fixture.Root, "outside")));
    }

    [Fact]
    public void EntriesOutsideTheAppFolderRejected()
    {
        var (archive, release) = _fixture.Build(zipHook: bundle => bundle.CreateEntry("Other/README.txt"));
        Assert.Throws<UpdateException>(() => UpdateArchive.Extract(archive, Path.Combine(_fixture.Root, "unpack"), release));
    }

    [Fact]
    public void SymlinkArchiveEntryRejected()
    {
        // Replace a declared entry with a symlink while keeping the manifest's signed size and
        // hash. Extraction must reject its type before writing.
        var contents = new Dictionary<string, byte[]>(_fixture.Contents);
        contents.Remove("README.txt");
        var (archive, release) = _fixture.Build(contents, bundle =>
        {
            var link = bundle.CreateEntry("VegasReborn/README.txt");
            link.ExternalAttributes = (0xA000 | 0x1FF) << 16;
            using var stream = link.Open();
            stream.Write("target"u8);
        });
        Assert.Throws<UpdateException>(() => UpdateArchive.Extract(archive, Path.Combine(_fixture.Root, "symlinks"), release));
    }

    [Fact]
    public void ExtractedTreeMatchesTheSignedFiles()
    {
        var (payload, release) = _fixture.Payload();
        Assert.Equal("new companion", File.ReadAllText(Path.Combine(payload, "VegasReborn.exe")));
        UpdateArchive.VerifyTree(payload, release);
    }
}

public sealed class ManagedPathTests
{
    [Theory]
    // 0.4.x allowlist.
    [InlineData("VegasReborn.exe")]
    [InlineData("README.txt")]
    [InlineData("LICENSE-Python.txt")]
    [InlineData("LICENSE-Minhook.txt")]
    [InlineData("THIRD-PARTY-NOTICES.txt")]
    [InlineData("version.json")]
    [InlineData("local/companion/service.json")]
    [InlineData("local/companion/root-public.der")]
    [InlineData("local/companion/bin/VegasCompanion-13.dll")]
    [InlineData("local/companion/bin/VegasDedicated-4.dll")]
    [InlineData("local/companion/bin/VegasSteamGate.dll")]
    [InlineData("local/companion/bin/build.json")]
    // .NET release layout (docs/DOTNET_UPDATER.md).
    [InlineData("runtime-0.5.0/dotnet/host/fxr/10.0.0/hostfxr.dll")]
    [InlineData("runtime-0.5.0/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.0/PresentationFramework.dll")]
    [InlineData("runtime-0.5.0/native/VegasCompanionVegas2.dll")]
    [InlineData("runtime-0.5.0/native/VegasDedicated-4.dll")]
    [InlineData("runtime-0.5.0/updater-only")]
    public void ReleaseLayoutIsManaged(string path) => Assert.True(ManagedPaths.IsManaged(path, "0.5.0"));

    [Theory]
    [InlineData("")]
    [InlineData("VegasReborn.dll")]
    [InlineData("vegasreborn.exe")]
    [InlineData("LICENSE-DotNet.txt")]
    [InlineData("runtime-0.4.14/python310.dll")]
    [InlineData("runtime-0.5.0")]
    [InlineData("runtime-0.5.0/")]
    [InlineData("runtime-0.5.0/CONOUT$")]
    [InlineData("runtime-0.5.0/COM¹.dll")]
    [InlineData("runtime-0.5.0/a\u0001b")]
    [InlineData("runtime-0.5.0/a*b")]
    [InlineData("local/companion/bin/VegasCompanion-0.dll")]
    [InlineData("local/companion/bin/sub/VegasCompanion.dll")]
    [InlineData("local/companion/bin/VegasCompanionVegas2.dll")]
    [InlineData("local/vegas2/game/R6Vegas2_Game.exe")]
    [InlineData("local/retail-accounts/a.json")]
    public void OtherPathsAreNotManaged(string path) => Assert.False(ManagedPaths.IsManaged(path, "0.5.0"));

    [Theory]
    [InlineData("0.4.14", "0.5.0", true)]
    [InlineData("0.5.0", "0.5.0", false)]
    [InlineData("0.10.0", "0.9.9", false)]
    [InlineData("1.0.0", "0.99.99", false)]
    public void VersionsCompareNumerically(string older, string newer, bool isOlder) =>
        Assert.Equal(isOlder, ReleaseVersion.Parse(older) < ReleaseVersion.Parse(newer));

    [Theory]
    [InlineData("v0.5.0")]
    [InlineData("0.5")]
    [InlineData("0.05.0")]
    [InlineData("0.5.0-preview")]
    [InlineData("0.5.0\n")]
    [InlineData("١.0.0")]
    [InlineData("99999999999.0.0")]
    public void InvalidVersionsRejected(string value) =>
        Assert.Equal("Invalid release version", Assert.Throws<UpdateException>(() => ReleaseVersion.Parse(value)).Message);
}
