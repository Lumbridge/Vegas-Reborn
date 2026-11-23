using System.Text.Json;
using System.Text.Json.Nodes;
using VegasReborn.Core.Display;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Launch;

/// <summary>
/// Classifies the native adapter's compat-status.json without weakening the code, name
/// and display guards. A toggleable feature may be absent only when the player turned it
/// off and the adapter reports it in <c>disabledFeatures</c>.
/// </summary>
public static class CompatibilityValidation
{
    private const string UpdateModule = "Update the companion's online compatibility module before playing";

    /// <summary>Toggle fix id to the readiness field the Vegas 1 adapter reports for it.</summary>
    public static IReadOnlyDictionary<string, string> NativeFeatureFields { get; } = new Dictionary<string, string>
    {
        [TitleCatalog.HeadAnimation] = "proceduralAnimationVerified",
        [TitleCatalog.SoloHunt] = "nativeSoloHuntVerified",
        [TitleCatalog.ReportXp] = "reportXpVerified",
        [TitleCatalog.PlayerRank] = "nativeRankVerified",
        [TitleCatalog.RankedAccess] = "rankedAccessVerified",
    };

    private static readonly string[] RequiredVegas1Fields =
    [
        "nativeStatisticsVerified", "nativeRankedHostVerified", "nativeUnlockPolicyVerified", "nativePecDefaultsVerified",
    ];

    public static void Validate(
        GameTitle title,
        JsonObject compatibility,
        string? claimedName = null,
        DisplayMode displayMode = DisplayMode.GameDefault,
        IReadOnlySet<string>? disabledFixes = null)
    {
        disabledFixes ??= new HashSet<string>();
        if (!TitleLaunchProfile.For(title).NativeGameHooks)
        {
            ValidateGeneric(title, compatibility, claimedName);
            return;
        }
        if (!Truthy(compatibility["ready"]))
        {
            if (!NumberEquals(compatibility["signatures"], 16383))
            {
                throw new LaunchException("The game's native code does not match this compatibility module");
            }
            if (IsFalse(compatibility["executablePagesReady"])
                || new[] { "unlockHookStatus", "taskHookStatus", "prefillHookStatus" }.Any(key => NumberEquals(compatibility[key], 7)))
            {
                throw new LaunchException("The game did not finish preparing its executable code. Close the game and retry launching.");
            }
            throw new LaunchException("The game's code matched, but compatibility setup failed. Update the companion and retry.");
        }
        if (!NumberEquals(compatibility["nativeVersion"], title.NativeVersion)
            || RequiredVegas1Fields.Any(key => !Truthy(compatibility[key])))
        {
            throw new LaunchException(UpdateModule);
        }
        var reportedDisabled = ReportedDisabled(compatibility);
        foreach (var (fix, field) in NativeFeatureFields)
        {
            if (disabledFixes.Contains(fix) && title.Fix(fix)?.Kind == FixKind.Toggle)
            {
                // Turned off by the player: the adapter must confirm it read the request.
                if (!reportedDisabled.Contains(fix))
                {
                    throw new LaunchException(UpdateModule);
                }
            }
            else if (!Truthy(compatibility[field]) || reportedDisabled.Contains(fix))
            {
                throw new LaunchException(UpdateModule);
            }
        }
        if (!string.IsNullOrEmpty(claimedName)
            && (!Truthy(compatibility["claimedNameVerified"]) || !Truthy(compatibility["claimedSessionNameVerified"])))
        {
            throw new LaunchException("The game's player-name setup was not verified; update the companion and retry");
        }
        if (displayMode == DisplayMode.Borderless && !Truthy(compatibility["displayStyleVerified"]))
        {
            throw new LaunchException("Borderless setup was not verified. Update the companion's native compatibility module, then retry.");
        }
    }

    /// <summary>Fix id to the readiness field a title-specific adapter reports for it.</summary>
    public static IReadOnlyDictionary<string, string> GenericFixFields { get; } = new Dictionary<string, string>
    {
        [TitleCatalog.HeadAnimation] = "proceduralAnimationVerified",
        [TitleCatalog.DiscordSignIn] = "discordSignInVerified",
        [TitleCatalog.SoloMatch] = "soloMatchVerified",
        [TitleCatalog.FullXp] = "fullXpVerified",
    };

    private static void ValidateGeneric(GameTitle title, JsonObject compatibility, string? claimedName)
    {
        if (!Truthy(compatibility["ready"]))
        {
            throw new LaunchException("The game's compatibility setup failed. Update the companion and retry.");
        }
        if (!NumberEquals(compatibility["nativeVersion"], title.NativeVersion)
            || compatibility["title"] is not JsonValue value || !value.TryGetValue<string>(out var key) || key != title.Key)
        {
            throw new LaunchException(UpdateModule);
        }
        foreach (var fix in title.Fixes)
        {
            if (GenericFixFields.TryGetValue(fix.Id, out var field) && !Truthy(compatibility[field]))
            {
                throw new LaunchException(UpdateModule);
            }
        }
        if (!string.IsNullOrEmpty(claimedName) && title.Fix(TitleCatalog.ClaimedName) is not null
            && !Truthy(compatibility["claimedNameVerified"]))
        {
            throw new LaunchException("The game's player-name setup was not verified; update the companion and retry");
        }
    }

    /// <summary>Whether launch-options-status.json confirms the requested display and intro options.</summary>
    public static bool LaunchOptionsReady(JsonObject report, DisplayMode displayMode, bool skipIntros)
    {
        if (Truthy(report["hookFailed"]))
        {
            throw new LaunchException("The game's requested display settings could not be installed. Update the companion and retry.");
        }
        if (skipIntros && !Truthy(report["introSequenceHookInstalled"]))
        {
            return false;
        }
        if (displayMode is DisplayMode.Windowed or DisplayMode.Borderless)
        {
            return Truthy(report["viewportHookInstalled"]) && Truthy(report["deviceVerified"]) && Truthy(report["actualWindowed"]);
        }
        return true;
    }

    private static HashSet<string> ReportedDisabled(JsonObject compatibility)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (compatibility["disabledFeatures"] is JsonArray items)
        {
            foreach (var item in items)
            {
                if (item is JsonValue value && value.TryGetValue<string>(out var id))
                {
                    result.Add(id);
                }
            }
        }
        return result;
    }

    /// <summary>Python truthiness for JSON values.</summary>
    internal static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonArray array => array.Count > 0,
        JsonObject obj => obj.Count > 0,
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => value.GetValue<string>().Length > 0,
            JsonValueKind.Number => Number(value) != 0,
            _ => false,
        },
        _ => false,
    };

    internal static bool IsFalse(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.False;

    internal static bool NumberEquals(JsonNode? node, double expected) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && Number(value) == expected;

    private static double Number(JsonValue value) =>
        double.Parse(value.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
}
