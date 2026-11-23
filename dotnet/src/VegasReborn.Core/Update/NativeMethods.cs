using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VegasReborn.Core.Update;

// Classic blittable P/Invoke: the Core project does not allow unsafe code, which the
// LibraryImport generator would need for these signatures.
#pragma warning disable SYSLIB1054
internal static class NativeMethods
{
    public const uint ProcessTerminate = 0x0001;
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint Synchronize = 0x00100000;
    public const uint WaitObject0 = 0;
    public const uint WaitTimeout = 258;
    public const int ErrorInvalidParameter = 87;

    private const uint FileShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(nint process, uint flags, [Out] char[] buffer, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TerminateProcess(nint process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] buffer, uint length, uint flags);

    /// <summary>The final (link-resolved, long-name) DOS path of an existing file or directory, or null.</summary>
    public static string? FinalPath(string path)
    {
        using var handle = CreateFile(path, 0, FileShareAll, 0, OpenExisting, FileFlagBackupSemantics, 0);
        if (handle.IsInvalid)
        {
            return null;
        }
        var buffer = new char[1024];
        while (true)
        {
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0)
            {
                return null;
            }
            if (length < buffer.Length)
            {
                var value = new string(buffer, 0, (int)length);
                return value.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + value[8..]
                    : value.StartsWith(@"\\?\", StringComparison.Ordinal) ? value[4..]
                    : value;
            }
            buffer = new char[length + 1];
        }
    }

    /// <summary>Full image path of a process handle, or null.</summary>
    public static string? ProcessImage(nint process)
    {
        var buffer = new char[32768];
        var size = (uint)buffer.Length;
        return QueryFullProcessImageName(process, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }
}
#pragma warning restore SYSLIB1054
