using VegasReborn.Core.Launch;

namespace VegasReborn.Core.Tests.Launch;

public sealed class GameCopyProgressTests
{
    private sealed class Recorder : IProgress<CopyProgress>
    {
        public List<CopyProgress> Reports { get; } = [];

        public void Report(CopyProgress value) => Reports.Add(value);
    }

    [Fact]
    public void CopyReportsBytesUpToTheTotalAndSkipsLogs()
    {
        using var root = new TestDirectory();
        var large = new byte[(3 << 20) + 17];
        new Random(5).NextBytes(large);
        root.Bytes("install/KellerGame/CookedPC/Big.upk", large);
        root.File("install/Binaries/game.ini", "[A]\nB=1\n");
        root.File("install/Binaries/Launch.log", "ignored log");
        root.File("install/session/state", "ignored");
        var source = Path.Combine(root.Path, "install");
        var destination = Path.Combine(root.Path, "copy");
        var recorder = new Recorder();

        GamePreparer.CopyTree(source, destination, CancellationToken.None, recorder);

        long total = large.Length + "[A]\nB=1\n".Length;
        Assert.Equal(new CopyProgress(0, total), recorder.Reports[0]);
        Assert.Equal(new CopyProgress(total, total), recorder.Reports[^1]);
        Assert.True(recorder.Reports.Count > 2);
        Assert.Equal(recorder.Reports.Select(r => r.Completed).Order(), recorder.Reports.Select(r => r.Completed));
        Assert.Equal(large, File.ReadAllBytes(Path.Combine(destination, "KellerGame/CookedPC/Big.upk")));
        Assert.Equal("[A]\nB=1\n", File.ReadAllText(Path.Combine(destination, "Binaries/game.ini")));
        Assert.False(File.Exists(Path.Combine(destination, "Binaries/Launch.log")));
        Assert.False(Directory.Exists(Path.Combine(destination, "session")));
        Assert.Equal(1.0, recorder.Reports[^1].Fraction);
    }

    [Fact]
    public void CopyRefusesToOverwriteAnExistingFile()
    {
        using var root = new TestDirectory();
        root.File("install/a.txt", "new");
        root.File("copy/a.txt", "kept");

        Assert.Throws<IOException>(() => GamePreparer.CopyTree(Path.Combine(root.Path, "install"), Path.Combine(root.Path, "copy"), CancellationToken.None));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(root.Path, "copy/a.txt")));
    }
}
