using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Launch;

/// <summary>A started game process the launcher may wait on.</summary>
internal interface IGameProcess : IDisposable
{
    /// <summary>True when the process exited within the timeout.</summary>
    bool WaitForExit(int milliseconds);
}

/// <summary>Operating-system effects of a launch, replaceable by fakes in tests.</summary>
internal interface ILaunchSystem
{
    /// <summary>Fails closed (throws) when process enumeration is unavailable.</summary>
    bool IsGameRunning(GameTitle title);

    /// <summary>The cross-install launch mutex, or null when another companion holds it.</summary>
    IDisposable? TryAcquireLaunchMutex();

    int? FindSteamProcessId();

    string SteamExecutable();

    void StartSteam(string steamExecutable, IReadOnlyList<string> arguments);

    Task<IPAddress> ResolveIPv4Async(string host, CancellationToken cancellationToken);

    Task<JsonObject> RunComponentAsync(IReadOnlyList<string> arguments);

    IGameProcess? OpenGameProcess(int processId);

    void TerminateProcess(int processId);

    string LaunchCacheDirectory { get; }

    /// <summary>Monotonic milliseconds.</summary>
    long Now { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class WindowsLaunchSystem : ILaunchSystem
{
    public const string MutexName = "Local\\VegasRetailCompanion";
    private const string DefaultSteam = "C:/Program Files (x86)/Steam/steam.exe";

    public bool IsGameRunning(GameTitle title)
    {
        try
        {
            var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(title.ClientExecutable));
            foreach (var process in processes)
            {
                process.Dispose();
            }
            return processes.Length > 0;
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            throw new LaunchException("Could not check whether the game is already running. Close the game and retry.");
        }
    }

    public IDisposable? TryAcquireLaunchMutex()
    {
        var mutex = NativeMethods.CreateMutexW(IntPtr.Zero, true, MutexName);
        var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        if (mutex.IsInvalid || error == NativeMethods.ErrorAlreadyExists)
        {
            mutex.Dispose();
            return null;
        }
        return mutex;
    }

    public int? FindSteamProcessId()
    {
        var processes = Process.GetProcessesByName("steam");
        try
        {
            return processes.Length == 0 ? null : processes.OrderBy(process => process.StartTimeOrMax()).First().Id;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public string SteamExecutable()
    {
        // Steam records its own location; fall back to the default install path.
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamExe", null) is string recorded
            && File.Exists(recorded))
        {
            return Path.GetFullPath(recorded);
        }
        return DefaultSteam;
    }

    public void StartSteam(string steamExecutable, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(steamExecutable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        try
        {
            Process.Start(info)?.Dispose();
        }
        catch (Win32Exception)
        {
            throw new LaunchException("Steam could not be started. Check that Steam is installed, then retry.");
        }
    }

    public async Task<IPAddress> ResolveIPv4Async(string host, CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellationToken).ConfigureAwait(false);
            return addresses.FirstOrDefault() ?? throw new SocketException((int)SocketError.HostNotFound);
        }
        catch (SocketException)
        {
            throw new LaunchException("Could not reach the Vegas Reborn service. Check your internet connection, then retry.");
        }
    }

    public Task<JsonObject> RunComponentAsync(IReadOnlyList<string> arguments) => NativeComponents.RunAsync(arguments);

    public IGameProcess? OpenGameProcess(int processId)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.Synchronize, false, processId);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        return new WaitableProcess(handle);
    }

    public void TerminateProcess(int processId)
    {
        using var handle = NativeMethods.OpenProcess(NativeMethods.ProcessTerminate, false, processId);
        if (!handle.IsInvalid)
        {
            NativeMethods.TerminateProcess(handle, 1);
        }
    }

    public string LaunchCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VegasReborn", "LaunchCache");

    public long Now => Environment.TickCount64;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);

    private sealed class WaitableProcess(SafeProcessHandle handle) : IGameProcess
    {
        public bool WaitForExit(int milliseconds) =>
            NativeMethods.WaitForSingleObject(handle, (uint)milliseconds) != NativeMethods.WaitTimeout;

        public void Dispose() => handle.Dispose();
    }
}

internal static class ProcessExtensions
{
    public static DateTime StartTimeOrMax(this Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            return DateTime.MaxValue;
        }
    }
}
