using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using VegasReborn.Core.Launch;

namespace VegasReborn.Core.Dedicated;

/// <summary>A started original server process owned by the host.</summary>
public interface IServerProcess : IDisposable
{
    int Id { get; }

    int ThreadId { get; }

    /// <summary>True when the process exited within the timeout.</summary>
    bool WaitForExit(int milliseconds);

    int? ExitCode { get; }

    /// <summary>Asks the server's own windows to close, then terminates it. Returns true when terminated.</summary>
    bool Stop();
}

/// <summary>Operating-system effects of dedicated hosting, replaceable by fakes in tests.</summary>
public interface IDedicatedSystem
{
    /// <summary>The per-copy host lock, or null when another host is using this copy.</summary>
    IDisposable? TryAcquireHostLock(string ownedRoot);

    /// <summary>Starts the executable suspended with its windows hidden.</summary>
    IServerProcess StartSuspended(string executable, IReadOnlyList<string> arguments, string workingDirectory);

    Task<JsonObject> RunComponentAsync(IReadOnlyList<string> arguments);

    Task<IPAddress> ResolveIPv4Async(string host, CancellationToken cancellationToken);

    /// <summary>Monotonic milliseconds.</summary>
    long Now { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class WindowsDedicatedSystem : IDedicatedSystem
{
    private readonly WindowsLaunchSystem _launch = new();

    public IDisposable? TryAcquireHostLock(string ownedRoot)
    {
        var suffix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(ownedRoot).ToLowerInvariant())))[..24];
        var mutex = DedicatedNativeMethods.CreateMutexW(IntPtr.Zero, true, "Local\\VegasOriginalDedicated_" + suffix);
        var error = Marshal.GetLastPInvokeError();
        if (mutex.IsInvalid || error == DedicatedNativeMethods.ErrorAlreadyExists)
        {
            mutex.Dispose();
            return null;
        }
        return mutex;
    }

    public IServerProcess StartSuspended(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var startup = new DedicatedNativeMethods.StartupInfo
        {
            Size = Marshal.SizeOf<DedicatedNativeMethods.StartupInfo>(),
            Desktop = "winsta0\\Default",
            Flags = DedicatedNativeMethods.StartfUseShowWindow,
            ShowWindow = 0,
        };
        var command = new StringBuilder(CommandLine.Join([executable, .. arguments]));
        if (!DedicatedNativeMethods.CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, false,
                DedicatedNativeMethods.CreateSuspended | DedicatedNativeMethods.CreateNoWindow, IntPtr.Zero,
                workingDirectory, ref startup, out var information))
        {
            throw new DedicatedException($"The original server could not be started (Windows error {Marshal.GetLastPInvokeError()})");
        }
        var process = new WindowsServerProcess(information);
        try
        {
            process.KillWithHost();
        }
        catch
        {
            DedicatedNativeMethods.TerminateProcess(new SafeProcessHandle(information.Process, ownsHandle: false), 1);
            process.Dispose();
            throw;
        }
        return process;
    }

    public Task<JsonObject> RunComponentAsync(IReadOnlyList<string> arguments) => NativeComponents.RunAsync(arguments);

    public Task<IPAddress> ResolveIPv4Async(string host, CancellationToken cancellationToken) => _launch.ResolveIPv4Async(host, cancellationToken);

    public long Now => Environment.TickCount64;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);

    private sealed class WindowsServerProcess : IServerProcess
    {
        private readonly SafeProcessHandle _process;
        private readonly SafeWaitHandle _thread;
        private SafeWaitHandle? _job;

        public WindowsServerProcess(DedicatedNativeMethods.ProcessInformation information)
        {
            _process = new SafeProcessHandle(information.Process, ownsHandle: true);
            _thread = new SafeWaitHandle(information.Thread, ownsHandle: true);
            Id = information.ProcessId;
            ThreadId = information.ThreadId;
        }

        public int Id { get; }

        public int ThreadId { get; }

        /// <summary>
        /// Places the suspended server in a job that ends it when this host exits, so a
        /// closed console or killed host cannot leave the server running unsupervised.
        /// </summary>
        public void KillWithHost()
        {
            var job = DedicatedNativeMethods.CreateJobObjectW(IntPtr.Zero, null);
            if (job.IsInvalid)
            {
                throw new DedicatedException($"The server's process job could not be created (Windows error {Marshal.GetLastPInvokeError()})");
            }
            var limits = new DedicatedNativeMethods.ExtendedLimitInformation
            {
                BasicLimitInformation = new DedicatedNativeMethods.BasicLimitInformation { LimitFlags = DedicatedNativeMethods.JobObjectLimitKillOnJobClose },
            };
            if (!DedicatedNativeMethods.SetInformationJobObject(job, DedicatedNativeMethods.JobObjectExtendedLimitInformationClass,
                    ref limits, Marshal.SizeOf<DedicatedNativeMethods.ExtendedLimitInformation>())
                || !DedicatedNativeMethods.AssignProcessToJobObject(job, _process))
            {
                var error = Marshal.GetLastPInvokeError();
                job.Dispose();
                throw new DedicatedException($"The server's process job could not be set up (Windows error {error})");
            }
            _job = job;
        }

        public bool WaitForExit(int milliseconds) =>
            DedicatedNativeMethods.WaitForSingleObject(_process, (uint)milliseconds) != DedicatedNativeMethods.WaitTimeout;

        public int? ExitCode => DedicatedNativeMethods.GetExitCodeProcess(_process, out var code) && code != DedicatedNativeMethods.StillActive
            ? unchecked((int)code)
            : null;

        public bool Stop()
        {
            if (WaitForExit(0))
            {
                return false;
            }
            var id = (uint)Id;
            DedicatedNativeMethods.EnumWindows((window, _) =>
            {
                DedicatedNativeMethods.GetWindowThreadProcessId(window, out var owner);
                if (owner == id)
                {
                    // WM_CLOSE to the server's own windows only.
                    DedicatedNativeMethods.PostMessageW(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
                }
                return true;
            }, IntPtr.Zero);
            if (WaitForExit(5000))
            {
                return false;
            }
            DedicatedNativeMethods.TerminateProcess(_process, 0);
            if (!WaitForExit(10000))
            {
                throw new DedicatedException("The original server could not be stopped");
            }
            return true;
        }

        public void Dispose()
        {
            _thread.Dispose();
            _process.Dispose();
            _job?.Dispose();
        }
    }
}

/// <summary>Windows command-line quoting, as CommandLineToArgvW parses it.</summary>
public static class CommandLine
{
    public static string Join(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(Quote));

    public static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return argument;
        }
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            result.Append(character);
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}

internal static class DedicatedNativeMethods
{
    public const int ErrorAlreadyExists = 183;
    public const uint WaitTimeout = 258;
    public const uint StillActive = 259;
    public const uint CreateSuspended = 0x00000004;
    public const uint CreateNoWindow = 0x08000000;
    public const int StartfUseShowWindow = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Count;
        public IntPtr Reserved2;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    public const uint JobObjectLimitKillOnJobClose = 0x00002000;
    public const int JobObjectExtendedLimitInformationClass = 9;

    [StructLayout(LayoutKind.Sequential)]
    public struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeWaitHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetInformationJobObject(SafeWaitHandle job, int informationClass, ref ExtendedLimitInformation information, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AssignProcessToJobObject(SafeWaitHandle job, SafeProcessHandle process);

    public delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags,
        IntPtr environment, string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeWaitHandle CreateMutexW(IntPtr attributes, [MarshalAs(UnmanagedType.Bool)] bool initialOwner, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TerminateProcess(SafeProcessHandle handle, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetExitCodeProcess(SafeProcessHandle handle, out uint exitCode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
