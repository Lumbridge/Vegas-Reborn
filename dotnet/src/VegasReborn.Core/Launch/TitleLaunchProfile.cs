using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Launch;

/// <summary>
/// Launch differences between titles that are not plain title data: what the native
/// adapter supports and which copy repairs are grounded for that game.
/// </summary>
internal sealed record TitleLaunchProfile
{
    /// <summary>
    /// The adapter installs game-code hooks (login, name, display and intro options,
    /// toggleable features) and reports the full readiness document.
    /// </summary>
    public required bool NativeGameHooks { get; init; }

    /// <summary>Copy-relative files that must match these SHA-256 values after preparation.</summary>
    public IReadOnlyDictionary<string, string> PinnedFiles { get; init; } = new Dictionary<string, string>();

    /// <summary>Point the copy's [URL] start map at the menu (Vegas 1 engine ini files).</summary>
    public bool RewriteMenuUrls { get; init; }

    /// <summary>Repair the leaderboard carrier and request the native-base-v1 statistics codec.</summary>
    public bool StatisticsCarrier { get; init; }

    /// <summary>SHA-256 of the shipped signature.pub (SignaturePub trust key).</summary>
    public string? OriginalSignatureSha256 { get; init; }

    /// <summary>A PlayerProfile.ini shipped in the copy that also receives the CD key.</summary>
    public string? CopyProfileIni { get; init; }

    /// <summary>KEYID holds the RC4-encoded CD key (Vegas 2), not the plain license.</summary>
    public bool EncodedKeyId { get; init; }

    public required string RunningMessage { get; init; }

    public static TitleLaunchProfile For(GameTitle title) => title.Key switch
    {
        "vegas1" => new TitleLaunchProfile
        {
            NativeGameHooks = true,
            PinnedFiles = new Dictionary<string, string>
            {
                ["Binaries/PhysXLoader.dll"] = "313e6065f14a033147ee08d6ad3992d7511bca4bb43568d881101a0e0734cc16",
                ["Binaries/PhysXCore.dll"] = "9467d747076f0312682f572e654acdfc7dc65a72cf8d7be75bc97a064116621d",
            },
            RewriteMenuUrls = true,
            StatisticsCarrier = true,
            RunningMessage = "Game running. Choose Multiplayer > Online and click Login. Sign-in is automatic.",
        },
        "vegas2" => new TitleLaunchProfile
        {
            NativeGameHooks = false,
            // Steam build observed on the development machine, 2026-11-23.
            OriginalSignatureSha256 = "bf549ddfc90f626727c9f06405981e8c78170eb47484ba5f20125325c25f7081",
            // The client reads the redirected profile copy (verified 2026-11-23); the shipped
            // copy is also written so a profile reset still finds the key.
            CopyProfileIni = "KellerGame/Config/PC/PlayerProfile.ini",
            EncodedKeyId = true,
            RunningMessage = "Game running. Choose Login from the main menu and click Login. Sign-in is automatic.",
        },
        _ => throw new ArgumentException($"No launch support for '{title.Key}'", nameof(title)),
    };
}
