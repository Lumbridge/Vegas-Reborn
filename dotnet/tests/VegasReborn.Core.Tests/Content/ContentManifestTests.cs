using System.Text;
using System.Text.Json;
using VegasReborn.Core.Content;
using VegasReborn.Core.Tests.Update;

namespace VegasReborn.Core.Tests.Content;

public sealed class ContentManifestTests : IDisposable
{
    private const string Unsafe = "The Extra Maps download list is unsupported or unsafe";
    private const string Unverified = "The Extra Maps download list could not be verified";
    private const string GoodPath = "KellerGame/Content/CookedPc/MapsPack/PAK_01_Kitchen/PAK_01_Kitchen.usdx";

    private readonly UpdateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static string Sha(char c) => new(c, 64);

    private static string Manifest(string path = GoodPath, string kind = "content", string title = "vegas1",
        string id = "vegas1-maps-pack", string fileArchive = "PAK_01_Kitchen.zip", long archiveBytes = 123,
        long fileBytes = 456, string? extraFile = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["schema"] = 1,
            ["kind"] = kind,
            ["id"] = id,
            ["version"] = 1,
            ["title"] = title,
            ["archives"] = new[] { new Dictionary<string, object> { ["name"] = "PAK_01_Kitchen.zip", ["bytes"] = archiveBytes, ["sha256"] = Sha('a') } },
            ["files"] = extraFile is null
                ? new[] { File(path, fileArchive, fileBytes) }
                : [File(path, fileArchive, fileBytes), File(extraFile, fileArchive, 1)],
        });

    private static Dictionary<string, object> File(string path, string archive, long bytes) =>
        new() { ["path"] = path, ["bytes"] = bytes, ["sha256"] = Sha('b'), ["archive"] = archive };

    private ContentRelease Verify(string json) => ContentManifest.Verify(Encoding.UTF8.GetBytes(json),
        _fixture.Sign(Encoding.UTF8.GetBytes(json)), _fixture.PublicKeyHex);

    private void AssertUnsafe(string json) =>
        Assert.Equal(Unsafe, Assert.Throws<ContentException>(() => Verify(json)).Message);

    [Fact]
    public void VerifiesAWellFormedManifest()
    {
        var release = Verify(Manifest());
        Assert.Equal(1, release.Version);
        Assert.Equal("vegas1-maps-pack", release.Id);
        Assert.Equal("vegas1", release.Title);
        var archive = Assert.Single(release.Archives);
        Assert.Equal(new ContentArchive("PAK_01_Kitchen.zip", 123, Sha('a')), archive);
        Assert.Equal(new ContentFile(GoodPath, 456, Sha('b'), "PAK_01_Kitchen.zip"), Assert.Single(release.Files));
    }

    [Theory]
    [InlineData("KellerGame/Localization/JPN/PAK_01_Kitchen.jpn")]
    [InlineData("KellerGame/Localization/INT/PAK_01_Kitchen.int")]
    [InlineData("KellerGame/Content/CookedPc/MapsPack/PAK_09_KillHouseDouble_DLC/Medias/PAK_09_KHD_SM.uppc")]
    [InlineData("KellerGame/Imagemap/PAK_Kitchen.dds")]
    [InlineData("KellerGame/ImageMap/PAK_Kitchen.dds")]
    public void AcceptsRealPackPaths(string path) => Assert.Single(Verify(Manifest(path)).Files);

    [Fact]
    public void RejectsBadSignature()
    {
        var data = Encoding.UTF8.GetBytes(Manifest());
        Assert.Equal(Unverified, Assert.Throws<ContentException>(() => ContentManifest.Verify(data, new byte[64], _fixture.PublicKeyHex)).Message);
        Assert.Equal(Unverified, Assert.Throws<ContentException>(() => ContentManifest.Verify(data, _fixture.Sign(data))).Message);
    }

    [Fact]
    public void RejectsUpdateKind() => AssertUnsafe(Manifest(kind: "update"));

    [Fact]
    public void RejectsOtherTitle() => AssertUnsafe(Manifest(title: "vegas2"));

    [Fact]
    public void RejectsOtherId() => AssertUnsafe(Manifest(id: "vegas2-maps-pack"));

    [Theory]
    [InlineData("../x")]
    [InlineData("C:/x")]
    [InlineData("/x")]
    [InlineData("KellerGame/Binaries/x.dll")]
    [InlineData("KellerGame/Config/DefaultEngine.ini")]
    [InlineData("KellerGame\\Content\\x")]
    [InlineData("KellerGame/Content/CookedPc/MapsPack/../../Binaries/x.usdx")]
    [InlineData("KellerGame/Content/CookedPc/MapsPack/a:b.usdx")]
    public void RejectsUnsafePaths(string path) => AssertUnsafe(Manifest(path));

    [Theory]
    [InlineData("KellerGame/Content/CookedPc/Other/x.usdx")]
    [InlineData("KellerGame/Content/CookedPc/MapsPack/x.exe")]
    [InlineData("KellerGame/Content/CookedPc/MapsPack/x.dll")]
    [InlineData("KellerGame/Localization/XXX/PAK_x.int")]
    [InlineData("KellerGame/Localization/INT/x.int")]
    [InlineData("KellerGame/ImageMap/x.dds")]
    [InlineData("KellerGame/Imagemap/PAK_x.exe")]
    [InlineData("KellerGame/Textures/PAK_x.dds")]
    public void RejectsFilesOutsideTheAllowedFolders(string path) => AssertUnsafe(Manifest(path));

    [Fact]
    public void RejectsUnknownArchive() => AssertUnsafe(Manifest(fileArchive: "Other.zip"));

    [Fact]
    public void RejectsDuplicatePathsIgnoringCase() =>
        AssertUnsafe(Manifest(extraFile: GoodPath.Replace("PAK_01_Kitchen.usdx", "pak_01_kitchen.USDX")));

    [Fact]
    public void RejectsOversize()
    {
        AssertUnsafe(Manifest(fileBytes: ContentManifest.MaxUnpacked + 1));
        AssertUnsafe(Manifest(archiveBytes: ContentManifest.MaxArchiveBytes + 1));
        AssertUnsafe(Manifest(archiveBytes: 0));
        AssertUnsafe(Manifest(fileBytes: -1));
    }

    [Fact]
    public void RejectsTotalOverMaxUnpacked() =>
        AssertUnsafe(Manifest(fileBytes: ContentManifest.MaxUnpacked, extraFile: "KellerGame/Content/CookedPc/MapsPack/b.usdx"));

    [Fact]
    public void RejectsMalformedJson() => AssertUnsafe("{\"schema\":");
}
