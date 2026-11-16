"""Desktop window presentation for the companion-owned retail process only."""
from contextlib import contextmanager
import ctypes as c
from ctypes import wintypes as w
from dataclasses import dataclass
import os
from pathlib import Path


MODES = ("Game default", "Borderless", "Windowed", "Fullscreen")
ENGINE_FILES = ("DefaultEngine.ini", "PC/DefaultEngine.ini", "PC/KellerEngine.ini", "PCKellerEngine.ini")


@dataclass(frozen=True)
class Monitor:
    device: str
    bounds: tuple[int, int, int, int]
    primary: bool

    @property
    def label(self):
        x, y, right, bottom = self.bounds
        return f"{self.device} · {right-x} × {bottom-y}" + (" · Primary" if self.primary else "")


class Desktop:
    def __init__(self):
        self.u = u = c.WinDLL("user32", use_last_error=True)
        self.callback = c.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
        self.monitor_callback = c.WINFUNCTYPE(w.BOOL, w.HANDLE, w.HDC, c.POINTER(w.RECT), w.LPARAM)
        signatures = {
            "EnumWindows": ([self.callback, w.LPARAM], w.BOOL),
            "EnumDisplayMonitors": ([w.HDC, c.POINTER(w.RECT), self.monitor_callback, w.LPARAM], w.BOOL),
            "GetMonitorInfoW": ([w.HANDLE, c.c_void_p], w.BOOL),
            "GetWindowThreadProcessId": ([w.HWND, c.POINTER(w.DWORD)], w.DWORD),
            "IsWindowVisible": ([w.HWND], w.BOOL), "IsIconic": ([w.HWND], w.BOOL),
            "GetWindow": ([w.HWND, w.UINT], w.HWND),
            "GetWindowRect": ([w.HWND, c.POINTER(w.RECT)], w.BOOL),
            "GetClientRect": ([w.HWND, c.POINTER(w.RECT)], w.BOOL),
            "SetWindowPos": ([w.HWND, w.HWND, c.c_int, c.c_int, c.c_int, c.c_int, w.UINT], w.BOOL),
            "SetThreadDpiAwarenessContext": ([w.HANDLE], w.HANDLE),
        }
        for name, (args, result) in signatures.items():
            fn = getattr(u, name)
            fn.argtypes, fn.restype = args, result
        suffix = "PtrW" if c.sizeof(c.c_void_p) == 8 else "W"
        self.get_style = getattr(u, "GetWindowLong" + suffix)
        self.set_style = getattr(u, "SetWindowLong" + suffix)
        self.get_style.argtypes, self.get_style.restype = [w.HWND, c.c_int], c.c_ssize_t
        self.set_style.argtypes, self.set_style.restype = [w.HWND, c.c_int, c.c_ssize_t], c.c_ssize_t

    @contextmanager
    def physical_pixels(self):
        previous = self.u.SetThreadDpiAwarenessContext(c.c_void_p(-4))
        try:
            yield
        finally:
            if previous:
                self.u.SetThreadDpiAwarenessContext(previous)

    def monitors(self):
        class Info(c.Structure):
            _fields_ = [("size", w.DWORD), ("monitor", w.RECT), ("work", w.RECT),
                        ("flags", w.DWORD), ("device", w.WCHAR * 32)]
        result = []
        def visit(handle, dc, rect, data):
            info = Info()
            info.size = c.sizeof(info)
            if self.u.GetMonitorInfoW(handle, c.byref(info)):
                r = info.monitor
                result.append(Monitor(info.device, (r.left, r.top, r.right, r.bottom), bool(info.flags & 1)))
            return True
        with self.physical_pixels():
            if not self.u.EnumDisplayMonitors(None, None, self.monitor_callback(visit), 0):
                raise c.WinError(c.get_last_error())
        return sorted(result, key=lambda item: (not item.primary, item.device))

    def game_window(self, pid):
        found = []
        def visit(hwnd, data):
            owner = w.DWORD()
            self.u.GetWindowThreadProcessId(hwnd, c.byref(owner))
            if owner.value == pid and self.u.IsWindowVisible(hwnd) and not self.u.GetWindow(hwnd, 4):
                rect = w.RECT()
                self.u.GetClientRect(hwnd, c.byref(rect))
                found.append(((rect.right-rect.left)*(rect.bottom-rect.top), hwnd))
            return True
        self.u.EnumWindows(self.callback(visit), 0)
        return max(found, default=(0, None))[1]

    def fit_borderless(self, pid, monitor):
        hwnd = self.game_window(pid)
        if not hwnd or self.u.IsIconic(hwnd):
            return False
        with self.physical_pixels():
            style, extended = self.get_style(hwnd, -16), self.get_style(hwnd, -20)
            topmost = bool(extended & 0x00000008)  # WS_EX_TOPMOST
            clean, clean_extended = (style & ~0x00CF0000) | 0x80000000, extended & ~0x00020301
            rect = w.RECT()
            self.u.GetWindowRect(hwnd, c.byref(rect))
            bounds = (rect.left, rect.top, rect.right, rect.bottom)
            if not topmost and (style, extended, bounds) == (clean, clean_extended, monitor.bounds):
                return True
            for index, old, new in ((-16, style, clean), (-20, extended, clean_extended)):
                if old != new:
                    c.set_last_error(0)
                    if not self.set_style(hwnd, index, new) and c.get_last_error():
                        raise c.WinError(c.get_last_error())
            x, y, right, bottom = monitor.bounds
            # SetWindowPos is required to remove WS_EX_TOPMOST. Demote only
            # when necessary. Ordinary windows keep their existing Z order
            # while the companion maintains size and borderless styling.
            after, flags = (-2, 0x30) if topmost else (None, 0x34)
            if not self.u.SetWindowPos(hwnd, after, x, y, right-x, bottom-y, flags):
                raise c.WinError(c.get_last_error())
        return True


def available_monitors():
    return Desktop().monitors() if os.name == "nt" else []


def select_monitor(monitors, device=""):
    return next((m for m in monitors if m.device == device),
                next((m for m in monitors if m.primary), monitors[0] if monitors else None))


def configure_display(game, mode, monitor=None):
    """Use keys present in shipped [WinDrv.WindowsClient]; only copied configs."""
    if mode not in MODES:
        raise ValueError("Unknown display mode")
    # Remember the copied startup settings so choosing Game default undoes our
    # overrides, rather than silently retaining the last forced mode.
    import json
    from .companion import _private_write
    baseline_path = Path(game) / ".companion-display.json"
    baseline = json.loads(baseline_path.read_text()) if baseline_path.exists() else {}
    restoring = mode == "Game default"
    if restoring and not baseline:
        return
    values = {"StartupFullscreen": "True" if mode == "Fullscreen" else "False"}
    if mode == "Borderless" and monitor:
        x, y, right, bottom = monitor.bounds
        values.update(StartupResolutionX=str(right-x), StartupResolutionY=str(bottom-y))
    for relative in ENGINE_FILES:
        target = Path(game) / "KellerGame/Config" / relative
        if not target.exists():
            continue
        section, lines = "", []
        original = baseline.setdefault(relative, {})
        for line in target.read_text(encoding="utf-8-sig").splitlines():
            if line.strip().startswith("["):
                section = line.strip()
            key = line.split("=", 1)[0].strip()
            if section == "[WinDrv.WindowsClient]" and key in ("StartupFullscreen", "StartupResolutionX", "StartupResolutionY"):
                original.setdefault(key, line.split("=", 1)[1])
                if restoring:
                    line = key + "=" + original[key]
                elif key in values:
                    line = key + "=" + values[key]
                elif mode == "Windowed":
                    line = key + "=" + original[key]
            lines.append(line)
        _private_write(target, ("\n".join(lines) + "\n").encode())
    if restoring:
        baseline_path.unlink(missing_ok=True)
    else:
        _private_write(baseline_path, json.dumps(baseline).encode())
