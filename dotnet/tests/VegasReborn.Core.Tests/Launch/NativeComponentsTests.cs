using VegasReborn.Core;
using System.ComponentModel;
using VegasReborn.Core.Launch;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Tests.Launch;

public sealed class NativeComponentsTests
{
    [Fact]
    public void MissingHelperIsNamedBeforeLaunch()
    {
        using var directory = new TestDirectory();
        var error = Assert.Throws<NativeLaunchException>(() => NativeComponents.RequireComponents(directory.Path, TitleCatalog.Vegas1));
        Assert.Contains("VegasSteamControl.exe", error.Message);
        Assert.Contains("Required launch component is missing", error.Message);
    }

    [Theory]
    [InlineData(225)]
    [InlineData(226)]
    public void SecurityBlockReportsComponentAndWindowsErrorWithoutDetails(int code)
    {
        var message = NativeComponents.ComponentError("VegasSteamControl.exe", new Win32Exception(code, "sensitive raw diagnostic"));
        Assert.Contains("Windows Security", message);
        Assert.Contains(code.ToString(), message);
        Assert.Contains("VegasSteamControl.exe", message);
        Assert.DoesNotContain("sensitive", message);
    }

    [Fact]
    public void AccessDeniedAndOtherErrorsAreDistinct()
    {
        Assert.Contains("denied access", NativeComponents.ComponentError("x.exe", new UnauthorizedAccessException()));
        Assert.Contains("(Windows error 32)", NativeComponents.ComponentError("x.exe", new IOException("busy", unchecked((int)0x80070020))));
        Assert.Contains("missing: x.exe", NativeComponents.ComponentError("x.exe", new Win32Exception(2)));
    }

    [Fact]
    public void AccessibleComponentsAreAcceptedPerTitle()
    {
        using var directory = new TestDirectory();
        foreach (var name in NativeComponents.Required(TitleCatalog.Vegas1))
        {
            directory.File(name, "authored fixture");
        }
        NativeComponents.RequireComponents(directory.Path, TitleCatalog.Vegas1);
        var error = Assert.Throws<NativeLaunchException>(() => NativeComponents.RequireComponents(directory.Path, TitleCatalog.Vegas2));
        Assert.Contains("VegasCompanionVegas2.dll", error.Message);
    }

    [Fact]
    public void ReleaseAdapterIsLoadedFromRuntimeFolder()
    {
        using var directory = new TestDirectory();
        var paths = new AppPaths(directory.Path);
        foreach (var name in NativeComponents.Required(TitleCatalog.Vegas1))
        {
            directory.File(Path.Combine("local/companion/bin", name), "authored fixture");
        }
        var development = paths.NativeAdapter(TitleCatalog.Vegas2.NativeAdapter);
        Assert.Equal(Path.Combine(paths.NativeBin, "VegasCompanionVegas2.dll"), development);
        Assert.Throws<NativeLaunchException>(() => NativeComponents.RequireComponents(paths.NativeBin, TitleCatalog.Vegas2, development));

        var release = directory.File(Path.Combine(paths.RuntimeNative, "VegasCompanionVegas2.dll"), "authored fixture");
        Assert.Equal(release, paths.NativeAdapter(TitleCatalog.Vegas2.NativeAdapter));
        NativeComponents.RequireComponents(paths.NativeBin, TitleCatalog.Vegas2, release);
        Assert.Equal(Path.Combine(paths.NativeBin, TitleCatalog.Vegas1.NativeAdapter), paths.NativeAdapter(TitleCatalog.Vegas1.NativeAdapter));
    }

    [Fact]
    public void EmptyComponentReportsIncompleteInstallation()
    {
        using var directory = new TestDirectory();
        directory.File("VegasSteamControl.exe", "");
        var error = Assert.Throws<NativeLaunchException>(() => NativeComponents.RequireComponents(directory.Path, TitleCatalog.Vegas1));
        Assert.Contains("component is empty: VegasSteamControl.exe", error.Message);
        Assert.DoesNotContain("Repair installation", error.Message);
    }

    [Fact]
    public void FailedExitAndInvalidResultAreSpecificAndSanitized()
    {
        var failed = Assert.Throws<NativeLaunchException>(() => NativeComponents.ParseResult("helper.exe", 6, "secret"));
        Assert.Contains("exit code 6", failed.Message);
        var invalid = Assert.Throws<NativeLaunchException>(() => NativeComponents.ParseResult("helper.exe", 0, "private-secret"));
        Assert.Contains("invalid launch result", invalid.Message);
        Assert.DoesNotContain("secret", failed.Message + invalid.Message);
        Assert.Throws<NativeLaunchException>(() => NativeComponents.ParseResult("helper.exe", 0, "[1]"));
        Assert.True((bool)NativeComponents.ParseResult("helper.exe", 0, "{\"ready\":true}")["ready"]!);
    }

    [Fact]
    public async Task MissingExecutableIsReportedAsMissingComponent()
    {
        var error = await Assert.ThrowsAsync<NativeLaunchException>(() =>
            NativeComponents.RunAsync([Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "-VegasInjector.exe"), "private-route-secret"]));
        Assert.Contains("Required launch component is missing", error.Message);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public void RuntimeGateIsCachedByContentHash()
    {
        using var directory = new TestDirectory();
        var source = directory.File("bin/VegasSteamGate.dll", "gate image v1");
        var cache = Path.Combine(directory.Path, "cache");
        var first = NativeComponents.RuntimeGate(source, cache);
        Assert.Equal("gate image v1", File.ReadAllText(first));
        Assert.Equal(first, NativeComponents.RuntimeGate(source, cache));
        File.WriteAllText(source, "gate image v2");
        var second = NativeComponents.RuntimeGate(source, cache);
        Assert.NotEqual(first, second);
        Assert.Equal("gate image v1", File.ReadAllText(first));
        Assert.Equal("VegasSteamGate.dll", Path.GetFileName(second));
    }
}
