using VegasReborn.Core.Display;
using VegasReborn.Core.Tests.Launch;

namespace VegasReborn.Core.Tests.Display;

public sealed class DisplayConfigurationTests
{
    [Fact]
    public void PrivateConfigRoundtripPreservesOtherSections()
    {
        using var directory = new TestDirectory();
        const string original = "[Other]\nStartupFullscreen=True\n[WinDrv.WindowsClient]\nStartupFullscreen=True\nStartupResolutionX=800\nStartupResolutionY=600\nUseSound=True\n";
        var target = directory.File("KellerGame/Config/PC/KellerEngine.ini", original);
        var monitor = new DisplayMonitor("test", new MonitorBounds(-1920, 0, 0, 1080), false);
        DisplayConfiguration.Configure(directory.Path, DisplayMode.Borderless, monitor);
        var text = File.ReadAllText(target);
        Assert.Contains("[Other]\nStartupFullscreen=True", text);
        Assert.Contains("StartupFullscreen=False", text);
        Assert.Contains("StartupResolutionX=1920", text);
        Assert.Contains("StartupResolutionY=1080", text);
        Assert.Contains("UseSound=True", text);
        DisplayConfiguration.Configure(directory.Path, DisplayMode.Windowed);
        Assert.Contains("StartupResolutionX=800", File.ReadAllText(target));
        Assert.Contains("StartupResolutionY=600", File.ReadAllText(target));
        DisplayConfiguration.Configure(directory.Path, DisplayMode.Fullscreen);
        Assert.Contains("[WinDrv.WindowsClient]\nStartupFullscreen=True", File.ReadAllText(target));
        DisplayConfiguration.Configure(directory.Path, DisplayMode.GameDefault);
        Assert.Equal(original, File.ReadAllText(target));
        Assert.False(File.Exists(Path.Combine(directory.Path, ".companion-display.json")));
    }

    [Fact]
    public void GameDefaultWithoutBaselineChangesNothing()
    {
        using var directory = new TestDirectory();
        const string original = "[WinDrv.WindowsClient]\r\nStartupFullscreen=True\r\n";
        var target = directory.File("KellerGame/Config/DefaultEngine.ini", original);
        DisplayConfiguration.Configure(directory.Path, DisplayMode.GameDefault);
        Assert.Equal(original, File.ReadAllText(target));
    }

    [Fact]
    public void DisconnectedMonitorFallsBackToPrimary()
    {
        var primary = new DisplayMonitor("primary", new MonitorBounds(0, 0, 1920, 1080), true);
        var secondary = new DisplayMonitor("other", new MonitorBounds(-1280, 0, 0, 720), false);
        Assert.Same(primary, DisplayConfiguration.SelectMonitor([secondary, primary], "missing"));
        Assert.Same(secondary, DisplayConfiguration.SelectMonitor([primary, secondary], "other"));
        Assert.Null(DisplayConfiguration.SelectMonitor([]));
        Assert.Equal("primary · 1920 × 1080 · Primary", primary.Label);
        Assert.Equal("other · 1280 × 720", secondary.Label);
    }

    [Fact]
    public void ModeNamesMatchStoredPreferences()
    {
        Assert.Equal(["Game default", "Borderless", "Windowed", "Fullscreen"], DisplayModes.All.Select(DisplayModes.Name));
        Assert.True(DisplayModes.TryParse("Game default", out var mode));
        Assert.Equal(DisplayMode.GameDefault, mode);
        Assert.False(DisplayModes.TryParse("game default", out _));
    }
}
