using System.Globalization;
using System.Text.RegularExpressions;

namespace VegasReborn.Core.Account;

/// <summary>
/// A signed-in companion account. <see cref="Token"/> is the account bearer and is
/// never included in <see cref="ToString"/>.
/// </summary>
public sealed record AccountSession(
    string DiscordId,
    string DisplayName,
    string Token,
    string? ClaimedName = null,
    bool NameClaimsEnabled = false,
    bool ClaimRequired = false,
    string? NameChangeAllowed = null,
    bool IsGuest = false)
{
    /// <summary>
    /// The player's name as the companion shows it: the claimed name when present. A guest
    /// is shown as Name#tag; the game is given <see cref="ClaimedName"/> (Name.tag).
    /// </summary>
    public string PlayerName => IsGuest ? DisplayName : ClaimedName ?? DisplayName;

    /// <summary>The name a guest typed, without its tag. Empty for Discord accounts.</summary>
    public string GuestBaseName => IsGuest ? DisplayName[..DisplayName.LastIndexOf('#')] : "";

    /// <summary>True when the player must choose a name before playing or hosting.</summary>
    public bool NeedsNameClaim => NameClaimsEnabled && ClaimRequired;

    public bool RenameOnCooldown => IsRenameOnCooldown(DateTimeOffset.UtcNow);

    public bool IsRenameOnCooldown(DateTimeOffset now) => NextNameChange(now) is not null;

    /// <summary>When the player may next change their name, or null if they can change it now.</summary>
    public DateTimeOffset? NextNameChange(DateTimeOffset now)
    {
        if (ClaimRequired || string.IsNullOrEmpty(NameChangeAllowed))
        {
            return null;
        }
        return AccountRules.TryParseTimestamp(NameChangeAllowed, out var allowed) && allowed > now ? allowed : null;
    }

    public override string ToString() =>
        $"AccountSession {{ DiscordId = {DiscordId}, DisplayName = {DisplayName}, ClaimedName = {ClaimedName}, " +
        $"NameClaimsEnabled = {NameClaimsEnabled}, ClaimRequired = {ClaimRequired}, NameChangeAllowed = {NameChangeAllowed}, IsGuest = {IsGuest} }}";
}

/// <summary>
/// A pending browser sign-in. Open <see cref="Url"/>; <see cref="Secret"/> is only sent
/// in poll request bodies and is never included in <see cref="ToString"/>.
/// </summary>
public sealed record AuthLink(string Code, string Url, int ExpiresIn, double PollSeconds, TimeSpan Deadline, string Secret)
{
    public override string ToString() =>
        $"AuthLink {{ Code = {Code}, Url = {Url}, ExpiresIn = {ExpiresIn}, PollSeconds = {PollSeconds} }}";
}

public enum NativeCredentialPurpose
{
    /// <summary>Player login (<c>/retail/v1/session</c>, usernames start with d).</summary>
    Player,
    /// <summary>Dedicated host (<c>/retail/v1/dedicated-session</c>, usernames start with h).</summary>
    Dedicated,
}

/// <summary>Short-lived native game credential. <see cref="Password"/> is never in <see cref="ToString"/>.</summary>
public sealed record NativeLease(string Username, int ExpiresIn, string DiscordId, string DisplayName, string Password)
{
    public override string ToString() =>
        $"NativeLease {{ Username = {Username}, ExpiresIn = {ExpiresIn}, DiscordId = {DiscordId}, DisplayName = {DisplayName} }}";
}

/// <summary>Plain status text used by the companion around account operations.</summary>
public static class AccountMessages
{
    public const string ApproveInBrowser = "Approve the Discord sign-in in your browser.";
    public const string ChooseName = "Choose your player name before playing or hosting.";
    public const string ReadyToPlay = "Ready to play. Steam must be running and signed in.";
    public const string NameSaved = "Player name saved. ";
    public const string GuestSaves = "Guest characters are saved on this PC only.";

    public static string Ready(AccountSession session) => session.NeedsNameClaim ? ChooseName : ReadyToPlay;

    public static string SignedInAs(AccountSession session) =>
        (session.IsGuest ? "Playing as guest " : "Signed in as ") + session.PlayerName;

    /// <summary>Cooldown notice, shown in the viewer's local time and culture.</summary>
    public static string NextNameChange(DateTimeOffset allowed, TimeZoneInfo zone, CultureInfo culture) =>
        "You can change your name again on " +
        TimeZoneInfo.ConvertTime(allowed, zone).ToString("f", culture) + ". Names can change every 30 days.";
}

internal static partial class AccountRules
{
    [GeneratedRegex(@"^[0-9A-Fa-f]{64}\z")]
    public static partial Regex Token();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{2,15}\z")]
    public static partial Regex PlayerName();

    [GeneratedRegex(@"^[0-9]{1,20}\z")]
    public static partial Regex DecimalId();

    /// <summary>The random install id a guest tag is made from (OpenSCCT install_id format).</summary>
    [GeneratedRegex(@"^[0-9a-f]{16}\z")]
    public static partial Regex GuestInstallId();

    /// <summary>A guest's in-game name: up to 11 name characters, '.', then the four hex tag.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9_.\-]{0,9}[A-Za-z0-9])?\.[0-9a-f]{4}\z")]
    public static partial Regex GuestInGameName();

    /// <summary>True when <paramref name="display"/> is Name#tag for the in-game Name.tag.</summary>
    public static bool IsGuestName(string display, string? inGame) =>
        inGame is not null && GuestInGameName().IsMatch(inGame)
        && display == inGame[..^5] + "#" + inGame[^4..];

    [GeneratedRegex(@"(?:[Zz]|[+\-][0-9]{2}(?::?[0-9]{2}(?::?[0-9]{2}(?:\.[0-9]+)?)?)?)\z")]
    private static partial Regex ZoneSuffix();

    public static bool IsToken(string? value) => value is not null && Token().IsMatch(value);

    /// <summary>An ISO 8601 timestamp that carries a time zone, like Python's aware fromisoformat.</summary>
    public static bool TryParseTimestamp(string value, out DateTimeOffset result)
    {
        result = default;
        return ZoneSuffix().IsMatch(value)
            && value.Length >= 10 && value[4] == '-' && value[7] == '-'
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }

    /// <summary>Normalised decimal account id below 2^64 and above zero, or null.</summary>
    public static string? NormaliseId(string? value)
    {
        if (value is null || !DecimalId().IsMatch(value)
            || !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number == 0)
        {
            return null;
        }
        return number.ToString(CultureInfo.InvariantCulture);
    }
}
