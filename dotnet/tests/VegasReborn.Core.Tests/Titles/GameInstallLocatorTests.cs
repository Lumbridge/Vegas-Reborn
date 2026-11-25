using VegasReborn.Core.Tests.Launch;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Tests.Titles;

public sealed class GameInstallLocatorTests
{
    private sealed class FakeRegistry : IInstallRegistry
    {
        public Dictionary<string, Dictionary<string, string>> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Set(string key, string name, string value)
        {
            if (!Keys.TryGetValue(key, out var values))
            {
                Keys[key] = values = new(StringComparer.OrdinalIgnoreCase);
            }
            values[name] = value;
        }

        public string? GetString(string key, string name) =>
            Keys.TryGetValue(key, out var values) && values.TryGetValue(name, out var value) ? value : null;

        public IReadOnlyList<string> SubKeyNames(string key) =>
            [.. Keys.Keys.Where(name => name.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase))
                .Select(name => name[(key.Length + 1)..].Split('\\')[0]).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    // A test title whose "supported" client is the bytes "supported".
    private static readonly GameTitle Title = TitleCatalog.Vegas2 with
    {
        ClientSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("supported"u8)),
    };

    private static string Install(TestDirectory directory, string relative, bool supported = true)
    {
        directory.File(Path.Combine(relative, Title.ClientExecutable), supported ? "supported" : "other build");
        return Path.Combine(directory.Path, relative);
    }

    private static GameInstallLocator Locator(TestDirectory directory, FakeRegistry registry, params string[] roots) =>
        new(registry, () => roots, Path.Combine(directory.Path, "ProgramData"),
            [Path.Combine(directory.Path, "Companion", "local")]);

    [Fact]
    public void FindsSteamLibrariesFromLibraryFoldersAndAppManifest()
    {
        using var directory = new TestDirectory();
        var registry = new FakeRegistry();
        var steam = Path.Combine(directory.Path, "Steam");
        var library = Path.Combine(directory.Path, "Library");
        registry.Set(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", steam.Replace('\\', '/'));
        directory.File(@"Steam\steamapps\libraryfolders.vdf",
            "\"libraryfolders\"\n{\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"" + library.Replace("\\", "\\\\") + "\"\n\t}\n}\n");
        directory.File($@"Library\steamapps\appmanifest_{Title.SteamAppId}.acf", "\"AppState\"\n{\n\t\"installdir\"\t\t\"Vegas Two\"\n}\n");
        var folder = Install(directory, @"Library\steamapps\common\Vegas Two");

        var found = Assert.Single(Locator(directory, registry).FindRecorded(Title));
        Assert.Equal(folder, found.Folder);
        Assert.Equal(InstallSource.Steam, found.Source);
        Assert.True(found.Supported);
    }

    [Fact]
    public void ReadsOlderNumberedLibraryEntries()
    {
        var paths = GameInstallLocator.SteamLibraryPaths("\"LibraryFolders\"\n{\n\t\"TimeNextStatsReport\"\t\t\"123\"\n\t\"1\"\t\t\"D:\\\\SteamLibrary\"\n}\n");
        Assert.Equal([@"D:\SteamLibrary"], paths);
    }

    [Fact]
    public void FindsGogEpicAndInstallerEntries()
    {
        using var directory = new TestDirectory();
        var registry = new FakeRegistry();
        var gog = Install(directory, "GOG Vegas", supported: false);
        var epic = Install(directory, "Epic Vegas", supported: false);
        var retail = Install(directory, "Retail Vegas");
        Install(directory, "Unlisted");
        var copy = Install(directory, @"Companion\local\games\vegas2");
        registry.Set(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Copy", "InstallLocation", copy);
        registry.Set(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\GOG.com\Games\2", "path", Path.Combine(directory.Path, "Missing"));
        registry.Set(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\GOG.com\Games\1", "path", gog);
        directory.File(@"ProgramData\Epic\EpicGamesLauncher\Data\Manifests\a.item",
            "{\"InstallLocation\": \"" + epic.Replace("\\", "\\\\") + "\"}");
        registry.Set(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{GUID}", "InstallLocation", retail);
        registry.Set(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\GOG 1", "InstallLocation", gog);

        var found = Locator(directory, registry).FindRecorded(Title);

        // The supported build is first; the duplicate GOG entry is listed once.
        Assert.Equal(
            [(retail, InstallSource.InstalledPrograms, true), (gog, InstallSource.Gog, false), (epic, InstallSource.Epic, false)],
            found.Select(candidate => (candidate.Folder, candidate.Source, candidate.Supported)));
    }

    [Fact]
    public void SearchFindsShallowestFirstAndRespectsDepthAndSkippedFolders()
    {
        using var directory = new TestDirectory();
        var shallow = Install(directory, @"Games\Vegas");
        var deep = Install(directory, @"a\b\c\d\Vegas");
        Install(directory, @"Users\me\AppData\Vegas");
        Install(directory, @"Companion\local\Vegas");
        Install(directory, @"a\b\c\d\e\f\g\Vegas");
        var vegas1 = directory.File(Path.Combine("Old", TitleCatalog.Vegas1.ClientExecutable), "x");

        var found = Locator(directory, new FakeRegistry(), directory.Path).Search([Title, TitleCatalog.Vegas1], maxDepth: 5);

        Assert.Equal([shallow, deep], found[Title].Select(candidate => candidate.Folder));
        Assert.All(found[Title], candidate => Assert.Equal(InstallSource.FolderSearch, candidate.Source));
        Assert.Equal(Path.Combine(directory.Path, "Old"), Assert.Single(found[TitleCatalog.Vegas1]).Folder);
        Assert.False(found[TitleCatalog.Vegas1][0].Supported);
        Assert.True(File.Exists(vegas1));
    }

    [Fact]
    public void SearchHonoursCancellation()
    {
        using var directory = new TestDirectory();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            Locator(directory, new FakeRegistry(), directory.Path).Search([Title], cancellationToken: cancel.Token));
    }
}
