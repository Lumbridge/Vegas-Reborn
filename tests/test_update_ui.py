"""Real update controls with synthetic signed-release operations."""
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock,patch
import sys
from test_companion_claim_ui import ControllerFixture
from vegas_reborn import application as app

class UpdateUiTests(ControllerFixture):
    def test_release_notes_opens_website_changelog(self):
        controller=self.create()
        with patch.object(app.webbrowser,"open") as open_browser:
            controller.view.widgets["release_notes"].invoke()
            open_browser.assert_called_once_with("https://vegas.lumbridge.co.uk/changelog.html")

    def test_check_download_verify_handoff_without_touching_a_real_install(self):
        controller=self.create()
        release=SimpleNamespace(version="0.4.1")
        prepare,handoff=Mock(return_value=Path("synthetic-plan.json")),Mock()
        with patch.object(app,"check_latest",return_value=release),patch.object(app,"record_check"),patch.object(app,"prepare_update",prepare),patch.object(app,"launch_helper",handoff),patch.object(sys,"frozen",True,create=True):
            self.assertTrue(controller.view.widgets["install_update"].instate(["disabled"]))
            controller.view.widgets["check_update"].invoke()
            self.pump(lambda:not controller.state.updating and controller.state.available_release is release)
            controller.view.widgets["install_update"].invoke()
            self.pump(lambda:handoff.called)
            self.assertEqual(prepare.call_args.args,(app.ROOT,release))
            self.assertTrue(callable(prepare.call_args.kwargs["progress"]))
            self.assertEqual(handoff.call_args.args[0],Path("synthetic-plan.json"))
            self.assertTrue(controller._closed)
    def test_update_handoff_is_blocked_during_game_or_host(self):
        controller=self.create()
        controller.state.available_release=SimpleNamespace(version="0.4.1")
        controller.state.staged_plan=Path("synthetic-plan.json")
        with patch.object(app,"launch_helper") as handoff,patch.object(sys,"frozen",True,create=True):
            controller.state.busy=True
            controller.update_and_restart()
            self.assertFalse(controller.apply_staged())
            controller.state.busy=False;controller.state.hosting=True
            controller.update_and_restart()
            self.assertFalse(controller.apply_staged())
            handoff.assert_not_called()
            controller.state.hosting=False

    def test_playing_can_stage_and_force_update_waits_for_character_save(self):
        controller=self.create()
        controller.state.available_release=SimpleNamespace(version="99.0.0")
        controller.state.busy=controller.state.playing=True
        controller.refresh()
        self.assertFalse(controller.view.widgets["install_update"].instate(["disabled"]))
        with patch.object(sys,"frozen",True,create=True),patch.object(app,"prepare_update",return_value=Path("plan.json")),patch.object(app,"launch_helper") as handoff,patch.object(app,"load_plan"),patch.object(app,"close_running_games") as close_games:
            controller.update_and_restart()
            self.pump(lambda:controller.state.staged_plan is not None and not controller.state.updating)
            self.assertTrue(controller.state.update_blocked)
            self.assertFalse(controller.view.widgets["force_update"].instate(["disabled"]))
            close_games.assert_not_called()
            controller.view.widgets["force_update"].invoke()
            self.pump(lambda:controller.state.force_close_finished)
            self.assertTrue(controller.state.updating)
            self.assertEqual(controller.view.update_activity.winfo_manager(),"pack")
            handoff.assert_not_called()  # Own launcher still saving the character.
            controller.messages.put(("done",None))
            self.pump(lambda:handoff.called)
            close_games.assert_called_once()
            self.assertTrue(controller._closed)

    def test_failed_force_close_keeps_companion_and_staged_update(self):
        controller=self.create()
        controller.state.staged_plan=Path("plan.json")
        controller.state.update_blocked=True
        with patch.object(app,"load_plan"),patch.object(app,"close_running_games",side_effect=app.UpdateError("Close it manually")),patch.object(app,"launch_helper") as handoff:
            controller.force_update()
            self.pump(lambda:not controller.state.updating)
            self.assertFalse(controller._closed)
            self.assertEqual(controller.values.update_status.get(),"Close it manually")
            self.assertEqual(controller.state.staged_plan,Path("plan.json"))
            handoff.assert_not_called()

    def test_tampered_staged_update_does_not_close_games(self):
        controller=self.create()
        controller.state.staged_plan=Path("plan.json")
        controller.state.update_blocked=True
        with patch.object(app,"load_plan",side_effect=app.UpdateError("Invalid update")),patch.object(app,"close_running_games") as close_games:
            controller.force_update()
            self.pump(lambda:not controller.state.updating)
            close_games.assert_not_called()
            self.assertFalse(controller._closed)

    def test_external_game_block_exposes_force_action(self):
        controller=self.create()
        controller.state.staged_plan=Path("plan.json")
        with patch.object(app,"launch_helper",side_effect=app.GamesBlockingUpdate("Game blocked")):
            self.assertFalse(controller.apply_staged())
        controller.refresh()
        self.assertTrue(controller.state.update_blocked)
        self.assertFalse(controller.view.widgets["force_update"].instate(["disabled"]))

    def test_installer_progress_window_renders_without_installing(self):
        import threading
        import tkinter as tk
        from vegas_reborn import update_ui
        # The helper runs alone in its real process. Tk mainloop counts all Tk
        # roots, so remove this fixture's unused root before creating its one.
        self.root.destroy()
        original_tk=tk.Tk
        finished=threading.Event()
        observed=[]
        def create_root():
            window=original_tk();window.withdraw()
            def inspect():
                def walk(widget):
                    for child in widget.winfo_children():
                        if child.winfo_class()=="TLabel":
                            variable=child.cget("textvariable")
                            if variable:observed.append(window.getvar(variable))
                        walk(child)
                walk(window);finished.set()
            window.after(180,inspect)
            return window
        def install(plan,parent_pid,*,progress):
            progress("Installing update files: 1 of 2")
            finished.wait(2)
        with patch.object(update_ui.tk,"Tk",side_effect=create_root),patch.object(update_ui,"apply_update",side_effect=install):
            self.assertEqual(update_ui.run_update(Path("synthetic-plan.json"),123),0)
        self.assertIn("Installing update files: 1 of 2",observed)
