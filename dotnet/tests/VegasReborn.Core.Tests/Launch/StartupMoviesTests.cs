using System.Text;
using VegasReborn.Core.Launch;

namespace VegasReborn.Core.Tests.Launch;

/// <summary>Copied retail INI changes; these checks do not launch the game.</summary>
public sealed class StartupMoviesTests
{
    [Fact]
    public void ToggleRestoresOriginalAndKeepsLoadingAndMenuMovies()
    {
        using var directory = new TestDirectory();
        var originals = new Dictionary<string, byte[]>();
        foreach (var (relative, enabled) in StartupMovies.MovieFiles.Zip(["TRUE", "FALSE"]))
        {
            var data = Encoding.UTF8.GetBytes("﻿[INTRO]\r\nEnable=" + enabled + " ; startup\r\nFilename=logos_PC.bik\r\nChain=INTRO1\r\n"
                + "[INTRO1]\r\nEnable=TRUE\r\nFilename=LoadingE3.bik\r\n"
                + "[LOADING]\r\nEnable=TRUE\r\nFilename=LoadingE3.bik\r\n"
                + "[MENU]\r\nEnable=TRUE\r\nFilename=SplashScreen.bik\r\n");
            originals[directory.Bytes("KellerGame/Config/" + relative, data)] = data;
        }
        StartupMovies.Configure(directory.Path, false);
        Assert.False(File.Exists(Path.Combine(directory.Path, ".companion-movies.json")));
        for (var round = 0; round < 2; round++)
        {
            StartupMovies.Configure(directory.Path, true);
            foreach (var (path, original) in originals)
            {
                Assert.Equal(Encoding.UTF8.GetString(original).Replace("Enable=TRUE ; startup", "Enable=FALSE ; startup"),
                    Encoding.UTF8.GetString(File.ReadAllBytes(path)));
                Assert.Equal(0xEF, File.ReadAllBytes(path)[0]);
            }
        }
        StartupMovies.Configure(directory.Path, false);
        foreach (var (path, original) in originals)
        {
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        Assert.False(File.Exists(Path.Combine(directory.Path, ".companion-movies.json")));
    }

    [Fact]
    public void UnsupportedConfigDoesNotPartiallyChangeFiles()
    {
        using var directory = new TestDirectory();
        directory.File("KellerGame/Config/MovieLoading.ini", "[INTRO]\nEnable=TRUE\n");
        directory.File("KellerGame/Config/PC/MovieLoading.ini", "[LOADING]\nEnable=TRUE\n");
        Assert.Throws<LaunchException>(() => StartupMovies.Configure(directory.Path, true));
        Assert.Equal("[INTRO]\nEnable=TRUE\n", File.ReadAllText(Path.Combine(directory.Path, "KellerGame/Config/MovieLoading.ini")));
        Assert.False(File.Exists(Path.Combine(directory.Path, ".companion-movies.json")));
    }

    [Fact]
    public void SingleRootMovieFileIsSupportedAsShippedByVegas2()
    {
        using var directory = new TestDirectory();
        var path = directory.File("KellerGame/Config/MovieLoading.ini", "[INTRO]\r\nEnable=TRUE\r\nLoop = FALSE\r\n\r\n[INTRO1]\r\nEnable=TRUE\r\n");
        StartupMovies.Configure(directory.Path, true);
        Assert.Equal("[INTRO]\r\nEnable=FALSE\r\nLoop = FALSE\r\n\r\n[INTRO1]\r\nEnable=TRUE\r\n", File.ReadAllText(path));
        StartupMovies.Configure(directory.Path, false);
        Assert.Equal("[INTRO]\r\nEnable=TRUE\r\nLoop = FALSE\r\n\r\n[INTRO1]\r\nEnable=TRUE\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void MissingMovieSettingsAreReported()
    {
        using var directory = new TestDirectory();
        Assert.Contains("missing", Assert.Throws<LaunchException>(() => StartupMovies.Configure(directory.Path, true)).Message);
    }
}
