using VegasReborn.Core.Account;

namespace VegasReborn.Core.Profiles;

/// <summary>Cloud character sync failure. Local saves are preserved whenever this is raised.</summary>
public class ProfileSyncException : ServiceException
{
    /// <summary>The 0.4.x companion's text for every non-conflict sync failure.</summary>
    public const string GenericMessage = "The operation failed. Your local character was preserved. Retry sign-in or synchronization.";

    public ProfileSyncException(string code, Exception? inner = null)
        : this(code, GenericMessage, inner)
    {
    }

    private protected ProfileSyncException(string code, string message, Exception? inner)
        : base(code, message, inner)
    {
    }
}

/// <summary>
/// The local and cloud characters both changed. The UI asks the player which to keep and
/// then calls <see cref="RetailProfileSync.ResolveConflictAsync"/>; the replaced character
/// is backed up and the higher XP is kept.
/// </summary>
public sealed class SyncConflictException : ProfileSyncException
{
    /// <summary>The 0.4.x companion's status text when a choice is needed.</summary>
    public const string ConflictMessage = "Your cloud and local character need a choice. Your higher XP is preserved.";

    public SyncConflictException(string code)
        : base(code, ConflictMessage, null)
    {
    }
}
