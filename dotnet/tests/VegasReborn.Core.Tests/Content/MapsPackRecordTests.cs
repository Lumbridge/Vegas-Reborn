using System.Text;
using VegasReborn.Core.Content;
using VegasReborn.Core.Launch;
using VegasReborn.Core.Tests.Launch;
using Xunit;

namespace VegasReborn.Core.Tests.Content;

public sealed class MapsPackRecordTests : IDisposable
{
    private const string GamePath = "KellerGame/Content/CookedPc/MapsPack/PAK_01_Kitchen/PAK_01_Kitchen.usdx";

    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private AppPaths Paths => new(_dir.Path);

    private (MapsPackRecord Record, string File) Install(byte[]? content = null)
    {
        content ??= Encoding.UTF8.GetBytes("map data");
        var file = Path.Combine(Paths.MapsPack, "files", GamePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, content);
        var record = new MapsPackRecord(1, [new ContentFile(GamePath, content.Length, PrivateFile.Sha256(content), "maps.zip")]);
        record.Write(Path.Combine(Paths.MapsPack, "record.json"));
        return (record, file);
    }

    [Fact]
    public void RoundTripsARecord()
    {
        var (record, _) = Install();
        var read = MapsPackRecord.Read(Path.Combine(Paths.MapsPack, "record.json"));
        Assert.NotNull(read);
        Assert.Equal(record.Version, read.Version);
        Assert.Equal(record.Files, read.Files);
        Assert.Equal(MapsPackState.Installed, MapsPackRecord.Check(Paths, out var checkedRecord));
        Assert.NotNull(checkedRecord);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"Version\":1}")]
    [InlineData("{\"Version\":1,\"Files\":[{\"Path\":\"/abs/file.usdx\",\"Bytes\":1,\"Sha256\":\"a\",\"Archive\":\"x\"}]}")]
    [InlineData("{\"Version\":1,\"Files\":[{\"Path\":\"C:/abs/file.usdx\",\"Bytes\":1,\"Sha256\":\"a\",\"Archive\":\"x\"}]}")]
    [InlineData("{\"Version\":1,\"Files\":[{\"Path\":\"a/../../b.usdx\",\"Bytes\":1,\"Sha256\":\"a\",\"Archive\":\"x\"}]}")]
    [InlineData("{\"Version\":1,\"Files\":[]}")]
    [InlineData("{\"Version\":1,\"Files\":[{\"Path\":\"a/b.usdx:stream\",\"Bytes\":1,\"Sha256\":\"a\",\"Archive\":\"x\"}]}")]
    [InlineData("{\"Version\":1,\"Files\":[{\"Path\":\"a/b./c.usdx\",\"Bytes\":1,\"Sha256\":\"a\",\"Archive\":\"x\"}]}")]
    [InlineData("{\"Version\":1,\"Files\":[{\"Path\":\"a/b /c.usdx\",\"Bytes\":1,\"Sha256\":\"a\",\"Archive\":\"x\"}]}")]
    [InlineData("{\"Version\":1,\"Files\":[{\"Path\":\"a\\\\b.usdx\",\"Bytes\":1,\"Sha256\":\"a\",\"Archive\":\"x\"}]}")]
    public void MalformedRecordReadsAsNull(string json)
    {
        var path = _dir.File("record.json", json);
        Assert.Null(MapsPackRecord.Read(path));
    }

    [Fact]
    public void MissingRecordFileReadsAsNull() =>
        Assert.Null(MapsPackRecord.Read(Path.Combine(_dir.Path, "nope.json")));

    [Fact]
    public void MissingFileIsDamaged()
    {
        var (_, file) = Install();
        File.Delete(file);
        Assert.Equal(MapsPackState.Damaged, MapsPackRecord.Check(Paths, out var record));
        Assert.NotNull(record);
    }

    [Fact]
    public void ChangedHashIsDamaged()
    {
        var (_, file) = Install();
        File.WriteAllBytes(file, Encoding.UTF8.GetBytes("map date"));
        Assert.Equal(MapsPackState.Damaged, MapsPackRecord.Check(Paths, out _));
    }

    [Fact]
    public void ChangedSizeIsDamaged()
    {
        var (_, file) = Install();
        File.WriteAllBytes(file, Encoding.UTF8.GetBytes("map data!"));
        Assert.Equal(MapsPackState.Damaged, MapsPackRecord.Check(Paths, out _));
    }

    [Fact]
    public void NoRecordIsNotInstalled()
    {
        var file = Path.Combine(Paths.MapsPack, "files", GamePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "orphan");
        Assert.Equal(MapsPackState.NotInstalled, MapsPackRecord.Check(Paths, out var record));
        Assert.Null(record);
    }
}
