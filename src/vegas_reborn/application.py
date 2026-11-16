"""Application coordination. Network workers never read or write Tk widgets."""
from dataclasses import dataclass
import os
from pathlib import Path
import queue
import sys
import threading
import time
import tkinter as tk
from tkinter import filedialog, messagebox
import webbrowser

from .companion import CompanionError, RetailLauncher
from .companion_settings import Preferences
from .dedicated import run_dedicated
from .discord_client import DiscordClient, DiscordClientError, load_session, save_session
from .discord_presence import DiscordPresence, activity_for
from .display import available_monitors
from .retail_rank import RankSourceError, read_rank
from .retail_sync import SyncConflict
from .ui.view import CompanionView, UiValues
from .updater import UpdateError, check_latest, record_check, prepare_update, launch_helper
from .version import VERSION, REPOSITORY

ROOT = Path(sys.executable).resolve().parent if getattr(sys,"frozen",False) else Path(__file__).resolve().parents[2]


class CompanionSetupError(Exception):
    pass


@dataclass
class AppState:
    session: object = None
    account_verified: bool = False
    busy: bool = False
    hosting: bool = False
    host_stop: object = None
    closing: bool = False
    playing: bool = False
    host_online: bool = False
    game_started_at: float | None = None
    updating: bool = False
    available_release: object = None
    staged_plan: object = None
    sync_conflict: bool = False
    launch_after_refresh: str | None = None


class CompanionController:
    def __init__(self, root, args):
        self.root,self.args=root,args
        self.state=AppState(launch_after_refresh="play" if args.play else "host" if args.host else None)
        self.messages=queue.Queue()
        self.preferences_path=args.session_file.parent/"preferences.json"
        preferences=Preferences.load(self.preferences_path)
        try:
            monitors=available_monitors()
        except OSError:
            monitors=[]
        self.values=UiValues(root,preferences,monitors)
        actions={"launch":self.launch_action,"connect":self.connect,"refresh":self.refresh_account,
                 "signout":self.sign_out,"claim":self.claim,"sync":self.sync,"saves":self.open_saves,
                 "browse":self.browse,"host":self.host,"stop_host":self.stop_host,
                 "check_updates":self.check_updates,"update":self.update_and_restart,
                 "cloud":lambda:self.resolve(True),"local":lambda:self.resolve(False),
                 "get_discord":lambda:webbrowser.open("https://discord.com/download"),
                 "release_notes":lambda:webbrowser.open("https://github.com/"+REPOSITORY+"/releases")}
        self.view=CompanionView(root,self.values,actions)
        try:
            self.launcher=RetailLauncher(args.config,notify=self.notify)
        except Exception as error:
            raise CompanionSetupError("Missing or invalid companion configuration") from error
        self.presence=DiscordPresence(notify=lambda value:self.messages.put(("discord_status",value)))
        self._closed=False
        self._confirming=False
        self._poll_timer=None
        self._startup_timers=[]
        for variable in (self.values.retail,self.values.mode,self.values.minimize,self.values.skip_intros,self.values.share_discord,
                         self.values.auto_check,self.values.monitor_name):
            variable.trace_add("write",self.preference_changed)
        self.values.retail.trace_add("write",lambda *_:self.update_rank())
        try:
            self.state.session=load_session(args.session_file)
            self.values.account.set("Signed in as "+self.state.session.display_name)
            self.values.status.set("Checking your shared account...")
        except Exception:
            pass
        root.protocol("WM_DELETE_WINDOW",self.close)
        self.poll()
        if self.state.session:
            self._startup_timers.append(root.after(200,self.refresh_account))
        if self.values.auto_check.get() and getattr(sys,"frozen",False):
            self._startup_timers.append(root.after(1000,self.check_updates))

    @property
    def ready_to_play(self):
        session=self.state.session
        return bool(session and self.state.account_verified and not self.state.sync_conflict
                    and not (session.name_claims_enabled and session.claim_required))

    def notify(self,text):
        self.messages.put(("status",text))

    def preference_changed(self,*_):
        v=self.values
        try:
            Preferences(game_folder=v.retail.get().strip(),display_mode=v.mode.get(),monitor_device=v.monitor_labels[v.monitor_name.get()],
                        minimize_on_play=v.minimize.get(),discord_presence=v.share_discord.get(),check_updates=v.auto_check.get(),
                        skip_intros=v.skip_intros.get()).save(self.preferences_path)
        except OSError:
            v.status.set("Preferences could not be saved. Check that the companion folder is writable.")
        self.refresh()

    def update_rank(self):
        session=self.state.session
        if session is None:
            self.values.rank.set("Sign in to see your saved rank and XP.")
            return
        try:
            profile=self.launcher.sync.profile_directory(session)/"R6_PECPEC"
            rank=read_rank(profile,retail_root=self.values.retail.get().strip())
            next_step=f"{rank.experience_to_next:,} XP to the next step" if rank.experience_to_next is not None else "Maximum rank"
            self.values.rank.set(f"{rank.label.title()}  /  {rank.subrank}/5\n{rank.experience:,} XP  ·  "+next_step)
        except FileNotFoundError:
            self.values.rank.set("Sync your character to see your saved rank.")
        except (OSError,ValueError,RankSourceError,TypeError):
            self.values.rank.set("Rank unavailable. Check your game folder and saved character.")

    def _error_message(self,error):
        if isinstance(error,DiscordClientError):
            return {"invalid_player_name":"Use 3–16 letters, digits, _ - or .; start with a letter or digit.",
                    "player_name_taken":"That name is taken or held for its previous owner. Choose another name.",
                    "player_name_change_too_soon":"Names can change every 30 days. Capitalization and separator changes are available sooner.",
                    "account_session_expired_or_revoked":"Your sign-in expired. Sign in with Discord again."}.get(str(error),"The account operation failed. Retry or sign in again.")
        return str(error) if isinstance(error,CompanionError) else "The operation failed. Your local character was preserved. Retry sign-in or synchronization."

    def start_operation(self,operation):
        if self.state.busy or self.state.closing:
            return
        self.state.busy=True
        self.refresh()
        def worker():
            try:
                operation()
            except SyncConflict:
                self.messages.put(("conflict",None))
            except Exception as error:
                self.messages.put(("error",self._error_message(error)))
            finally:
                self.messages.put(("done",None))
        threading.Thread(target=worker,daemon=False,name="VegasAccountOperation").start()

    def persist_session(self,updated):
        destination=self.args.session_file
        temporary=destination.with_name(destination.name+".new")
        temporary.unlink(missing_ok=True)
        save_session(temporary,updated)
        temporary.replace(destination)

    @staticmethod
    def account_ready(session):
        return "Choose your player name before playing or hosting." if session.name_claims_enabled and session.claim_required else "Ready to play. Steam must be running and signed in."

    def refresh_account(self):
        session=self.state.session
        if session is None:
            return
        def operation():
            updated=DiscordClient().refresh_account(session)
            self.persist_session(updated)
            self.messages.put(("session",updated))
            self.notify(self.account_ready(updated))
        self.start_operation(operation)

    def connect(self):
        if self.state.hosting:
            return
        def operation():
            client=DiscordClient()
            link=client.begin_link()
            self.notify("Approve the Discord sign-in in your browser.")
            webbrowser.open(link.url,new=2)
            linked=client.wait_for_link(link)
            self.persist_session(linked)
            self.messages.put(("session",linked))
            self.notify(self.account_ready(linked))
        self.start_operation(operation)

    def claim(self):
        session,name=self.state.session,self.values.claimed_name.get()
        if session is None or not session.name_claims_enabled or session.rename_on_cooldown or self.state.busy or self.state.hosting:
            return
        def operation():
            updated=DiscordClient().claim_name(session,name)
            self.persist_session(updated)
            self.messages.put(("session",updated))
            self.notify("Player name saved. "+self.account_ready(updated))
        self.start_operation(operation)

    def launch_action(self):
        if self.state.session is None:
            self.connect()
        elif self.state.session.name_claims_enabled and self.state.session.claim_required:
            self.view.focus_name()
        elif self.state.sync_conflict:
            self.view.show_conflict(True)
        elif not self.state.account_verified:
            self.refresh_account()
        else:
            self.play()

    def play(self):
        if not self.ready_to_play:
            return
        session,source=self.state.session,self.values.retail.get().strip()
        mode=self.values.mode.get()
        device=self.values.monitor_labels[self.values.monitor_name.get()]
        minimize=self.values.minimize.get()
        skip_intros=self.values.skip_intros.get()
        self.start_operation(lambda:self.launcher.play(session,source=source,display_mode=mode,monitor_device=device,
                              skip_intros=skip_intros,
                              on_started=lambda:self.messages.put(("game_started",minimize))))

    def sync(self):
        session=self.state.session
        if session is None:
            return
        def operation():
            self.launcher.sync.pull(session)
            if (self.launcher.sync.profile_directory(session)/"R6_PECPEC").exists():
                self.launcher.sync.push(session)
            self.notify("Cloud character and shared progression are up to date.")
        self.start_operation(operation)

    def resolve(self,cloud):
        session=self.state.session
        if session is None:
            return
        def operation():
            if cloud:
                self.launcher.sync.pull(session,replace_local=True)
            else:
                self.launcher.sync.push(session,replace_cloud=True)
            self.messages.put(("resolved","Character choice saved. You can play now."))
        self.start_operation(operation)

    def open_saves(self):
        if self.state.session:
            try:
                path=self.launcher.sync.profile_directory(self.state.session)
                path.mkdir(parents=True,exist_ok=True)
                os.startfile(path)
            except OSError:
                self.values.status.set("Could not open your character folder. Check folder permissions and retry.")

    def browse(self):
        selected=filedialog.askdirectory(parent=self.root,title="Choose your installed Rainbow Six Vegas folder",initialdir=self.values.retail.get())
        if selected:
            self.values.retail.set(selected)

    def sign_out(self):
        if self.state.busy or self.state.hosting or self.state.updating or not self.state.session:
            return
        if not self.confirm("Sign out?", "Sign out of Vegas Reborn on this PC?\n\nYour character and saved settings will be kept."):
            return
        if self.state.busy or self.state.hosting or self.state.updating:
            return
        try:
            self.args.session_file.unlink(missing_ok=True)
        except OSError:
            self.values.status.set("Could not remove the saved sign-in. Check folder permissions and retry.")
            return
        self.state.session,self.state.account_verified=None,False
        self.state.sync_conflict=False
        self.values.account.set("Not signed in")
        self.values.claimed_name.set("")
        self.values.status.set("Signed out on this PC. Your character stays with your account.")
        self.update_rank()
        self.view.show_conflict(False)
        self.refresh()

    def host(self):
        if self.state.hosting or self.state.busy or not self.ready_to_play:
            return
        self.state.hosting,self.state.host_stop=True,threading.Event()
        self.view.show_page("host")
        source,stop=self.values.retail.get().strip(),self.state.host_stop
        self.values.host_status.set("Preparing your dedicated server...")
        self.refresh()
        def worker():
            try:
                run_dedicated(self.args.session_file,source=source,config_path=self.args.config,stop_event=stop,
                              notify=lambda text:self.messages.put(("host_status",text)))
            except Exception:
                self.messages.put(("host_status","The server stopped. Check your game folder and sign-in, then retry."))
            finally:
                self.messages.put(("host_done",None))
        threading.Thread(target=worker,daemon=False,name="VegasDedicatedHost").start()

    def stop_host(self):
        if self.state.host_stop:
            self.state.host_stop.set()
            self.values.host_status.set("Stopping your dedicated server...")

    def start_update_worker(self,operation):
        if self.state.updating or self.state.closing:
            return
        self.state.updating=True
        self.refresh()
        def worker():
            try:
                operation()
            except Exception as error:
                self.messages.put(("update_error",str(error) if isinstance(error,UpdateError) else "The update failed. Your current app and character were preserved."))
            finally:
                self.messages.put(("update_done",None))
        threading.Thread(target=worker,daemon=True,name="VegasUpdateDownload").start()

    def check_updates(self):
        if self.state.updating:
            return
        self.values.update_status.set("Checking GitHub Releases...")
        def operation():
            release=check_latest()
            if getattr(sys,"frozen",False):
                record_check(ROOT,release)
            self.messages.put(("update_checked",release))
        self.start_update_worker(operation)

    def apply_staged(self):
        if self.state.busy or self.state.hosting or self.state.closing:
            self.values.update_status.set("Update downloaded. Finish playing and stop hosting, then choose Update and restart.")
            return False
        try:
            launch_helper(self.state.staged_plan,os.getpid())
        except UpdateError as error:
            self.values.update_status.set(str(error))
            return False
        self.shutdown()
        self.root.destroy()
        return True

    def update_and_restart(self):
        s=self.state
        if s.busy or s.hosting or s.updating or not s.available_release:
            return
        if not getattr(sys,"frozen",False):
            self.values.update_status.set("Run the packaged companion to install updates. Source checkouts update through Git.")
            return
        if s.staged_plan:
            self.apply_staged()
        else:
            release=s.available_release
            self.values.update_status.set("Downloading and verifying "+release.version+"...")
            self.start_update_worker(lambda:self.messages.put(("update_staged",prepare_update(ROOT,release))))

    def refresh(self):
        if self._closed:
            return
        s=self.state
        self.presence.set_activity(activity_for(playing=s.playing,hosting=s.host_online,preparing_host=s.hosting and not s.host_online,
                                               started=s.game_started_at) if self.values.share_discord.get() else None)
        self.view.render(s)

    def poll(self):
        if self._closed:
            return
        s=self.state
        while True:
            try:
                kind,value=self.messages.get_nowait()
            except queue.Empty:
                break
            if kind=="session":
                if s.session is None or s.session.discord_id!=value.discord_id:
                    s.sync_conflict=False
                    self.view.show_conflict(False)
                s.session,s.account_verified=value,True
                self.values.account.set("Signed in as "+(value.claimed_name or value.display_name))
                self.values.claimed_name.set(value.claimed_name or "")
                self.update_rank()
                if value.name_claims_enabled and value.claim_required:
                    self.view.focus_name()
            elif kind=="done":
                s.busy,s.playing,s.game_started_at=False,False,None
                self.update_rank()
                if self.root.state()=="iconic":
                    self.root.deiconify()
                if s.launch_after_refresh:
                    requested,s.launch_after_refresh=s.launch_after_refresh,None
                    if self.ready_to_play:
                        self.root.after(100,self.play if requested=="play" else self.host)
            elif kind=="game_started":
                s.playing,s.game_started_at=True,time.time()
                if value:
                    self.root.iconify()
            elif kind=="host_status":
                self.values.host_status.set(value)
                s.host_online=value.startswith("Dedicated server running.")
            elif kind=="host_done":
                s.hosting,s.host_stop,s.host_online=False,None,False
            elif kind=="discord_status":
                self.values.discord_status.set(value)
            elif kind=="conflict":
                s.sync_conflict=True
                self.values.status.set("Your cloud and local character need a choice. Your higher XP is preserved.")
                self.view.show_conflict(True)
            elif kind=="resolved":
                s.sync_conflict=False
                self.values.status.set(value)
                self.view.show_conflict(False)
            elif kind=="update_checked":
                s.available_release,s.staged_plan=value,None
                self.values.update_status.set("Version "+value.version+" is available. Update and restart when you are ready." if value
                                              else "Version "+VERSION+" · You are up to date.")
            elif kind=="update_staged":
                s.staged_plan=value
                if self.apply_staged():
                    return
            elif kind=="update_done":
                s.updating=False
            elif kind=="update_error":
                self.values.update_status.set(value)
            else:
                self.values.status.set(value)
        self.refresh()
        if s.closing and not s.hosting and not s.busy:
            self.shutdown();self.root.destroy();return
        self._poll_timer=self.root.after(100,self.poll)

    def confirm(self,title,message):
        if self._confirming or self._closed:
            return False
        self._confirming=True
        try:
            return messagebox.askyesno(title,message,parent=self.root,default=messagebox.NO)
        finally:
            self._confirming=False

    def close(self):
        if self._closed or self.state.closing or self._confirming:
            return
        if self.state.updating:
            self.values.status.set("Wait for the update check or download to finish before closing.")
            return
        if self.state.busy:
            self.values.status.set("Keep the companion open while the game or account operation is running. It saves your character when the game closes.")
            return
        message="Close Vegas Reborn?"
        if self.state.hosting:
            message+="\n\nYour dedicated server will be stopped."
        if not self.confirm("Close Vegas Reborn?",message):
            return
        # A native modal dialog still dispatches Tk events. Recheck work that
        # may have started while it was open before changing application state.
        if self.state.busy or self.state.updating:
            return
        if self.state.hosting:
            self.state.closing=True
            self.stop_host()
            self.values.status.set("Stopping your dedicated server before closing...")
            self.refresh()
            return
        self.shutdown();self.root.destroy()

    def shutdown(self):
        if self._closed:
            return
        self._closed=True
        self.presence.close()
        for timer in [self._poll_timer,*self._startup_timers]:
            if timer:
                try:self.root.after_cancel(timer)
                except tk.TclError:pass
        try:self.view.activity.stop()
        except tk.TclError:pass

    def run(self):
        try:
            self.root.mainloop()
        finally:
            self.shutdown()
        return 0
