using System.Runtime.InteropServices;
using VegasReborn.Core.Display;

namespace VegasReborn.Core.Tests.Display;

/// <summary>Presentation checks use synthetic offscreen windows, never the retail game.</summary>
public sealed class DesktopTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    private static (int, int, int, int) Rect(IntPtr hwnd, bool client = false)
    {
        NativeMethods.Rect rect;
        Assert.True(client ? NativeMethods.GetClientRect(hwnd, out rect) : NativeMethods.GetWindowRect(hwnd, out rect));
        return (rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private static T WithWindow<T>(uint exStyle, uint style, int width, int height, Func<IntPtr, T> test) =>
        Desktop.InPhysicalPixels(() =>
        {
            // Visible style but far offscreen: no game, input or screen takeover.
            var hwnd = CreateWindowExW(exStyle, "STATIC", "Vegas presentation test", style, -30000, -30000, width, height,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Assert.NotEqual(IntPtr.Zero, hwnd);
            try
            {
                return test(hwnd);
            }
            finally
            {
                DestroyWindow(hwnd);
            }
        });

    [Fact]
    public void BorderlessOnlyTouchesOwnedWindowAndUsesExactBounds()
    {
        var desktop = new Desktop();
        var pid = Environment.ProcessId;
        WithWindow(0x200, 0x10CF0000, 320, 200, hwnd =>
        {
            var monitor = new DisplayMonitor("test", new MonitorBounds(-30000, -30000, -29360, -29520), false);
            var before = NativeMethods.GetStyle(hwnd, NativeMethods.GwlStyle);
            Assert.False(desktop.FitBorderless(0, monitor));
            Assert.Equal(before, NativeMethods.GetStyle(hwnd, NativeMethods.GwlStyle));
            Assert.True(desktop.FitBorderless(pid, monitor));
            Assert.Equal(0u, NativeMethods.GetStyle(hwnd, NativeMethods.GwlStyle) & 0x00CF0000);
            Assert.NotEqual(0u, NativeMethods.GetStyle(hwnd, NativeMethods.GwlStyle) & 0x80000000);
            Assert.Equal((-30000, -30000, -29360, -29520), Rect(hwnd));
            Assert.True(desktop.FitBorderless(pid, monitor));
            // The game may set topmost after geometry is already correct; this must
            // bypass the early return and demote through SetWindowPos.
            Assert.True(SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x13));
            Assert.NotEqual(0u, NativeMethods.GetStyle(hwnd, NativeMethods.GwlExStyle) & 8);
            var focus = GetForegroundWindow();
            Assert.True(desktop.FitBorderless(pid, monitor));
            Assert.Equal(0u, NativeMethods.GetStyle(hwnd, NativeMethods.GwlExStyle) & 8);
            for (var round = 0; round < 3; round++)
            {
                Assert.True(SetWindowPos(hwnd, IntPtr.Zero, -30001, -30000, 640, 480, 0x14));
                Assert.True(desktop.FitBorderless(pid, monitor));
                Assert.Equal((-30000, -30000, -29360, -29520), Rect(hwnd));
                Assert.Equal(focus, GetForegroundWindow());
            }
            Assert.NotEmpty(desktop.Monitors());
            return 0;
        });
    }

    [Fact]
    public void WindowedRestoresFrameAndPreservesUserMoves()
    {
        var desktop = new Desktop();
        var pid = Environment.ProcessId;
        WithWindow(0x08000080, 0x90000000, 800, 600, hwnd =>
        {
            var before = Rect(hwnd);
            Assert.False(desktop.FitWindowed(0));
            Assert.Equal(before, Rect(hwnd));
            Assert.True(SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x13));
            var focus = GetForegroundWindow();
            Assert.True(desktop.FitWindowed(pid));
            Assert.Equal(0x00CF0000u, NativeMethods.GetStyle(hwnd, NativeMethods.GwlStyle) & 0x80CF0000);
            Assert.Equal(0u, NativeMethods.GetStyle(hwnd, NativeMethods.GwlExStyle) & 0x08000088);
            Assert.Equal((0, 0, 800, 600), Rect(hwnd, client: true));
            // Maintenance must not undo movement, resize or change focus.
            Assert.True(SetWindowPos(hwnd, IntPtr.Zero, -29000, -29000, 700, 500, 0x14));
            var moved = Rect(hwnd);
            var client = Rect(hwnd, client: true);
            for (var round = 0; round < 3; round++)
            {
                Assert.True(desktop.FitWindowed(pid));
                Assert.Equal(moved, Rect(hwnd));
                Assert.Equal(client, Rect(hwnd, client: true));
                Assert.Equal(focus, GetForegroundWindow());
            }
            return 0;
        });
    }
}
