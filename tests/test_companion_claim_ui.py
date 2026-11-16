"""Real hidden-Tk account flow with synthetic services, no game/network."""
import gc
from dataclasses import replace
from pathlib import Path
import sys,tempfile,time,tkinter as tk,unittest
import _tkinter
from types import SimpleNamespace
from unittest.mock import Mock,patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/"src"))
from vegas_reborn import application as app
from vegas_reborn.companion_settings import Preferences
from vegas_reborn.discord_client import AccountSession,save_session

class ControllerFixture(unittest.TestCase):
    def setUp(self):
        self.temporary=tempfile.TemporaryDirectory()
        self.directory=Path(self.temporary.name)
        self.args=SimpleNamespace(config=self.directory/"service.json",session_file=self.directory/"account-session.json",play=False,host=False)
        Preferences(check_updates=False,discord_presence=False,minimize_on_play=False).save(self.directory/"preferences.json")
        self.root=tk.Tk();self.root.withdraw()
        self.launcher=Mock()
        self.launcher.sync.profile_directory.return_value=self.directory/"profile"
        self.client=Mock()
        self.confirmation=Mock(return_value=True)
        self.patchers=[patch.object(app.messagebox,"askyesno",self.confirmation),patch.object(app,"RetailLauncher",return_value=self.launcher),patch.object(app,"DiscordPresence",return_value=Mock()),patch.object(app,"available_monitors",return_value=[]),patch.object(app,"DiscordClient",return_value=self.client)]
        for item in self.patchers:item.start()
        self.controller=None
    def create(self,session=None):
        if session:save_session(self.args.session_file,session)
        self.controller=app.CompanionController(self.root,self.args)
        return self.controller
    def pump(self,condition):
        end=time.monotonic()+4
        while not condition() and time.monotonic()<end:
            try:
                # update() drains the whole queue and can starve this deadline
                # while progress animations keep adding events on a slow VM.
                for _ in range(30):
                    if not self.root.tk.dooneevent(_tkinter.ALL_EVENTS|_tkinter.DONT_WAIT) or condition():break
            except tk.TclError:
                if condition():break
                raise
            time.sleep(.005)
        self.assertTrue(condition())
    def tearDown(self):
        if self.controller:self.controller.shutdown()
        try:self.root.destroy()
        except tk.TclError:pass
        for item in reversed(self.patchers):item.stop()
        self.temporary.cleanup()
        gc.collect()

class ClaimUiTests(ControllerFixture):
    def test_name_readonly_during_cooldown_and_unlocks_when_it_expires(self):
        from datetime import datetime, timedelta, timezone
        now=datetime.now(timezone.utc)
        session=AccountSession("199999999999999999","Tester","a"*64,"Test_Name",True,False,(now+timedelta(days=30)).isoformat())
        self.client.refresh_account.return_value=session
        controller=self.create(session)
        self.pump(lambda:controller.ready_to_play and not controller.state.busy)
        entry=controller.view.widgets["name_entry"]
        self.assertTrue(entry.instate(["readonly"]))
        self.assertTrue(controller.view.widgets["claim"].instate(["disabled"]))
        controller.claim()
        self.client.claim_name.assert_not_called()
        # The normal polling render must unlock the field without a refresh.
        with patch("vegas_reborn.discord_client.datetime") as clock:
            clock.fromisoformat=datetime.fromisoformat
            clock.now.return_value=now+timedelta(days=31)
            controller.refresh()
            self.assertTrue(entry.instate(["!readonly","!disabled"]))
            self.assertTrue(controller.view.widgets["claim"].instate(["!disabled"]))

    def exercise(self,enabled):
        session=AccountSession("199999999999999999","Tester","a"*64,None,enabled,enabled)
        self.client.refresh_account.return_value=session
        self.client.claim_name.side_effect=lambda old,name:replace(old,claimed_name=name.strip(),claim_required=False)
        controller=self.create(session)
        self.pump(lambda:controller.state.account_verified and not controller.state.busy)
        self.client.claim_name.assert_not_called()
        if enabled:
            self.assertFalse(controller.ready_to_play)
            self.assertEqual(controller.view.primary_button.cget("text"),"Choose player name")
            controller.play();self.launcher.play.assert_not_called()
            self.assertTrue(controller.view.widgets["host"].instate(["disabled"]))
            self.assertEqual(controller.view.current_page,"account")
            entry=controller.view.widgets["name_entry"]
            self.assertEqual(entry.get(),"")
            entry.insert(0,"Test_Name")
            controller.view.widgets["claim"].invoke()
            self.pump(lambda:controller.ready_to_play and not controller.state.busy)
            self.client.claim_name.assert_called_once_with(session,"Test_Name")
        else:
            self.assertTrue(controller.ready_to_play)
            self.assertEqual(controller.view.identity_card.winfo_manager(),"")
        self.assertFalse(controller.view.widgets["host"].instate(["disabled"]))
    def test_advertised_claim_feature_blocks_play_until_explicit_name(self):self.exercise(True)
    def test_old_server_without_claim_feature_retains_play(self):self.exercise(False)
