namespace VegasReborn.Core.Account;

/// <summary>
/// A failure from the account or cloud-character service. <see cref="Code"/> is a fixed
/// code (shared with the Python companion) and <see cref="Exception.Message"/> is plain
/// text that is safe to show. Neither ever contains credentials or response bodies.
/// </summary>
public abstract class ServiceException : Exception
{
    protected ServiceException(string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    public string Code { get; }

    public override string ToString() => $"{GetType().Name}: {Code}";
}

/// <summary>Sign-in, account refresh, name claim and native lease failures.</summary>
public sealed class AccountException : ServiceException
{
    public const string GenericMessage = "The account operation failed. Retry or sign in again.";

    public AccountException(string code, Exception? inner = null)
        : base(code, MessageFor(code), inner)
    {
    }

    /// <summary>User-facing text for a code, as shown by the 0.4.x companion.</summary>
    public static string MessageFor(string code) => code switch
    {
        "invalid_player_name" => "Use 3–16 letters, digits, _ - or .; start with a letter or digit.",
        "player_name_taken" => "That name is taken or held for its previous owner. Choose another name.",
        "player_name_change_too_soon" => "Names can change every 30 days. Capitalization and separator changes are available sooner.",
        "account_session_expired_or_revoked" => "Your sign-in expired. Sign in with Discord again.",
        "guest_unavailable" => "Guest play is not available on the server yet. Sign in with Discord to play.",
        "guest_capacity" => "Too many guests are playing right now. Try again later or sign in with Discord.",
        "guest_sign_in_failed" => "Guest play could not start. Retry or sign in with Discord.",
        "guest_no_cloud" => "Guest characters are saved on this PC only. Sign in with Discord to save to your account.",
        "guest_dedicated" => "Dedicated hosting needs a Discord sign-in.",
        _ => GenericMessage,
    };
}
