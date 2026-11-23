using System.Diagnostics;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Profiles;

/// <summary>
/// Checks for a running original game client (R6Vegas_Game.exe, R6Vegas2_Game.exe).
/// Fails closed: when processes cannot be enumerated this throws
/// <see cref="ProfileSyncException"/> (<c>cannot_verify_game_stopped</c>). Never launches anything.
/// </summary>
public static class RetailProcesses
{
    /// <summary>Client image name, for example <c>R6Vegas2_Game.exe</c>.</summary>
    public static string ImageName(GameTitle title) => Path.GetFileName(title.ClientExecutable);

    public static bool IsRunning(GameTitle title)
    {
        ArgumentNullException.ThrowIfNull(title);
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(title.ClientExecutable));
        }
        catch (Exception error)
        {
            throw new ProfileSyncException("cannot_verify_game_stopped", error);
        }
        foreach (var process in processes)
        {
            process.Dispose();
        }
        return processes.Length != 0;
    }

    /// <summary>True when any supported title's client is running.</summary>
    public static bool IsAnyRunning() => TitleCatalog.All.Any(IsRunning);
}
