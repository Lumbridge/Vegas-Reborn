using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VegasReborn.Core.Content;
using VegasReborn.Core.Launch;
using VegasReborn.Core.Tests.Launch;
using Xunit;

namespace VegasReborn.Core.Tests.Content;

/// <summary>Writes a verified Maps Pack store (files plus record) for tests.</summary>
internal static class MapsPackFixture
{
    public const string Map = "KellerGame/Content/CookedPc/MapsPack/PAK_01_Kitchen/PAK_01_Kitchen.usdx";
    public const string MapIni = "KellerGame/Content/CookedPc/MapsPack/PAK_01_Kitchen/PAK_01_Kitchen.ini";
    public const string Names = "KellerGame/Localization/INT/PAK_01_Kitchen.int";

    public const string ShippedIni =
        "[GameMode0]\r\nm_eGameMode=GAMEMODE_DEATHMATCH\r\nm_iMaxPlayers=16\r\nm_iMaxPlayersListenServer=14\r\n\r\n"
        + "[GameMode1]\r\nm_eGameMode=GAMEMODE_TERROHUNT\r\nm_iMaxPlayers=4\r\n";

    public static Dictionary<string, string> Default() => new()
    {
        [Map] = "map data",
        [MapIni] = ShippedIni,
        [Names] = "[Kitchen]\r\nName=Kitchen\r\n",
    };

    public static void Install(AppPaths paths, IReadOnlyDictionary<string, string> files)
    {
        Remove(paths);
        var entries = new List<ContentFile>();
        foreach (var (relative, text) in files)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var path = Path.Combine(paths.MapsPack, "files", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            entries.Add(new ContentFile(relative, bytes.Length, PrivateFile.Sha256(bytes), "maps.zip"));
        }
        new MapsPackRecord(1, entries).Write(Path.Combine(paths.MapsPack, "record.json"));
    }

    public static void Remove(AppPaths paths)
    {
        if (Directory.Exists(paths.MapsPack))
        {
            Directory.Delete(paths.MapsPack, recursive: true);
        }
    }
}

public sealed class MapsPackLinkTests : IDisposable
{
    private readonly TestDirectory _dir = new();
    private readonly AppPaths _paths;
    private readonly string _copy;

    public MapsPackLinkTests()
    {
        _paths = new AppPaths(Path.Combine(_dir.Path, "install"));
        _copy = Path.Combine(_dir.Path, "game");
        _dir.File("game/Binaries/R6Vegas_Game.exe", "game");
        _dir.File("game/KellerGame/Localization/INT/Core.int", "[Core]\r\n");
        _dir.File("game/KellerGame/Content/CookedPc/Maps/01_Mexico/01_Mexico_01.ini", "[GameMode0]\r\nm_eGameMode=GAMEMODE_STORY\r\n");
    }

    public void Dispose() => _dir.Dispose();

    private string Copy(string relative) => Path.Combine(_copy, relative);

    private string Store(string relative) => Path.Combine(_paths.MapsPack, "files", relative);

    [Fact]
    public void InstalledPackIsLinkedAndInisRaised()
    {
        MapsPackFixture.Install(_paths, MapsPackFixture.Default());

        Assert.True(MapsPackLink.Apply(_paths, _copy));

        Assert.Equal("map data", File.ReadAllText(Copy(MapsPackFixture.Map)));
        Assert.True(MapsPackLink.SameFile(Copy(MapsPackFixture.Map), Store(MapsPackFixture.Map)));
        Assert.True(MapsPackLink.SameFile(Copy(MapsPackFixture.Names), Store(MapsPackFixture.Names)));
        Assert.Equal(MissionPlayerLimits.Raise(MapsPackFixture.ShippedIni), File.ReadAllText(Copy(MapsPackFixture.MapIni)));
        Assert.False(MapsPackLink.SameFile(Copy(MapsPackFixture.MapIni), Store(MapsPackFixture.MapIni)));
        // The store keeps the original, so the record still verifies.
        Assert.Equal(MapsPackFixture.ShippedIni, File.ReadAllText(Store(MapsPackFixture.MapIni)));
        Assert.Equal(MapsPackState.Installed, MapsPackRecord.Check(_paths, out _));
        var listed = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Copy(MapsPackLink.LinkedList)))!;
        Assert.Equal(MapsPackFixture.Default().Keys.Order(), listed.Order());
    }

    [Fact]
    public void RelinkIsANoOpWhenUnchanged()
    {
        MapsPackFixture.Install(_paths, MapsPackFixture.Default());
        MapsPackLink.Apply(_paths, _copy);
        var past = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var files = Directory.GetFiles(_copy, "*", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            File.SetLastWriteTimeUtc(file, past);
        }

        Assert.True(MapsPackLink.Apply(_paths, _copy));

        Assert.Equal(files.Order(), Directory.GetFiles(_copy, "*", SearchOption.AllDirectories).Order());
        Assert.All(files, file => Assert.Equal(past, File.GetLastWriteTimeUtc(file)));
    }

    [Fact]
    public void ChangedCopyFileIsRelinked()
    {
        MapsPackFixture.Install(_paths, MapsPackFixture.Default());
        MapsPackLink.Apply(_paths, _copy);
        File.Delete(Copy(MapsPackFixture.Map));
        File.WriteAllText(Copy(MapsPackFixture.Map), "map date");

        MapsPackLink.Apply(_paths, _copy);

        Assert.True(MapsPackLink.SameFile(Copy(MapsPackFixture.Map), Store(MapsPackFixture.Map)));
        Assert.Equal("map data", File.ReadAllText(Copy(MapsPackFixture.Map)));
    }

    [Fact]
    public void RemovedPackIsUnlinkedOnNextPrepare()
    {
        var before = Snapshot(_copy);
        MapsPackFixture.Install(_paths, MapsPackFixture.Default());
        Assert.True(MapsPackLink.Apply(_paths, _copy));
        MapsPackFixture.Remove(_paths);

        Assert.False(MapsPackLink.Apply(_paths, _copy));

        Assert.Equal(before, Snapshot(_copy));
    }

    [Fact]
    public void FileDroppedFromThePackIsRemoved()
    {
        MapsPackFixture.Install(_paths, MapsPackFixture.Default());
        MapsPackLink.Apply(_paths, _copy);
        var smaller = MapsPackFixture.Default();
        smaller.Remove(MapsPackFixture.Names);
        MapsPackFixture.Install(_paths, smaller);

        Assert.True(MapsPackLink.Apply(_paths, _copy));

        Assert.False(File.Exists(Copy(MapsPackFixture.Names)));
        Assert.True(File.Exists(Copy("KellerGame/Localization/INT/Core.int")));
        Assert.True(File.Exists(Copy(MapsPackFixture.Map)));
        var listed = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Copy(MapsPackLink.LinkedList)))!;
        Assert.DoesNotContain(MapsPackFixture.Names, listed);
    }

    [Fact]
    public void DamagedStoreRefusesToLink()
    {
        MapsPackFixture.Install(_paths, MapsPackFixture.Default());
        File.WriteAllText(Store(MapsPackFixture.Map), "map date");

        var error = Assert.Throws<LaunchException>(() => MapsPackLink.Apply(_paths, _copy));

        Assert.Equal("The Maps Pack is damaged. Download it again or remove it.", error.Message);
        Assert.False(File.Exists(Copy(MapsPackFixture.Map)));
    }

    [Fact]
    public void LinkedListNeverDeletesOutsideTheCopy()
    {
        var outside = _dir.File("x", "keep");
        var sibling = _dir.File("other/KellerGame/Content/CookedPc/MapsPack/a.usdx", "keep");
        File.WriteAllText(Copy(MapsPackLink.LinkedList), JsonSerializer.Serialize(new[]
        {
            "../x",
            "..\\x",
            outside,
            "KellerGame/Content/CookedPc/MapsPack/../../../../../x",
            "../other/KellerGame/Content/CookedPc/MapsPack/a.usdx",
            "Binaries/R6Vegas_Game.exe",
        }));

        Assert.False(MapsPackLink.Apply(_paths, _copy));

        Assert.Equal("keep", File.ReadAllText(outside));
        Assert.Equal("keep", File.ReadAllText(sibling));
        Assert.Equal("game", File.ReadAllText(Copy("Binaries/R6Vegas_Game.exe")));
        Assert.False(File.Exists(Copy(MapsPackLink.LinkedList)));
    }

    [Fact]
    public void LinkedListNeverFollowsAJunctionOutOfTheCopy()
    {
        var target = Path.Combine(_dir.Path, "elsewhere");
        var victim = _dir.File("elsewhere/a.usdx", "keep");
        var junction = Copy("KellerGame/Content/CookedPc/MapsPack");
        Directory.CreateDirectory(Path.GetDirectoryName(junction)!);
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"")
               { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
        {
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }
        File.WriteAllText(Copy(MapsPackLink.LinkedList), JsonSerializer.Serialize(new[] { "KellerGame/Content/CookedPc/MapsPack/a.usdx" }));

        MapsPackLink.Apply(_paths, _copy);

        Assert.Equal("keep", File.ReadAllText(victim));
        Directory.Delete(junction);
    }

    /// <summary>Every file's bytes and every directory, relative to the root.</summary>
    private static SortedDictionary<string, string> Snapshot(string root)
    {
        var tree = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
        {
            tree[Path.GetRelativePath(root, directory) + "/"] = "";
        }
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            tree[Path.GetRelativePath(root, file)] = Convert.ToHexString(File.ReadAllBytes(file));
        }
        return tree;
    }
}
