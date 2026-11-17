"""Lifecycle regressions for the refactored controller; no retail launch."""
from dataclasses import replace
import json
from pathlib import Path
import threading
from types import SimpleNamespace
from unittest.mock import patch
from test_companion_claim_ui import ControllerFixture
from vegas_reborn import application as app
from vegas_reborn.discord_client import AccountSession
from vegas_reborn.retail_sync import SyncConflict
from vegas_reborn.companion import CompanionError


class ApplicationFlowTests(ControllerFixture):
    def test_missing_launch_helper_is_modal_persistent_and_retry_clears_banner(self):
        controller=self.signed_in()
        message="VegasSteamControl.exe is missing from the companion installation. Security software may have removed it."
        self.launcher.play.side_effect=CompanionError(message)
        controller.state.launch_after_refresh="play"
        with patch.object(app.messagebox,"showerror") as dialog:
            controller.play()
            self.pump(lambda:not controller.state.busy)
        dialog.assert_called_once_with("Game launch failed",message,parent=self.root)
        self.assertIsNone(controller.state.launch_after_refresh)
        self.launcher.play.assert_called_once()
        self.assertTrue(controller.view.error_banner.winfo_manager())
        self.assertEqual(controller.view.error_message.get(),message)
        controller.view.show_page("display")
        controller.notify("A later status update")
        self.pump(lambda:controller.values.status.get()=="A later status update")
        self.assertTrue(controller.view.error_banner.winfo_manager())
        report=json.loads((self.directory/"last-launch-error.json").read_text())
        self.assertEqual(set(report),{"version","time","title","message"})
        self.assertEqual(report["message"],message)
        self.launcher.play.side_effect=None
        controller.play()
        self.pump(lambda:not controller.state.busy)
        self.assertFalse(controller.view.error_banner.winfo_manager())

    def test_launch_error_restores_minimized_app_before_showing_dialog(self):
        controller=self.signed_in()
        self.launcher.play.side_effect=CompanionError("Synthetic missing component")
        calls=[]
        with patch.object(self.root,"state",return_value="iconic"), \
             patch.object(self.root,"deiconify",side_effect=lambda:calls.append("restore")), \
             patch.object(self.root,"lift",side_effect=lambda:calls.append("lift")), \
             patch.object(app.messagebox,"showerror",side_effect=lambda *a,**kw:calls.append("dialog")):
            controller.play()
            self.pump(lambda:not controller.state.busy)
        self.assertEqual(calls[:3],["restore","lift","dialog"])

    def test_unexpected_launch_error_does_not_log_exception_secrets_or_offer_signin(self):
        controller=self.signed_in()
        secret="private-native-capability-must-not-leak"
        self.launcher.play.side_effect=RuntimeError(secret)
        with patch.object(app.messagebox,"showerror") as dialog:
            controller.play()
            self.pump(lambda:not controller.state.busy)
        report=(self.directory/"last-launch-error.json").read_text()
        message=dialog.call_args.args[1]
        self.assertNotIn(secret,report)
        self.assertNotIn(secret,message)
        self.assertNotIn("sign-in",message)
        self.assertEqual(json.loads(report)["message"],message)
        self.assertIn("companion installation",message)

    def signed_in(self):
        session=AccountSession("199999999999999999","Operator","a"*64,"Operator",True,False)
        self.client.refresh_account.return_value=session
        controller=self.create(session)
        self.pump(lambda:controller.ready_to_play and not controller.state.busy)
        return controller

    def test_launch_waits_for_exit_and_blocks_closing_during_play(self):
        controller=self.signed_in()
        finish=threading.Event()
        def game(*args,**kwargs):
            kwargs["on_started"]()
            finish.wait(timeout=5)
        self.launcher.play.side_effect=game
        controller.values.skip_intros.set(True)
        try:
            controller.play()
            self.pump(lambda:controller.state.playing)
            controller.close()
            self.assertFalse(controller._closed)
            self.confirmation.assert_not_called()
            self.assertIn("Keep the companion open",controller.values.status.get())
        finally:
            finish.set()
            self.pump(lambda:not controller.state.busy)
        self.assertFalse(controller.state.playing)
        self.launcher.play.assert_called_once()
        self.assertTrue(self.launcher.play.call_args.kwargs["skip_intros"])

    def test_sync_conflict_shows_resolution_and_blocks_launch_until_choice(self):
        controller=self.signed_in()
        self.launcher.sync.pull.side_effect=SyncConflict("synthetic")
        controller.sync()
        self.pump(lambda:controller.state.sync_conflict and not controller.state.busy)
        self.assertEqual(controller.view.current_page,"account")
        self.assertTrue(controller.view.conflict_card.winfo_manager())
        controller.play();self.launcher.play.assert_not_called()
        self.launcher.sync.pull.side_effect=None
        controller.resolve(True)
        self.pump(lambda:not controller.state.sync_conflict and not controller.state.busy)
        self.launcher.sync.pull.assert_called_with(controller.state.session,replace_local=True)
        self.assertTrue(controller.ready_to_play)

    def test_legacy_host_stops_before_closing_without_normal_nav_entry(self):
        controller=self.signed_in()
        self.assertNotIn("host",controller.view.nav_buttons)
        def dedicated(*args,**kwargs):
            kwargs["notify"]("Dedicated server running. Join through Online > Custom Match.")
            kwargs["stop_event"].wait(timeout=5)
        with patch.object(app,"run_dedicated",side_effect=dedicated):
            controller.host()
            self.pump(lambda:controller.state.host_online)
            self.assertEqual(controller.view.current_page,"host")
            controller.close()
            self.assertTrue(controller.state.closing)
            self.pump(lambda:controller._closed)
            self.assertFalse(controller.state.hosting)

    def test_signout_preserves_character_and_returns_launch_action_to_signin(self):
        controller=self.signed_in()
        profile=self.directory/"profile/R6_PECPEC"
        profile.parent.mkdir();profile.write_bytes(b"synthetic character")
        controller.sign_out()
        self.assertIsNone(controller.state.session)
        self.assertFalse(self.args.session_file.exists())
        self.assertEqual(profile.read_bytes(),b"synthetic character")
        self.assertEqual(controller.view.primary_button.cget("text"),"Sign in to launch")
        self.confirmation.assert_called_once()

    def test_cancel_signout_keeps_session_and_character_state(self):
        controller=self.signed_in()
        original=self.args.session_file.read_bytes()
        session=controller.state.session
        self.confirmation.return_value=False
        controller.sign_out()
        self.assertIs(controller.state.session,session)
        self.assertEqual(self.args.session_file.read_bytes(),original)
        self.assertTrue(controller.ready_to_play)
        self.assertEqual(self.confirmation.call_args.kwargs["default"],"no")

    def test_close_requires_confirmation(self):
        controller=self.create()
        self.confirmation.return_value=False
        controller.close()
        self.assertFalse(controller._closed)
        self.assertTrue(self.root.winfo_exists())
        self.confirmation.return_value=True
        controller.close()
        self.assertTrue(controller._closed)

    def test_cancel_close_does_not_stop_dedicated_server(self):
        controller=self.create()
        controller.state.hosting=True
        controller.state.host_stop=threading.Event()
        self.confirmation.return_value=False
        controller.close()
        self.assertFalse(controller.state.closing)
        self.assertFalse(controller.state.host_stop.is_set())

    def test_close_waits_for_update_without_prompting(self):
        controller=self.create()
        controller.state.updating=True
        controller.close()
        self.assertFalse(controller._closed)
        self.confirmation.assert_not_called()

    def test_switching_accounts_clears_previous_accounts_character_conflict(self):
        controller=self.signed_in()
        controller.state.sync_conflict=True
        controller.view.show_conflict(True)
        other=AccountSession("188888888888888888","Other","b"*64,"Other",True,False)
        controller.messages.put(("session",other))
        self.pump(lambda:controller.state.session is other)
        self.assertFalse(controller.state.sync_conflict)
        self.assertEqual(controller.view.conflict_card.winfo_manager(),"")
        self.assertTrue(controller.ready_to_play)

    def test_navigation_preserves_preferences_and_uses_five_main_pages(self):
        controller=self.create()
        self.assertEqual(len(controller.view.nav_buttons),5)
        controller.values.mode.set("Windowed")
        controller.values.minimize.set(False)
        controller.values.skip_intros.set(True)
        for key in controller.view.nav_buttons:
            controller.view.nav_buttons[key].invoke()
            self.root.update()
            self.assertEqual(controller.view.current_page,key)
        preferences=app.Preferences.load(controller.preferences_path)
        self.assertEqual(preferences.display_mode,"Windowed")
        self.assertFalse(preferences.minimize_on_play)
        self.assertTrue(preferences.skip_intros)

    def test_page_switching_preserves_initial_and_manually_resized_geometry(self):
        import ctypes as c
        from ctypes import wintypes as w
        controller=self.create()
        # Only this synthetic fixture window is mapped. Prevent activation so
        # the geometry check cannot take focus from the user's applications.
        u=c.WinDLL("user32")
        u.GetParent.argtypes=[w.HWND];u.GetParent.restype=w.HWND
        suffix="PtrW" if c.sizeof(c.c_void_p)==8 else "W"
        get=getattr(u,"GetWindowLong"+suffix);set_=getattr(u,"SetWindowLong"+suffix)
        get.argtypes=[w.HWND,c.c_int];get.restype=c.c_ssize_t
        set_.argtypes=[w.HWND,c.c_int,c.c_ssize_t];set_.restype=c.c_ssize_t
        handle=u.GetParent(self.root.winfo_id())
        set_(handle,-20,get(handle,-20)|0x08000000)
        self.root.deiconify()
        for geometry in ("1040x710+80+80","1120x760+80+80"):
            self.root.geometry(geometry);self.root.update()
            expected=self.root.geometry()
            for key in controller.view.nav_buttons:
                controller.view.show_page(key);self.root.update()
                self.assertEqual(self.root.geometry(),expected,key)
