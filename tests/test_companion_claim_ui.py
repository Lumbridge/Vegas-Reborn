"""Hidden real-Tk controls with synthetic account replies; no game or network."""
from dataclasses import replace
import gc
import importlib.util
from pathlib import Path
import sys
import tempfile
import time
import tkinter as tk
from tkinter import ttk
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from vegas_reborn.discord_client import AccountSession, save_session


class ClaimUiTests(unittest.TestCase):
    def tearDown(self):
        # Tcl objects from a destroyed fixture must be finalized on the UI
        # thread, before a later test starts an account worker and triggers GC.
        gc.collect()

    def exercise(self, enabled):
        spec = importlib.util.spec_from_file_location("claim_ui_fixture", Path(__file__).resolve().parents[1] / "tools/vegas_companion.py")
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        try:
            window = tk.Tk()
        except tk.TclError:
            self.skipTest("Tk display unavailable")
        window.withdraw()
        real_tk_type = type(window)
        with tempfile.TemporaryDirectory() as directory:
            session = AccountSession("199999999999999999", "ry", "a" * 64, None, enabled, enabled)
            path = Path(directory) / "session.json"
            save_session(path, session)
            client = SimpleNamespace(refresh_account=Mock(return_value=session),
                claim_name=Mock(side_effect=lambda old, name: replace(old, claimed_name=name.strip(), claim_required=False)))
            def children(parent):
                for child in parent.winfo_children():
                    yield child
                    yield from children(child)
            def button(text):
                return next(child for child in children(window) if isinstance(child, ttk.Button) and child.cget("text") == text)
            def pump_until(condition):
                deadline = time.monotonic() + 3
                while not condition() and time.monotonic() < deadline:
                    window.update()
                    time.sleep(.005)
                self.assertTrue(condition())
            def run_loop(_window, *_args):
                try:
                    pump_until(lambda: client.refresh_account.called and not button("Refresh account").instate(["disabled"]))
                    client.claim_name.assert_not_called()
                    if enabled:
                        self.assertTrue(button("Play Vegas   â†’").instate(["disabled"]))
                        self.assertTrue(button("Host dedicated server").instate(["disabled"]))
                        claim = button("Claim name")
                        entry = next(child for child in claim.master.winfo_children() if isinstance(child, ttk.Entry))
                        self.assertEqual(entry.get(), "")  # Never autoclaim Discord display "ry".
                        entry.insert(0, "Test_Name")
                        claim.invoke()
                        pump_until(lambda: client.claim_name.called and not button("Play Vegas   â†’").instate(["disabled"]))
                        client.claim_name.assert_called_once_with(session, "Test_Name")
                        self.assertFalse(button("Host dedicated server").instate(["disabled"]))
                    else:
                        self.assertFalse(button("Play Vegas   â†’").instate(["disabled"]))
                        self.assertFalse(button("Host dedicated server").instate(["disabled"]))
                        self.assertEqual(button("Claim name").master.master.winfo_manager(), "")
                finally:
                    for pending in window.tk.call("after", "info"):
                        window.after_cancel(pending)
                    window.destroy()
            with patch.object(module.tk, "Tk", return_value=window), \
                 patch.object(real_tk_type, "mainloop", run_loop), \
                 patch.object(module, "available_monitors", return_value=[]), \
                 patch.object(module, "RetailLauncher", return_value=Mock()), \
                 patch.object(module, "DiscordPresence", return_value=Mock()), \
                 patch.object(module, "DiscordClient", return_value=client), \
                 patch.object(sys, "argv", ["vegas_companion", "--session-file", str(path)]):
                self.assertEqual(module.main(), 0)

    def test_advertised_claim_feature_blocks_play_until_explicit_name(self):
        self.exercise(True)

    def test_old_server_without_claim_feature_retains_play(self):
        self.exercise(False)
