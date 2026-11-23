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
    string? NameChangeAllowed = null)
{
    /// <summary>The name shown in game: the claimed name when present.</summary>
    public string PlayerName => ClaimedName ?? DisplayName;

    /// <summary>True when the player must choose a name before playing or hosting.</summary>
    public bool NeedsNameClaim => NameClaimsEnabled && ClaimRequired;

    public bool RenameOnCooldown => IsRenameOnCooldown(DateTimeOffset.UtcNow);

    public bool IsRenameOnCooldown(DateTimeOffset now)
    {
        if (ClaimRequired || string.IsNullOrEmpty(NameChangeAllowed))
        {
            return false;
        }
        return AccountRules.TryParseTimestamp(NameChangeAllowed, out var allowed) && allowed > now;
    }

    public override string ToString() =>
        $"AccountSession {{ DiscordId = {DiscordId}, DisplayName = {DisplayName}, ClaimedName = {ClaimedName}, " +
        $"NameClaimsEnabled = {NameClaimsEnabled}, ClaimRequired = {ClaimRequired}, NameChangeAllowed = {NameChangeAllowed} }}";
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

    public static string Ready(AccountSession session) => session.NeedsNameClaim ? ChooseName : ReadyToPlay;

    public static string SignedInAs(AccountSession session) => "Signed in as " + session.PlayerName;
}

internal static partial class AccountRules
{
    [GeneratedRegex(@"^[0-9A-Fa-f]{64}\z")]
    public static partial Regex Token();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{2,15}\z")]
    public static partial Regex PlayerName();

    [GeneratedRegex(@"^[0-9]{1,20}\z")]
    public static partial Regex DecimalId();

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
