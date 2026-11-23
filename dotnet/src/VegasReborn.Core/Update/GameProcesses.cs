using System.Diagnostics;

namespace VegasReborn.Core.Update;

/// <summary>Process handle operations, replaceable by tests.</summary>
internal interface IProcessApi
{
    /// <summary>Opens a process; returns 0 and the Win32 error on failure.</summary>
    nint Open(uint access, int processId, out int error);
    uint Wait(nint handle, uint milliseconds);
    string? QueryImage(nint handle);
    bool Terminate(nint handle, uint exitCode);
    void Close(nint handle);
}

internal sealed class Win32ProcessApi : IProcessApi
{
    public static Win32ProcessApi Instance { get; } = new();

    public nint Open(uint access, int processId, out int error)
    {
        var handle = NativeMethods.OpenProcess(access, false, processId);
        error = handle == 0 ? System.Runtime.InteropServices.Marshal.GetLastPInvokeError() : 0;
        return handle;
    }

    public uint Wait(nint handle, uint milliseconds) => NativeMethods.WaitForSingleObject(handle, milliseconds);
    public string? QueryImage(nint handle) => NativeMethods.ProcessImage(handle);
    public bool Terminate(nint handle, uint exitCode) => NativeMethods.TerminateProcess(handle, exitCode);
    public void Close(nint handle) => NativeMethods.CloseHandle(handle);
}

/// <summary>Original Vegas and Vegas 2 game/server processes, which block installation.</summary>
public static class GameProcesses
{
    /// <summary>Image names (case-insensitive) that block an update.</summary>
    public static IReadOnlySet<string> BlockingImageNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "R6Vegas_Game.exe", "R6VegasServer.exe", "R6Vegas2_Game.exe", "RainbowSixVegas2_SADS.exe",
    };

    /// <summary>Process ids of running blocking processes.</summary>
    /// <exception cref="UpdateException">The process list could not be read.</exception>
    public static IReadOnlyList<int> FindRunning()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new UpdateException("Could not check running games before updating");
        }
        var result = new List<int>();
        foreach (var process in processes)
        {
            using (process)
            {
                if (BlockingImageNames.Contains(process.ProcessName + ".exe"))
                {
                    result.Add(process.Id);
                }
            }
        }
        return result;
    }

    public static bool AnyRunning() => FindRunning().Count > 0;

    /// <summary>
    /// Terminates running Vegas games and servers. Only for the explicit force-close action;
    /// automatic checks and the normal update button never terminate a game.
    /// </summary>
    /// <exception cref="UpdateException">A game could not be identified or closed.</exception>
    /// <exception cref="GamesBlockingUpdateException">A game is still running afterwards.</exception>
    public static void CloseRunning(IProgress<UpdateProgress>? progress = null) =>
        CloseRunning(progress, Win32ProcessApi.Instance, FindRunning, AnyRunning);

    internal static void CloseRunning(IProgress<UpdateProgress>? progress, IProcessApi api, Func<IReadOnlyList<int>> find,
        Func<bool> anyRunning)
    {
        var ids = find();
        if (ids.Count == 0)
        {
            return;
        }
        progress?.Report(new UpdateProgress(UpdateStage.ClosingGames, "Force-closing running Vegas games and servers..."));
        foreach (var id in ids)
        {
            // Query and terminate the same handle, so PID reuse cannot close an unrelated
            // process between enumeration and termination.
            var handle = api.Open(NativeMethods.ProcessTerminate | NativeMethods.ProcessQueryLimitedInformation | NativeMethods.Synchronize,
                id, out var error);
            if (handle == 0)
            {
                if (error == NativeMethods.ErrorInvalidParameter)
                {
                    continue;
                }
                throw new UpdateException("Could not close a Vegas game. Close it manually and retry the update.");
            }
            try
            {
                if (api.Wait(handle, 0) == NativeMethods.WaitObject0)
                {
                    continue;
                }
                var image = api.QueryImage(handle);
                if (image is null)
                {
                    if (api.Wait(handle, 0) == NativeMethods.WaitObject0)
                    {
                        continue;
                    }
                    throw new UpdateException("Could not identify a Vegas game. Close it manually and retry.");
                }
                if (!BlockingImageNames.Contains(Path.GetFileName(image)))
                {
                    continue;
                }
                if (!api.Terminate(handle, 0) && api.Wait(handle, 0) != NativeMethods.WaitObject0)
                {
                    throw new UpdateException("A Vegas game did not close. Close it manually and retry.");
                }
                if (api.Wait(handle, 10000) != NativeMethods.WaitObject0)
                {
                    throw new UpdateException("A Vegas game did not close. Close it manually and retry.");
                }
            }
            finally
            {
                api.Close(handle);
            }
        }
        if (anyRunning())
        {
            throw new GamesBlockingUpdateException("A Vegas game is still running. Close it or retry force-close.");
        }
    }
}

/// <summary>The companion process that handed an update to the staged helper.</summary>
public static class ParentProcess
{
    /// <summary>
    /// Waits up to 90 s for the companion at <paramref name="root"/> (process <paramref name="processId"/>)
    /// to exit. A live process with another image is refused without waiting.
    /// </summary>
    /// <exception cref="UpdateException">The parent is not this installation or did not close.</exception>
    public static void WaitForExit(int processId, string root) => WaitForExit(processId, root, Win32ProcessApi.Instance);

    internal static void WaitForExit(int processId, string root, IProcessApi api)
    {
        if (!OperatingSystem.IsWindows() || processId <= 0)
        {
            throw new UpdateException("The updater requires a Windows companion process");
        }
        var handle = api.Open(NativeMethods.ProcessQueryLimitedInformation | NativeMethods.Synchronize, processId, out var error);
        if (handle == 0)
        {
            if (error == NativeMethods.ErrorInvalidParameter)
            {
                return; // Parent already exited.
            }
            throw new UpdateException("Could not wait for the companion to close");
        }
        try
        {
            // A process object can remain open after exit while its creator still holds a
            // handle; the image query may then fail. It is already safe to replace the exited app.
            if (api.Wait(handle, 0) == NativeMethods.WaitObject0)
            {
                return;
            }
            var image = api.QueryImage(handle);
            if (image is null)
            {
                if (api.Wait(handle, 0) == NativeMethods.WaitObject0)
                {
                    return;
                }
                throw new UpdateException("Could not identify the companion process before updating");
            }
            if (!SafePaths.Same(SafePaths.Resolve(image), Path.Combine(SafePaths.Resolve(root), "VegasReborn.exe")))
            {
                throw new UpdateException("The updater parent is not this companion installation");
            }
            if (api.Wait(handle, 90000) != NativeMethods.WaitObject0)
            {
                throw new UpdateException("The companion did not close. The update was not installed.");
            }
        }
        finally
        {
            api.Close(handle);
        }
    }
}
