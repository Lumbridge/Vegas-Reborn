namespace VegasReborn.Core.Tests.Launch;

public sealed class TextDefaultsTests
{
    [Fact]
    public void LaunchSourcesHaveNoEncodingCorruption()
    {
        var markers = new[] { "Ã¢", "â€", "â\u0080", "Â ", "�" };
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VegasReborn.slnx")))
        {
            directory = directory.Parent;
        }
        if (directory is null)
        {
            return; // Sources are not next to the build output.
        }
        var core = Path.Combine(directory.FullName, "src", "VegasReborn.Core");
        var checkedFiles = 0;
        foreach (var folder in new[] { "Launch", "Settings", "Display", "Presence" })
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(core, folder), "*.cs"))
            {
                var text = File.ReadAllText(path);
                Assert.False(markers.Any(text.Contains), Path.GetFileName(path));
                checkedFiles++;
            }
        }
        Assert.True(checkedFiles > 10);
    }
}
