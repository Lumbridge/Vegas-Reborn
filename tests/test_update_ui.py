"""Real update controls with synthetic signed-release operations."""
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock,patch
import sys
from test_companion_claim_ui import ControllerFixture
from vegas_reborn import application as app

class UpdateUiTests(ControllerFixture):
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
            prepare.assert_called_once_with(app.ROOT,release)
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
