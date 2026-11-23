using System.ComponentModel;
using System.Runtime.InteropServices;
using static VegasReborn.Core.Display.NativeMethods;

namespace VegasReborn.Core.Display;

/// <summary>Monitor listing and window presentation for the companion-owned game process only.</summary>
public interface IDesktop
{
    /// <summary>Monitors in physical pixels, primary first, then by device name.</summary>
    IReadOnlyList<DisplayMonitor> Monitors();

    /// <summary>Make the game's window borderless and cover the monitor exactly. False when no window yet.</summary>
    bool FitBorderless(int processId, DisplayMonitor monitor);

    /// <summary>Restore the framed style without pinning the user's window position or size.</summary>
    bool FitWindowed(int processId);
}

/// <summary>Win32 implementation. Failures throw <see cref="Win32Exception"/>.</summary>
public sealed class Desktop : IDesktop
{
    private const uint WsPopup = 0x80000000;
    private const uint WsFrame = 0x00CF0000; // WS_OVERLAPPEDWINDOW bits used by FWindowsViewport::Resize.
    private const uint WsExTopmost = 0x00000008;
    private const uint ChromeExtended = 0x00020301;
    private const uint FullscreenExtended = 0x08000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
    private const uint KeepZOrder = 0x34; // SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED
    private const uint DemoteTopmost = 0x30; // SWP_NOACTIVATE | SWP_FRAMECHANGED

    /// <summary>The monitors of this desktop (empty if enumeration is unavailable).</summary>
    public static IReadOnlyList<DisplayMonitor> AvailableMonitors()
    {
        try
        {
            return new Desktop().Monitors();
        }
        catch (Win32Exception)
        {
            return [];
        }
    }

    private readonly struct PhysicalPixels : IDisposable
    {
        private readonly IntPtr _previous;

        public PhysicalPixels() => _previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);

        public void Dispose()
        {
            if (_previous != IntPtr.Zero)
            {
                SetThreadDpiAwarenessContext(_previous);
            }
        }
    }

    /// <summary>Runs <paramref name="action"/> with per-monitor physical pixel coordinates.</summary>
    internal static T InPhysicalPixels<T>(Func<T> action)
    {
        using var scope = new PhysicalPixels();
        return action();
    }

    public IReadOnlyList<DisplayMonitor> Monitors()
    {
        var result = new List<DisplayMonitor>();
        MonitorEnumProc visit = (handle, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfoW(handle, ref info))
            {
                var r = info.Monitor;
                result.Add(new DisplayMonitor(info.Device, new MonitorBounds(r.Left, r.Top, r.Right, r.Bottom), (info.Flags & 1) != 0));
            }
            return true;
        };
        using (new PhysicalPixels())
        {
            if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, visit, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        GC.KeepAlive(visit);
        return result.OrderBy(item => !item.Primary).ThenBy(item => item.Device, StringComparer.Ordinal).ToList();
    }

    internal static IntPtr GameWindow(int processId)
    {
        var bestArea = 0L;
        var best = IntPtr.Zero;
        var found = false;
        EnumWindowsProc visit = (hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == (uint)processId && IsWindowVisible(hwnd) && GetWindow(hwnd, GwOwner) == IntPtr.Zero)
            {
                GetClientRect(hwnd, out var rect);
                var area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                if (!found || area > bestArea || (area == bestArea && (long)hwnd > (long)best))
                {
                    found = true;
                    bestArea = area;
                    best = hwnd;
                }
            }
            return true;
        };
        EnumWindows(visit, IntPtr.Zero);
        GC.KeepAlive(visit);
        return best;
    }

    private static void ApplyStyles(IntPtr hwnd, uint style, uint newStyle, uint extended, uint newExtended)
    {
        foreach (var (index, old, value) in new[] { (GwlStyle, style, newStyle), (GwlExStyle, extended, newExtended) })
        {
            if (old != value && !SetStyle(hwnd, index, value, out var error))
            {
                throw new Win32Exception(error);
            }
        }
    }

    public bool FitBorderless(int processId, DisplayMonitor monitor)
    {
        var hwnd = GameWindow(processId);
        if (hwnd == IntPtr.Zero || IsIconic(hwnd))
        {
            return false;
        }
        using var scope = new PhysicalPixels();
        uint style = GetStyle(hwnd, GwlStyle), extended = GetStyle(hwnd, GwlExStyle);
        var topmost = (extended & WsExTopmost) != 0;
        var clean = (style & ~WsFrame) | WsPopup;
        var cleanExtended = extended & ~ChromeExtended;
        GetWindowRect(hwnd, out var rect);
        var bounds = new MonitorBounds(rect.Left, rect.Top, rect.Right, rect.Bottom);
        if (!topmost && style == clean && extended == cleanExtended && bounds == monitor.Bounds)
        {
            return true;
        }
        ApplyStyles(hwnd, style, clean, extended, cleanExtended);
        var b = monitor.Bounds;
        // SetWindowPos is required to remove WS_EX_TOPMOST. Demote only when
        // necessary. Ordinary windows keep their existing Z order while the
        // companion maintains size and borderless styling.
        var (after, flags) = topmost ? (HwndNoTopmost, DemoteTopmost) : (IntPtr.Zero, KeepZOrder);
        if (!SetWindowPos(hwnd, after, b.Left, b.Top, b.Width, b.Height, flags))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return true;
    }

    public bool FitWindowed(int processId)
    {
        var hwnd = GameWindow(processId);
        if (hwnd == IntPtr.Zero || IsIconic(hwnd))
        {
            return false;
        }
        using var scope = new PhysicalPixels();
        uint style = GetStyle(hwnd, GwlStyle), extended = GetStyle(hwnd, GwlExStyle);
        // Same framed bits as retail FWindowsViewport::Resize. Clear popup and
        // tool/no-activate flags left by fullscreen-style presentation.
        var framed = (style & ~WsPopup) | WsFrame;
        var framedExtended = extended & ~FullscreenExtended;
        var topmost = (extended & WsExTopmost) != 0;
        if (style == framed && extended == framedExtended && !topmost)
        {
            return true;
        }
        if (!GetWindowRect(hwnd, out var outer) || !GetClientRect(hwnd, out var client))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        // Add the non-client frame around the current render area. Simply adding
        // WS_CAPTION would shrink/crop that area by the title height.
        var frame = new Rect { Left = 0, Top = 0, Right = client.Right, Bottom = client.Bottom };
        var dpi = GetDpiForWindow(hwnd);
        if (!AdjustWindowRectExForDpi(ref frame, framed, false, framedExtended, dpi == 0 ? 96 : dpi))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        ApplyStyles(hwnd, style, framed, extended, framedExtended);
        var (after, flags) = topmost ? (HwndNoTopmost, DemoteTopmost) : (IntPtr.Zero, KeepZOrder);
        if (!SetWindowPos(hwnd, after, outer.Left, outer.Top, frame.Right - frame.Left, frame.Bottom - frame.Top, flags))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return true;
    }
}
