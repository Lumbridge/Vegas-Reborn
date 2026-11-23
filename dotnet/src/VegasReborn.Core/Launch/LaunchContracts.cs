using VegasReborn.Core.Display;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Launch;

/// <summary>The signed-in account as the launcher needs it. No bearer token is held here.</summary>
public sealed record LaunchAccount(
    string DiscordId,
    string? ClaimedName = null,
    bool NameClaimsEnabled = false,
    bool ClaimRequired = false);

/// <summary>A one-use native game credential. The password is never logged or saved.</summary>
public sealed record NativeCredential(string Username, string Password)
{
    public override string ToString() => $"NativeCredential {{ Username = {Username} }}";
}

/// <summary>What a native credential is minted for.</summary>
public sealed record NativeCredentialRequest(GameTitle Title, string StatisticsCodec = "", bool Dedicated = false);

/// <summary>
/// Owner-authenticated account operations for the current session. Implemented over the
/// account service client (Core/Account); the launcher never sees the Discord bearer.
/// Failures should surface as exceptions with user-safe messages.
/// </summary>
public interface ICredentialSource
{
    /// <summary>Rehydrate the account (claimed name and claim state) from its owner-authenticated source.</summary>
    Task<LaunchAccount> RefreshAccountAsync(CancellationToken cancellationToken);

    /// <summary>Whether the account service issues native credentials for this title.</summary>
    bool SupportsTitle(GameTitle title);

    Task<NativeCredential> MintNativeAsync(NativeCredentialRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Account-isolated save folders and cloud character sync (Core/Profiles) for the current session.
/// </summary>
public interface IProfileSync
{
    /// <summary>
    /// The account's native output folder for a title (route, status and the redirected
    /// Documents folder under <c>profile</c>). See <see cref="LaunchLayout.DefaultAccountDirectory"/>.
    /// </summary>
    string AccountDirectory(GameTitle title);

    /// <summary>Pull the cloud character into the account folder before play. May throw a sync conflict.</summary>
    Task PullAsync(GameTitle title, CancellationToken cancellationToken);

    /// <summary>Push the character after play. Returns true when a character was saved.</summary>
    Task<bool> PushAsync(GameTitle title, CancellationToken cancellationToken);
}

/// <summary>Per-account folder layout shared with the 0.4.x Python companion.</summary>
public static class LaunchLayout
{
    /// <summary>
    /// Vegas 1 keeps <c>local/retail-accounts/&lt;discord id&gt;</c>; other titles use
    /// <c>local/&lt;title&gt;/accounts/&lt;discord id&gt;</c> so their native files never mix.
    /// </summary>
    public static string DefaultAccountDirectory(AppPaths paths, GameTitle title, string discordId) =>
        title.Key == TitleCatalog.Vegas1.Key
            ? Path.Combine(paths.RetailAccounts, discordId)
            : Path.Combine(paths.Local, title.Key, "accounts", discordId);

    /// <summary>The title's save folder inside the account's redirected Documents folder.</summary>
    public static string ProfileDirectory(string accountDirectory, GameTitle title) =>
        Path.Combine([accountDirectory, "profile", .. title.ProfileSubdirectory.Split('/')]);
}

/// <summary>Everything one launch needs from the UI and preferences.</summary>
public sealed record LaunchRequest
{
    public required GameTitle Title { get; init; }
    /// <summary>The installed game folder. It is only read.</summary>
    public required string GameFolder { get; init; }
    public DisplayMode DisplayMode { get; init; } = DisplayMode.GameDefault;
    public string MonitorDevice { get; init; } = "";
    public bool SkipIntros { get; init; }
    /// <summary>Toggle fix ids the player turned off (see Settings.Preferences.DisabledFixes).</summary>
    public IReadOnlySet<string> DisabledFixes { get; init; } = new HashSet<string>();
    /// <summary>Called once the game is running and verified, before waiting for it to exit.</summary>
    public Action? OnStarted { get; init; }
}

/// <summary>The result of a completed play session.</summary>
public sealed record LaunchOutcome(bool CharacterSaved);
