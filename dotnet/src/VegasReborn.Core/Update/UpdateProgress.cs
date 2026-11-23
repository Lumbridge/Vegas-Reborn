namespace VegasReborn.Core.Update;

public enum UpdateStage
{
    Checking,
    Downloading,
    Verifying,
    WaitingForCompanion,
    ClosingGames,
    Installing,
    Restarting,
}

/// <summary>
/// One progress report. <see cref="Message"/> is plain user-facing text (the 0.4.x wording);
/// <see cref="Completed"/>/<see cref="Total"/> are bytes while downloading and files while
/// installing, and 0 when the step has no measurable progress.
/// </summary>
public sealed record UpdateProgress(UpdateStage Stage, string Message, long Completed = 0, long Total = 0);
