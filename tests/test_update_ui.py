"""Exercise hidden real-Tk update controls with a synthetic release peer."""
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


class UpdateUiTests(unittest.TestCase):
    def test_check_download_verify_handoff_without_touching_a_real_install(self):
        root = Path(__file__).resolve().parents[1]
        sys.path.insert(0, str(root/"src"))
        from vegas_reborn.companion_settings import Preferences
        spec=importlib.util.spec_from_file_location("update_ui_fixture",root/"tools/vegas_companion.py")
        module=importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        window=tk.Tk();window.withdraw()
        real_type=type(window)
        release=SimpleNamespace(version="0.3.1")
        prepare, handoff=Mock(return_value=Path("synthetic-plan.json")),Mock()
        with tempfile.TemporaryDirectory() as directory:
            session=Path(directory)/"session.json"
            Preferences(check_updates=False).save(session.parent/"preferences.json")
            def descendants(parent):
                for child in parent.winfo_children():
                    yield child
                    yield from descendants(child)
            def button(text):
                return next(item for item in descendants(window) if isinstance(item,ttk.Button) and item.cget("text")==text)
            def pump(condition):
                end=time.monotonic()+3
                while not condition() and time.monotonic()<end:
                    window.update();time.sleep(.005)
                self.assertTrue(condition())
            def loop(_):
                self.assertTrue(button("Update and restart").instate(["disabled"]))
                button("Check for updates").invoke()
                pump(lambda: not button("Update and restart").instate(["disabled"]))
                button("Update and restart").invoke()
                pump(lambda: handoff.called)
                prepare.assert_called_once_with(module.ROOT,release)
                handoff.assert_called_once()
                self.assertEqual(handoff.call_args.args[0],Path("synthetic-plan.json"))
            with patch.object(module.tk,"Tk",return_value=window),patch.object(real_type,"mainloop",loop),\
                 patch.object(module,"load_session",side_effect=ValueError()),\
                 patch.object(module,"RetailLauncher",return_value=Mock()),\
                 patch.object(module,"DiscordPresence",return_value=Mock()),\
                 patch.object(module,"available_monitors",return_value=[]),\
                 patch.object(module,"check_latest",return_value=release),\
                 patch.object(module,"record_check",return_value=None),\
                 patch.object(module,"prepare_update",prepare),patch.object(module,"launch_helper",handoff),\
                 patch.object(sys,"frozen",True,create=True),\
                 patch.object(sys,"argv",["vegas_companion","--session-file",str(session)]):
                self.assertEqual(module.main(),0)


if __name__=="__main__":unittest.main()
