using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VegasReborn.Core.Presence;

internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekNamedPipe(SafePipeHandle pipe, IntPtr buffer, uint size, IntPtr read, out uint available, IntPtr left);
}
