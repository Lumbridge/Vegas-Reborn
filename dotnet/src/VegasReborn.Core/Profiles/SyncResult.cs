namespace VegasReborn.Core.Profiles;

public enum SyncOperation
{
    Pull,
    Push,
}

/// <summary>Which character the player keeps after a <see cref="SyncConflictException"/>.</summary>
public enum ConflictChoice
{
    /// <summary>Replace the local character with the cloud one (local is backed up).</summary>
    KeepCloud,
    /// <summary>Replace the cloud character with the local one (cloud is backed up).</summary>
    KeepLocal,
}

/// <param name="Operation">Pull or push.</param>
/// <param name="Revision">Cloud profile revision after the operation.</param>
/// <param name="Experience">Shared raw XP after the operation (the highest seen).</param>
/// <param name="FilesChanged">Local save file names that were replaced, sorted.</param>
/// <param name="LocalChangedDuringUpload">A local save changed while uploading; it was left untouched.</param>
/// <param name="HasSaveSlots">False when the title has no synchronised saves (nothing was done).</param>
public sealed record SyncResult(
    SyncOperation Operation,
    long Revision,
    uint Experience,
    IReadOnlyList<string> FilesChanged,
    bool LocalChangedDuringUpload = false,
    bool HasSaveSlots = true)
{
    public static SyncResult NothingToSync(SyncOperation operation) => new(operation, 0, 0, [], false, false);
}

/// <summary>Plain status text the companion shows around character sync.</summary>
public static class SyncMessages
{
    public const string UpToDate = "Cloud character and shared progression are up to date.";
    public const string ChoiceSaved = "Character choice saved. You can play now.";
    public const string CheckingBeforeLaunch = "Checking the account's cloud character...";
    public const string SavingAfterGame = "Saving the character and progression to your account...";
    public const string SavedAfterGame = "Character saved to your account.";
    public const string NoCharacterCreated = "Game closed. No character was created.";
    /// <summary>For titles whose save format is not supported yet (Vegas 2).</summary>
    public const string NotAvailable = "Character sync is not available for this game yet.";
}
