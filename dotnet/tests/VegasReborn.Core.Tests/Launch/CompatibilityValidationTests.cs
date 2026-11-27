using System.Text.Json.Nodes;
using VegasReborn.Core.Display;
using VegasReborn.Core.Launch;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Tests.Launch;

/// <summary>Classify the startup report without weakening code, name and display guards.</summary>
public sealed class CompatibilityValidationTests
{
    private static readonly GameTitle Vegas1 = TitleCatalog.Vegas1;

    private static void Rejects(JsonObject report, string expected, string? claimedName = null,
        DisplayMode mode = DisplayMode.GameDefault, IReadOnlySet<string>? disabled = null)
    {
        var error = Assert.Throws<LaunchException>(() => CompatibilityValidation.Validate(Vegas1, report, claimedName, mode, disabled));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void WindowedRequiresVerifiedRendererAndViewportNotOnlyConfigs()
    {
        var report = new JsonObject { ["viewportHookInstalled"] = true, ["deviceVerified"] = true, ["actualWindowed"] = false };
        Assert.False(CompatibilityValidation.LaunchOptionsReady(report, DisplayMode.Windowed, false));
        report["actualWindowed"] = true;
        Assert.True(CompatibilityValidation.LaunchOptionsReady(report, DisplayMode.Windowed, false));
        report["viewportHookInstalled"] = false;
        Assert.False(CompatibilityValidation.LaunchOptionsReady(report, DisplayMode.Borderless, false));
    }

    [Fact]
    public void SkipIntrosRequiresArmedMovieHookAndFailureIsExplicit()
    {
        Assert.False(CompatibilityValidation.LaunchOptionsReady([], DisplayMode.GameDefault, true));
        Assert.True(CompatibilityValidation.LaunchOptionsReady(new JsonObject { ["introSequenceHookInstalled"] = true }, DisplayMode.GameDefault, true));
        var error = Assert.Throws<LaunchException>(() =>
            CompatibilityValidation.LaunchOptionsReady(new JsonObject { ["hookFailed"] = true }, DisplayMode.Windowed, true));
        Assert.Contains("display settings", error.Message);
    }

    [Fact]
    public void ReportedHookFailureIsNotACodeMismatch() =>
        Rejects(new JsonObject
        {
            ["ready"] = false, ["signatures"] = 16383, ["unlockHookStatus"] = 7, ["taskHookStatus"] = -1, ["prefillHookStatus"] = -1,
        }, "executable code", mode: DisplayMode.Windowed);

    [Fact]
    public void ActualMismatchAndOtherSetupFailureRemainBlocked()
    {
        Rejects(new JsonObject { ["ready"] = false, ["signatures"] = 8191 }, "does not match");
        Rejects(new JsonObject { ["ready"] = false, ["signatures"] = 16383, ["executablePagesReady"] = true }, "code matched");
    }

    [Fact]
    public void SuccessStillRequiresNameAndBorderlessVerification()
    {
        var report = Reports.Vegas1Ready();
        CompatibilityValidation.Validate(Vegas1, report, displayMode: DisplayMode.Windowed);
        foreach (var key in new[] { "proceduralAnimationVerified", "reportXpVerified", "nativeStatisticsVerified", "nativePecDefaultsVerified", "punkBusterRemovedVerified", "nativeAwardsVerified" })
        {
            var changed = Reports.Vegas1Ready();
            changed[key] = false;
            Rejects(changed, "compatibility module");
        }
        var old = Reports.Vegas1Ready();
        old["nativeVersion"] = 10;
        Rejects(old, "compatibility module");
        Rejects(report, "player-name", claimedName: "Tester");
        Rejects(report, "Borderless", mode: DisplayMode.Borderless);
    }

    [Fact]
    public void AlwaysOnFeaturesMustBePresentEvenWhenADisableIsRequested()
    {
        var disabled = new HashSet<string> { TitleCatalog.ReportXp, TitleCatalog.PlayerRank };
        var report = Reports.Vegas1Ready();
        report["reportXpVerified"] = false;
        report["nativeRankVerified"] = false;
        Rejects(report, "compatibility module");
        // Every Vegas 1 fix is always on: a stale disable request does not excuse a missing feature.
        report["disabledFeatures"] = new JsonArray("report-xp", "player-rank");
        Rejects(report, "compatibility module", disabled: disabled);
        // The adapter disabled something on its own.
        var unrequested = Reports.Vegas1Ready();
        unrequested["disabledFeatures"] = new JsonArray("ranked-access");
        Rejects(unrequested, "compatibility module");
        var statistics = Reports.Vegas1Ready();
        statistics["nativeStatisticsVerified"] = false;
        Rejects(statistics, "compatibility module", disabled: new HashSet<string> { TitleCatalog.LeaderboardStatistics });
    }

    [Fact]
    public void Vegas2ReportRequiresReadyMatchingAdapter()
    {
        var title = TitleCatalog.Vegas2;
        var report = Reports.Vegas2Ready();
        CompatibilityValidation.Validate(title, report);
        report["nativeVersion"] = 2;
        Assert.Throws<LaunchException>(() => CompatibilityValidation.Validate(title, report));
        foreach (var field in new[] { "proceduralAnimationVerified", "discordSignInVerified", "soloMatchVerified", "fullXpVerified", "punkBusterRemovedVerified", "fireSoundLoopVerified" })
        {
            var missing = Reports.Vegas2Ready();
            missing[field] = false;
            Assert.Throws<LaunchException>(() => CompatibilityValidation.Validate(title, missing));
        }
        var failed = new JsonObject { ["ready"] = false, ["title"] = "vegas2", ["nativeVersion"] = 1 };
        Assert.Contains("setup failed", Assert.Throws<LaunchException>(() => CompatibilityValidation.Validate(title, failed)).Message);
        Assert.Throws<LaunchException>(() => CompatibilityValidation.Validate(title, Reports.Vegas1Ready()));
    }

    [Fact]
    public void Vegas2HostNetworkRateMismatchDoesNotBlockLaunch()
    {
        var report = Reports.Vegas2Ready();
        report["hostNetworkRateVerified"] = false;
        report["hostNetworkRateStatus"] = "codeMismatch";
        CompatibilityValidation.Validate(TitleCatalog.Vegas2, report);
        Assert.NotNull(TitleCatalog.Vegas2.Fix(TitleCatalog.HostNetworkRate));
        Assert.NotNull(TitleCatalog.Vegas2.Fix(TitleCatalog.FireSoundLoop));
    }

    [Fact]
    public void Vegas2ClaimRequiresVerifiedNameHook()
    {
        var title = TitleCatalog.Vegas2;
        var absent = Reports.Vegas2Ready();
        absent["claimedNameVerified"] = false;
        absent["claimedNameStatus"] = "claimFileAbsent";
        CompatibilityValidation.Validate(title, absent);
        Assert.Contains("player-name", Assert.Throws<LaunchException>(
            () => CompatibilityValidation.Validate(title, absent, claimedName: "Tester")).Message);
        CompatibilityValidation.Validate(title, Reports.Vegas2Ready(), claimedName: "Tester");
    }

    [Fact]
    public void FieldOfViewRequiresTheRequestedAngleVerified()
    {
        foreach (var (title, ready) in new[] { (TitleCatalog.Vegas1, Reports.Vegas1Ready()), (TitleCatalog.Vegas2, Reports.Vegas2Ready()) })
        {
            CompatibilityValidation.Validate(title, ready);
            Assert.Contains("Field of view", Assert.Throws<LaunchException>(
                () => CompatibilityValidation.Validate(title, ready, fieldOfView: 75)).Message);
            ready["fieldOfViewRequested"] = 75;
            ready["fieldOfViewVerified"] = true;
            CompatibilityValidation.Validate(title, ready, fieldOfView: 75);
            Assert.Throws<LaunchException>(() => CompatibilityValidation.Validate(title, ready, fieldOfView: 80));
            ready["fieldOfViewVerified"] = false;
            Assert.Throws<LaunchException>(() => CompatibilityValidation.Validate(title, ready, fieldOfView: 75));
        }
    }

    [Fact]
    public void Vegas2SkipIntrosRequiresVerifiedSequenceHook()
    {
        var title = TitleCatalog.Vegas2;
        var played = Reports.Vegas2Ready();
        played["skipIntrosVerified"] = false;
        played["skipIntrosStatus"] = "notRequested";
        CompatibilityValidation.Validate(title, played);
        Assert.Throws<LaunchException>(() => CompatibilityValidation.Validate(title, played, skipIntros: true));
        CompatibilityValidation.Validate(title, Reports.Vegas2Ready(), skipIntros: true);
    }

    [Fact]
    public void Vegas2DisplayModesRequireVerifiedViewportAndStyle()
    {
        var title = TitleCatalog.Vegas2;
        var report = Reports.Vegas2Ready();
        CompatibilityValidation.Validate(title, report, displayMode: DisplayMode.Fullscreen);
        Assert.Throws<LaunchException>(() => CompatibilityValidation.Validate(title, report, displayMode: DisplayMode.Windowed));
        report["displayViewportVerified"] = true;
        CompatibilityValidation.Validate(title, report, displayMode: DisplayMode.Windowed);
        Assert.Contains("Borderless", Assert.Throws<LaunchException>(
            () => CompatibilityValidation.Validate(title, report, displayMode: DisplayMode.Borderless)).Message);
        report["displayStyleVerified"] = true;
        CompatibilityValidation.Validate(title, report, displayMode: DisplayMode.Borderless);
    }
}
