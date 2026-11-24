namespace VegasReborn.Core.Titles;

/// <summary>How a fix is presented and whether the player can turn it off.</summary>
public enum FixKind
{
    /// <summary>Player switch. Off plays as the original game does.</summary>
    Toggle,
    /// <summary>Required on current PCs or by the revived online service.</summary>
    AlwaysOn,
}

public enum FixSection
{
    /// <summary>Optional differences from the original game.</summary>
    GameFixes,
    /// <summary>Where the original would fail or hang on a current PC.</summary>
    KeptFromCrashing,
    /// <summary>What the revived online service needs in the game.</summary>
    OnlineServices,
}

/// <summary>
/// One companion change to the original game. <see cref="Id"/> is the stable key
/// shared with preferences and the native adapter's feature file.
/// </summary>
public sealed record GameFix(
    string Id,
    string Title,
    string Summary,
    string Details,
    FixSection Section,
    FixKind Kind = FixKind.Toggle,
    bool DefaultEnabled = true);

public enum TitleSupport
{
    /// <summary>Sign-in, character sync and online play are supported.</summary>
    Supported,
    /// <summary>Online support is being tested; expect failures.</summary>
    Experimental,
}

public enum TrustKeyKind
{
    /// <summary>Vegas 1: UbiSigKeyPub line in KellerGame/Config/PC/AgoraConfig.lua.</summary>
    AgoraConfigLua,
    /// <summary>Vegas 2: KellerGame/Config/PC/signature.pub (PKCS#1 DER).</summary>
    SignaturePub,
}

/// <summary>
/// Everything the companion needs to know about one original PC game.
/// Paths are relative to the game folder and use forward slashes.
/// </summary>
public sealed record GameTitle
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public required TitleSupport Support { get; init; }
    public required int SteamAppId { get; init; }
    public required string DefaultInstallFolder { get; init; }
    public required string ClientExecutable { get; init; }
    public required string ClientSha256 { get; init; }
    public string? ServerExecutable { get; init; }
    public string? ServerSha256 { get; init; }
    /// <summary>Profile folder under the redirected Documents folder.</summary>
    public required string ProfileSubdirectory { get; init; }
    /// <summary>Native adapter DLL file name in the companion's native folder.</summary>
    public required string NativeAdapter { get; init; }
    /// <summary>Native adapter version reported in compat-status.json.</summary>
    public required int NativeVersion { get; init; }
    public required string LaunchArguments { get; init; }
    public required TrustKeyKind TrustKey { get; init; }
    /// <summary>Host names the native adapter routes to the realm.</summary>
    public required IReadOnlyList<string> RedirectedHosts { get; init; }
    /// <summary>Default realm TCP port for this title's master.</summary>
    public required int RealmPort { get; init; }
    /// <summary>Account profile slot name to save file name in the profile folder.</summary>
    public required IReadOnlyDictionary<string, string> SaveSlots { get; init; }
    public required IReadOnlyList<GameFix> Fixes { get; init; }

    public IEnumerable<GameFix> FixesIn(FixSection section) => Fixes.Where(fix => fix.Section == section);

    public GameFix? Fix(string id) => Fixes.FirstOrDefault(fix => fix.Id == id);
}
