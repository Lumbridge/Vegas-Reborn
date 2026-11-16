"""Presentation checks use synthetic windows, never the retail game."""
import ctypes as c
from ctypes import wintypes as w
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from vegas_reborn.display import Desktop, Monitor, configure_display, select_monitor
from vegas_reborn.companion_settings import Preferences


class DisplayConfigTests(unittest.TestCase):
    def test_private_config_roundtrip_preserves_other_sections(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            target = root / "KellerGame/Config/PC/KellerEngine.ini"
            target.parent.mkdir(parents=True)
            original = "[Other]\nStartupFullscreen=True\n[WinDrv.WindowsClient]\nStartupFullscreen=True\nStartupResolutionX=800\nStartupResolutionY=600\nUseSound=True\n"
            target.write_text(original)
            monitor = Monitor("test", (-1920, 0, 0, 1080), False)
            configure_display(root, "Borderless", monitor)
            text = target.read_text()
            self.assertIn("[Other]\nStartupFullscreen=True", text)
            self.assertIn("StartupFullscreen=False", text)
            self.assertIn("StartupResolutionX=1920", text)
            self.assertIn("StartupResolutionY=1080", text)
            self.assertIn("UseSound=True", text)
            configure_display(root, "Windowed")
            self.assertIn("StartupResolutionX=800", target.read_text())
            self.assertIn("StartupResolutionY=600", target.read_text())
            configure_display(root, "Fullscreen")
            configure_display(root, "Game default")
            self.assertEqual(target.read_text(), original)
            self.assertFalse((root / ".companion-display.json").exists())

    def test_disconnected_monitor_falls_back_to_primary(self):
        primary = Monitor("primary", (0, 0, 1920, 1080), True)
        secondary = Monitor("other", (-1280, 0, 0, 720), False)
        self.assertIs(select_monitor([secondary, primary], "missing"), primary)
        self.assertIs(select_monitor([primary, secondary], "other"), secondary)
        self.assertIsNone(select_monitor([]))

    def test_preferences_roundtrip_and_corrupt_fallback(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "settings.json"
            prefs = Preferences("D:/Owned Vegas", "Windowed", "other", False)
            prefs.save(path)
            self.assertEqual(Preferences.load(path), prefs)
            for value in ('{"display_mode":"invalid"}', '{"minimize_on_play":"false"}', 'null', '[]', '{'):
                path.write_text(value)
                self.assertEqual(Preferences.load(path), Preferences())


@unittest.skipUnless(os.name == "nt", "Windows desktop API required")
class DesktopTests(unittest.TestCase):
    def test_borderless_only_touches_owned_window_and_uses_exact_bounds(self):
        desktop = Desktop()
        u = desktop.u
        u.CreateWindowExW.argtypes = [w.DWORD, w.LPCWSTR, w.LPCWSTR, w.DWORD,
                                      c.c_int, c.c_int, c.c_int, c.c_int,
                                      w.HWND, w.HMENU, w.HINSTANCE, c.c_void_p]
        u.CreateWindowExW.restype = w.HWND
        u.DestroyWindow.argtypes = [w.HWND]
        with desktop.physical_pixels():
            # Visible style but far offscreen: no game, input, or screen takeover.
            hwnd = u.CreateWindowExW(0x200, "STATIC", "Vegas presentation test", 0x10CF0000,
                                      -30000, -30000, 320, 200, None, None, None, None)
            self.assertTrue(hwnd)
            try:
                before = desktop.get_style(hwnd, -16)
                self.assertFalse(desktop.fit_borderless(0, Monitor("test", (-30000, -30000, -29360, -29520), False)))
                self.assertEqual(desktop.get_style(hwnd, -16), before)
                monitor = Monitor("test", (-30000, -30000, -29360, -29520), False)
                self.assertTrue(desktop.fit_borderless(os.getpid(), monitor))
                self.assertEqual(desktop.get_style(hwnd, -16) & 0x00CF0000, 0)
                self.assertTrue(desktop.get_style(hwnd, -16) & 0x80000000)
                rect = w.RECT()
                u.GetWindowRect(hwnd, c.byref(rect))
                self.assertEqual((rect.left, rect.top, rect.right, rect.bottom), monitor.bounds)
                self.assertTrue(desktop.fit_borderless(os.getpid(), monitor))
                self.assertTrue(desktop.monitors())
                # The game may set topmost after geometry is already correct.
                # This must bypass the geometry/style early return and demote
                # through SetWindowPos, not just edit the extended style bits.
                self.assertTrue(u.SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x13))
                self.assertTrue(desktop.get_style(hwnd, -20) & 8)
                self.assertTrue(desktop.fit_borderless(os.getpid(), monitor))
                self.assertFalse(desktop.get_style(hwnd, -20) & 8)
                overlay = u.CreateWindowExW(0x08000000, "STATIC", "Foreground app stand-in", 0x90000000,
                                           -30000, -30000, 200, 100, None, None, None, None)
                self.assertTrue(overlay)
                try:
                    self.assertTrue(u.SetWindowPos(overlay, None, 0, 0, 0, 0, 0x13))
                    def overlay_above_game():
                        current = u.GetWindow(hwnd, 3)  # GW_HWNDPREV
                        while current:
                            if current == overlay:
                                return True
                            current = u.GetWindow(current, 3)
                        return False
                    self.assertTrue(overlay_above_game())
                    foreground = u.GetForegroundWindow
                    foreground.restype = w.HWND
                    before_focus = foreground()
                    for _ in range(4):
                        # Exercise the resize path as well as the early return.
                        self.assertTrue(u.SetWindowPos(hwnd, None, -30001, -30000, 640, 480, 0x14))
                        self.assertTrue(desktop.fit_borderless(os.getpid(), monitor))
                        self.assertTrue(overlay_above_game())
                        self.assertEqual(foreground(), before_focus)
                finally:
                    u.DestroyWindow(overlay)
            finally:
                u.DestroyWindow(hwnd)


if __name__ == "__main__":
    unittest.main()
