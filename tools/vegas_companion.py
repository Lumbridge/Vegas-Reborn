"""Vegas Reborn Companion: play, host, display settings and shared progression."""
import argparse
import os
from pathlib import Path
import queue
import sys
import threading
import time
import tkinter as tk
from tkinter import ttk, messagebox, filedialog
import webbrowser

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src"))
from vegas_reborn.companion import RetailLauncher, CompanionError
from vegas_reborn.discord_client import DiscordClient, DiscordClientError, load_session, save_session
from vegas_reborn.retail_sync import SyncConflict
from vegas_reborn.companion_settings import Preferences
from vegas_reborn.display import MODES, available_monitors
from vegas_reborn.discord_presence import DiscordPresence, activity_for
from vegas_reborn.retail_rank import RankSourceError, read_rank
from vegas_reborn.version import VERSION
from vegas_reborn.updater import UpdateError, check_latest, prepare_update, launch_helper, apply_update


def main():
    # Frozen windowed Python has no console streams. Python 3.10 argparse
    # still writes help/errors to them, so supply sinks before parsing.
    for name in ("stdout", "stderr"):
        if getattr(sys, name) is None:
            setattr(sys, name, open(os.devnull, "w"))
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, default=ROOT / "local/companion/service.json")
    parser.add_argument("--session-file", type=Path, default=ROOT / "local/companion/account-session.json")
    parser.add_argument("--apply-update", type=Path, help=argparse.SUPPRESS)
    parser.add_argument("--parent-pid", type=int, default=0, help=argparse.SUPPRESS)
    parser.add_argument("--install-staged-update", type=Path, help=argparse.SUPPRESS)
    launch = parser.add_mutually_exclusive_group()
    launch.add_argument("--play", action="store_true", help="Play immediately when an account is already signed in")
    launch.add_argument("--host", action="store_true", help="Host immediately when an account is already signed in")
    args = parser.parse_args()
    if args.apply_update:
        try:
            apply_update(args.apply_update, args.parent_pid)
            return 0
        except UpdateError as error:
            messagebox.showerror("Update not installed", str(error))
            return 1
    if args.install_staged_update:
        launch_helper(args.install_staged_update, os.getpid())
        return 0
    preferences_path = args.session_file.parent / "preferences.json"
    preferences = Preferences.load(preferences_path)
    window = tk.Tk()
    window.title("Vegas Reborn Companion")
    window.geometry("900x850")
    window.minsize(820, 620)
    window.configure(background="#10151e")
    style = ttk.Style(window)
    style.theme_use("clam")
    style.configure(".", background="#10151e", foreground="#e9eef6", font=("Segoe UI", 10))
    style.configure("Card.TFrame", background="#1a2230")
    style.configure("Card.TLabel", background="#1a2230")
    style.configure("Muted.TLabel", foreground="#9baabd")
    style.configure("CardMuted.TLabel", foreground="#9baabd", background="#1a2230")
    style.configure("Title.TLabel", font=("Segoe UI", 28, "bold"))
    style.configure("Heading.TLabel", font=("Segoe UI", 13, "bold"), background="#1a2230")
    style.configure("TButton", background="#2b394c", foreground="#eef3fa", padding=(15, 10), borderwidth=0)
    style.map("TButton", background=[("active", "#3a4c65"), ("disabled", "#222c3a")],
              foreground=[("disabled", "#738195")])
    style.configure("Play.TButton", background="#e5b85a", foreground="#10151e", font=("Segoe UI", 12, "bold"), padding=(28, 14))
    style.map("Play.TButton", background=[("active", "#f5cf80"), ("disabled", "#3c3e3e")],
              foreground=[("disabled", "#84909d")])
    style.configure("TEntry", fieldbackground="#101824", foreground="#edf3fa", insertcolor="#edf3fa", padding=8)
    style.configure("TCombobox", fieldbackground="#101824", background="#2b394c", foreground="#edf3fa", padding=7)
    style.map("TCombobox", fieldbackground=[("readonly", "#101824")], foreground=[("readonly", "#edf3fa")])
    style.configure("TCheckbutton", background="#1a2230", padding=(0, 5))
    style.map("TCheckbutton", background=[("active", "#1a2230")])
    style.configure("Horizontal.TProgressbar", background="#e5b85a", troughcolor="#222c3a", borderwidth=0)
    window.option_add("*TCombobox*Listbox.background", "#1a2230")
    window.option_add("*TCombobox*Listbox.foreground", "#edf3fa")
    messages = queue.Queue()
    session, busy = None, False
    account_verified = False
    launch_after_refresh = "play" if args.play else "host" if args.host else None
    hosting, host_stop = False, None
    playing, host_online, game_started_at = False, False, None
    closing = False
    updating, available_release, staged_plan = False, None, None
    host_status = tk.StringVar(value="Dedicated server stopped.")
    status = tk.StringVar(value="Sign in to play and keep your character with your account.")
    account = tk.StringVar(value="Not signed in")
    rank_text = tk.StringVar(value="Sign in to view your saved rank.")
    retail = tk.StringVar(value=preferences.game_folder)
    mode = tk.StringVar(value=preferences.display_mode)
    minimize = tk.BooleanVar(value=preferences.minimize_on_play)
    share_discord = tk.BooleanVar(value=preferences.discord_presence)
    auto_check = tk.BooleanVar(value=preferences.check_updates)
    update_status = tk.StringVar(value="Version " + VERSION + " · Updates are served through GitHub Releases.")
    discord_status = tk.StringVar(value="Open Discord desktop to share your activity")
    try:
        monitors = available_monitors()
    except OSError:
        monitors = []
    monitor_labels = {"Primary monitor (automatic)": ""}
    monitor_labels.update({item.label: item.device for item in monitors})
    monitor_name = tk.StringVar(value=next((label for label, device in monitor_labels.items()
                                           if device == preferences.monitor_device), "Primary monitor (automatic)"))
    viewport = tk.Canvas(window, background="#10151e", highlightthickness=0, borderwidth=0)
    scroll = ttk.Scrollbar(window, orient="vertical", command=viewport.yview)
    scroll.pack(side="right", fill="y")
    viewport.pack(side="left", fill="both", expand=True)
    viewport.configure(yscrollcommand=scroll.set)
    body = ttk.Frame(viewport, padding=(30, 18))
    content = viewport.create_window((0, 0), window=body, anchor="nw")
    body.bind("<Configure>", lambda event: viewport.configure(scrollregion=viewport.bbox("all")))
    viewport.bind("<Configure>", lambda event: viewport.itemconfigure(content, width=event.width))
    def scroll_wheel(event):
        if body.winfo_height() > viewport.winfo_height():
            viewport.yview_scroll(-int(event.delta / 120), "units")
    window.bind("<MouseWheel>", scroll_wheel)
    ttk.Label(body, text="VEGAS REBORN", foreground="#e5b85a", font=("Segoe UI", 10, "bold")).pack(anchor="w")
    ttk.Label(body, text="COMPANION  /  " + VERSION, style="Muted.TLabel", font=("Segoe UI", 9)).pack(anchor="w", pady=(2, 0))
    ttk.Label(body, text="Your next session starts here.", style="Title.TLabel").pack(anchor="w", pady=(3, 0))
    ttk.Label(body, text="Play together. Keep your character. Make Vegas yours.", style="Muted.TLabel").pack(anchor="w", pady=(2, 14))

    def card(title, hint):
        panel = ttk.Frame(body, style="Card.TFrame", padding=(20, 12))
        panel.pack(fill="x", pady=(0, 10))
        ttk.Label(panel, text=title, style="Heading.TLabel").pack(anchor="w")
        ttk.Label(panel, text=hint, style="CardMuted.TLabel").pack(anchor="w", pady=(2, 7))
        return panel

    play_card = card("01   Play Vegas", "The original PC game, with your shared character and progression.")
    buttons = ttk.Frame(play_card, style="Card.TFrame")
    buttons.pack(fill="x")
    ttk.Label(play_card, textvariable=rank_text, style="CardMuted.TLabel", wraplength=740).pack(anchor="w", pady=(7, 0))
    display_card = card("02   Make yourself comfortable", "Display preferences apply the next time you play.")
    display_row = ttk.Frame(display_card, style="Card.TFrame")
    display_row.pack(fill="x")
    mode_box = ttk.Combobox(display_row, textvariable=mode, values=MODES, state="readonly", width=18)
    mode_box.pack(side="left", padx=(0, 10))
    monitor_box = ttk.Combobox(display_row, textvariable=monitor_name, values=list(monitor_labels), state="readonly")
    monitor_box.pack(side="left", fill="x", expand=True)
    ttk.Label(display_card, text="Borderless fills the selected screen and makes switching apps easier.", style="CardMuted.TLabel").pack(anchor="w", pady=(7, 0))
    minimize_box = ttk.Checkbutton(display_card, text="Minimize the companion when the game starts", variable=minimize)
    minimize_box.pack(anchor="w")
    folder_row = ttk.Frame(display_card, style="Card.TFrame")
    folder_row.pack(fill="x", pady=(6, 0))
    folder_entry = ttk.Entry(folder_row, textvariable=retail)
    folder_entry.pack(side="left", fill="x", expand=True, padx=(0, 8))
    def browse():
        selected = filedialog.askdirectory(parent=window, title="Choose your installed Rainbow Six Vegas folder", initialdir=retail.get())
        if selected:
            retail.set(selected)
    browse_button = ttk.Button(folder_row, text="Browseâ€¦", command=browse)
    browse_button.pack(side="left")
    ttk.Label(display_card, text="Installed game folder Â· Your installation is preserved.", style="CardMuted.TLabel").pack(anchor="w", pady=(5, 0))
    host_card = card("03   Bring the squad", "Run a dedicated server while you play, using a separate host identity.")
    host_buttons = ttk.Frame(host_card, style="Card.TFrame")
    host_buttons.pack(fill="x")
    ttk.Label(host_card, textvariable=host_status, style="CardMuted.TLabel", wraplength=740).pack(anchor="w", pady=(7, 0))
    discord_card = card("04   Connected to Discord", "Native desktop activity, using the same Discord app as the UE5 version.")
    ttk.Checkbutton(discord_card, text="Share my Vegas Reborn activity on Discord", variable=share_discord).pack(anchor="w")
    ttk.Label(discord_card, textvariable=discord_status, style="CardMuted.TLabel", wraplength=700).pack(anchor="w", pady=(3, 0))
    ttk.Label(discord_card, text="Retail join invites are not available yet.", style="CardMuted.TLabel").pack(anchor="w", pady=(3, 0))
    update_card = card("Keep Vegas Reborn up to date", "Signed companion updates. Your game copy, character and settings stay in place.")
    ttk.Checkbutton(update_card, text="Check for updates when the companion starts", variable=auto_check).pack(anchor="w")
    ttk.Label(update_card, textvariable=update_status, style="CardMuted.TLabel", wraplength=700).pack(anchor="w", pady=(3, 8))
    update_buttons = ttk.Frame(update_card, style="Card.TFrame")
    update_buttons.pack(fill="x")
    account_row = ttk.Frame(body)
    account_row.pack(fill="x", pady=(0, 10))
    ttk.Label(account_row, textvariable=account, style="Muted.TLabel").pack(side="left")
    identity_card = card("05   Your player name", "Claim a unique shared name: 3â€“16 letters, digits, _ - or .; start with a letter or digit.")
    identity_card.pack_forget()
    claimed_name = tk.StringVar(value="")
    rename_hint = tk.StringVar(value="")
    name_row = ttk.Frame(identity_card, style="Card.TFrame")
    name_row.pack(fill="x")
    name_entry = ttk.Entry(name_row, textvariable=claimed_name)
    name_entry.pack(side="left", fill="x", expand=True, padx=(0, 8))
    ttk.Label(identity_card, textvariable=rename_hint, style="CardMuted.TLabel", wraplength=740).pack(anchor="w", pady=(6, 0))
    activity = ttk.Progressbar(body, mode="indeterminate")
    activity.pack(fill="x", pady=(0, 8))
    ttk.Label(body, textvariable=status, wraplength=760).pack(anchor="w", fill="x")

    def save_preferences():
        Preferences(retail.get().strip(), mode.get(), monitor_labels[monitor_name.get()], minimize.get(), share_discord.get(), auto_check.get()).save(preferences_path)

    def notify(text):
        messages.put(("status", text))

    def update_rank():
        if session is None:
            rank_text.set("Sign in to view your saved rank.")
            return
        try:
            profile = launcher.sync.profile_directory(session) / "R6_PECPEC"
            rank = read_rank(profile, retail_root=retail.get().strip())
            next_step = (f" Â· {rank.experience_to_next:,} XP to the next step"
                         if rank.experience_to_next is not None else " Â· Maximum rank")
            rank_text.set(f"{rank.label.title()} Â· {rank.subrank}/5 Â· {rank.experience:,} XP" + next_step)
        except FileNotFoundError:
            rank_text.set("Sync your character to view your saved rank.")
        except (OSError, ValueError, RankSourceError, TypeError):
            rank_text.set("Rank unavailable: check the selected game folder and saved character.")

    try:
        launcher = RetailLauncher(args.config, notify=notify)
    except Exception:
        messagebox.showerror("Companion setup required", "The companion files are missing or invalid. Extract the complete download again, then retry.")
        window.destroy()
        return 1

    presence = DiscordPresence(notify=lambda text: messages.put(("discord_status", text)))

    def update_worker(operation):
        nonlocal updating
        if updating:
            return
        updating = True
        refresh()
        def worker():
            try:
                operation()
            except Exception as error:
                messages.put(("update_error", str(error) if isinstance(error, UpdateError) else "The update failed. Your current app and character were preserved."))
            finally:
                messages.put(("update_done", None))
        threading.Thread(target=worker, daemon=True, name="VegasUpdateDownload").start()

    def check_updates():
        update_status.set("Checking GitHub Releases…")
        update_worker(lambda: messages.put(("update_checked", check_latest())))

    def apply_staged():
        if busy or hosting or closing:
            update_status.set("Update downloaded. Finish the game and stop hosting, then choose Update and restart.")
            return False
        try:
            launch_helper(staged_plan, os.getpid())
        except UpdateError as error:
            update_status.set(str(error))
            return False
        presence.close()
        window.destroy()
        return True

    def update_and_restart():
        if busy or hosting or updating or not available_release:
            return
        if not getattr(sys, "frozen", False):
            update_status.set("Run the packaged companion to install updates. Source checkouts update through Git.")
            return
        if staged_plan:
            apply_staged()
        else:
            update_status.set("Downloading and verifying " + available_release.version + "…")
            selected = available_release
            update_worker(lambda: messages.put(("update_staged", prepare_update(ROOT, selected))))

    check_update_button = ttk.Button(update_buttons, text="Check for updates", command=check_updates)
    check_update_button.pack(side="left")
    install_update_button = ttk.Button(update_buttons, text="Update and restart", command=update_and_restart)
    install_update_button.pack(side="left", padx=8)

    def start(function):
        nonlocal busy
        if busy:
            return
        busy = True
        activity.start(14)
        refresh()

        def worker():
            try:
                function()
            except SyncConflict:
                messages.put(("conflict", "This PC and the cloud have different characters. Choose which appearance to keep. Your higher XP is preserved either way."))
            except Exception as error:
                # Error types/codes from the companion carry no account secret.
                claim_errors = {"invalid_player_name": "Choose 3â€“16 letters, digits, _ - or .; start with a letter or digit. Reserved names cannot be claimed.",
                    "player_name_taken": "That name is already taken or held for its previous owner. Choose another name.",
                    "player_name_change_too_soon": "Names can change every 30 days. Capitalization or separator changes are allowed sooner.",
                    "account_session_expired_or_revoked": "Your sign-in expired. Sign in with Discord again."}
                message = (claim_errors.get(str(error), "The account operation failed. Retry or sign in again.") if isinstance(error, DiscordClientError)
                    else str(error) if isinstance(error, CompanionError) else "The operation failed. Your local character was preserved. Retry sign-in or synchronization.")
                messages.put(("error", message))
            finally:
                messages.put(("done", None))
        threading.Thread(target=worker, daemon=False).start()

    def persist_session(updated):
        destination = args.session_file
        temporary = destination.with_name(destination.name + ".new")
        temporary.unlink(missing_ok=True)
        save_session(temporary, updated)
        temporary.replace(destination)

    def account_ready(updated):
        return "Choose your player name before playing or hosting." if updated.name_claims_enabled and updated.claim_required else "Ready to play. Steam must be running and signed in."

    def refresh_account():
        selected = session
        if selected is None:
            return
        def operation():
            updated = DiscordClient().refresh_account(selected)
            persist_session(updated)
            messages.put(("session", updated))
            notify(account_ready(updated))
        start(operation)

    def claim():
        selected, name = session, claimed_name.get()
        if selected is None or not selected.name_claims_enabled or busy or hosting:
            return
        def operation():
            updated = DiscordClient().claim_name(selected, name)
            persist_session(updated)
            messages.put(("session", updated))
            notify("Player name saved. " + account_ready(updated))
        start(operation)
    name_button = ttk.Button(name_row, text="Claim name", command=claim)
    name_button.pack(side="left")
    name_entry.bind("<Return>", lambda _event: claim())

    def connect():
        def operation():
            client = DiscordClient()
            link = client.begin_link()
            notify("Approve the Discord sign-in in your browser.")
            webbrowser.open(link.url, new=2)
            linked = client.wait_for_link(link)
            persist_session(linked)
            messages.put(("session", linked))
            notify(account_ready(linked))
        start(operation)

    def play():
        if not account_verified or session is None or (session.name_claims_enabled and session.claim_required):
            return
        selected, source = session, retail.get().strip()
        selected_mode, device = mode.get(), monitor_labels[monitor_name.get()]
        minimize_after_start = minimize.get()
        def operation():
            launcher.play(selected, source=source, display_mode=selected_mode, monitor_device=device,
                          on_started=lambda: messages.put(("game_started", minimize_after_start)))
        start(operation)

    def sync():
        selected = session
        def operation():
            launcher.sync.pull(selected)
            profile = launcher.sync.profile_directory(selected) / "R6_PECPEC"
            if profile.exists():
                launcher.sync.push(selected)
            notify("Cloud character and shared progression are up to date.")
        start(operation)

    connect_button = ttk.Button(account_row, text="Sign in with Discord", command=connect)
    connect_button.pack(side="right")
    refresh_button = ttk.Button(account_row, text="Refresh account", command=refresh_account)
    refresh_button.pack(side="right", padx=8)
    play_button = ttk.Button(buttons, text="Play Vegas   â†’", style="Play.TButton", command=play)
    play_button.pack(side="left", padx=(0, 12))
    sync_button = ttk.Button(buttons, text="Sync character", command=sync)
    sync_button.pack(side="left")
    def open_saves():
        if session:
            folder = launcher.sync.profile_directory(session)
            folder.mkdir(parents=True, exist_ok=True)
            os.startfile(folder)
    saves_button = ttk.Button(buttons, text="Character folder", command=open_saves)
    saves_button.pack(side="left", padx=8)
    def sign_out():
        nonlocal session, account_verified
        try:
            args.session_file.unlink(missing_ok=True)
        except OSError:
            status.set("Could not remove the saved sign-in. Check folder permissions and retry.")
            return
        session = None
        account_verified = False
        account.set("Not signed in")
        update_rank()
        status.set("Signed out on this PC. Your character stays with your account.")
        choices.pack_forget()
        refresh()
    signout_button = ttk.Button(account_row, text="Sign out", command=sign_out)
    signout_button.pack(side="right", padx=8)

    def host():
        nonlocal hosting, host_stop
        if hosting or busy or not session or not account_verified or (session.name_claims_enabled and session.claim_required):
            return
        hosting, host_stop = True, threading.Event()
        selected_source, stop_event = retail.get(), host_stop
        host_status.set("Preparing the dedicated serverâ€¦")
        refresh()
        def worker():
            try:
                from vegas_reborn.dedicated import run_dedicated
                run_dedicated(args.session_file, source=selected_source,
                              config_path=args.config, stop_event=stop_event,
                              notify=lambda text: messages.put(("host_status", text)))
            except Exception:
                messages.put(("host_status", "The dedicated server stopped. Check the installed game folder and Discord sign-in, then retry."))
            finally:
                messages.put(("host_done", None))
        threading.Thread(target=worker, daemon=False).start()

    def stop_host():
        if host_stop:
            host_stop.set()
            host_status.set("Stopping the dedicated serverâ€¦")

    host_button = ttk.Button(host_buttons, text="Host dedicated server", command=host)
    host_button.pack(side="left")
    stop_host_button = ttk.Button(host_buttons, text="Stop server", command=stop_host)
    stop_host_button.pack(side="left", padx=8)
    choices = ttk.Frame(body)
    def resolve(cloud):
        selected = session
        def operation():
            if cloud:
                launcher.sync.pull(selected, replace_local=True)
            else:
                launcher.sync.push(selected, replace_cloud=True)
            messages.put(("resolved", "Character choice saved. You can play now."))
        start(operation)
    ttk.Button(choices, text="Use cloud character", command=lambda: resolve(True)).pack(side="left")
    ttk.Button(choices, text="Keep this PC's character", command=lambda: resolve(False)).pack(side="left", padx=8)

    def refresh():
        check_update_button.configure(state="disabled" if updating or closing else "normal")
        install_update_button.configure(state="normal" if available_release and not (updating or busy or hosting or closing) else "disabled")
        presence.set_activity(activity_for(playing=playing, hosting=host_online,
                                          preparing_host=hosting and not host_online, started=game_started_at)
                              if share_discord.get() else None)
        if closing:
            for button in (connect_button, refresh_button, name_button, play_button, sync_button, host_button, stop_host_button, saves_button, signout_button):
                button.configure(state="disabled")
            return
        connect_button.configure(state="disabled" if busy or hosting else "normal")
        connect_button.configure(text="Switch account" if session else "Sign in with Discord")
        can_play = session and account_verified and not (session.name_claims_enabled and session.claim_required)
        play_button.configure(state="normal" if can_play and not busy else "disabled")
        sync_button.configure(state="normal" if session and not busy else "disabled")
        host_button.configure(state="normal" if can_play and not busy and not hosting else "disabled")
        refresh_button.configure(state="normal" if session and not busy and not hosting else "disabled")
        if session and session.name_claims_enabled:
            if not identity_card.winfo_manager():
                identity_card.pack(fill="x", pady=(0, 10), before=display_card)
            name_button.configure(text="Claim name" if session.claim_required else "Save name", state="normal" if not busy and not hosting else "disabled")
            name_entry.configure(state="disabled" if busy or hosting else "normal")
            rename_hint.set(("Next rename: " + session.name_change_allowed[:10] + ". Capitalization/separator changes remain available.")
                            if session.name_change_allowed else "Names are unique, including look-alike spellings. Old names are held for 14 days after a rename.")
        else:
            identity_card.pack_forget()
        stop_host_button.configure(state="normal" if hosting else "disabled")
        saves_button.configure(state="normal" if session and not busy else "disabled")
        signout_button.configure(state="normal" if session and not busy and not hosting else "disabled")
        folder_entry.configure(state="disabled" if busy or hosting else "normal")
        browse_button.configure(state="disabled" if busy or hosting else "normal")
        mode_box.configure(state="disabled" if busy else "readonly")
        monitor_box.configure(state="readonly" if not busy and mode.get() == "Borderless" else "disabled")

    def poll():
        nonlocal session, busy, hosting, host_stop, account_verified, launch_after_refresh
        nonlocal playing, host_online, game_started_at
        nonlocal updating, available_release, staged_plan
        while True:
            try:
                kind, value = messages.get_nowait()
            except queue.Empty:
                break
            if kind == "update_checked":
                available_release = value
                staged_plan = None
                update_status.set("Version " + value.version + " is available. Update and restart when you are ready." if value
                                  else "Version " + VERSION + " · You are up to date.")
            elif kind == "update_staged":
                staged_plan = value
                if apply_staged():
                    return
            elif kind == "update_done":
                updating = False
            elif kind == "update_error":
                update_status.set(value)
            elif kind == "session":
                session = value
                account_verified = True
                account.set("Signed in as " + (value.claimed_name or value.display_name))
                claimed_name.set(value.claimed_name or "")
                update_rank()
                if value.name_claims_enabled and value.claim_required:
                    window.after_idle(name_entry.focus_set)
            elif kind == "done":
                busy = False
                playing, game_started_at = False, None
                activity.stop()
                update_rank()
                if window.state() == "iconic":
                    window.deiconify()
                if launch_after_refresh is not None:
                    requested, launch_after_refresh = launch_after_refresh, None
                    if account_verified and session and not (session.name_claims_enabled and session.claim_required):
                        window.after(100, play if requested == "play" else host)
            elif kind == "game_started":
                playing, game_started_at = True, time.time()
                if value:
                    window.iconify()
            elif kind == "discord_status":
                discord_status.set(value)
            elif kind == "host_status":
                host_status.set(value)
                host_online = value.startswith("Dedicated server running.")
            elif kind == "host_done":
                hosting, host_stop = False, None
                host_online = False
            elif kind == "conflict":
                status.set(value)
                choices.pack(fill="x", pady=8)
            elif kind == "resolved":
                status.set(value)
                choices.pack_forget()
            else:
                status.set(value)
        refresh()
        if closing and not hosting and not busy:
            presence.close()
            window.destroy()
            return
        window.after(100, poll)

    def close():
        nonlocal closing
        if busy:
            status.set("Keep the companion open while the game or account operation is running. It saves your character when the game closes.")
            return
        if hosting:
            closing = True
            stop_host()
            status.set("Stopping the dedicated server before closingâ€¦")
            return
        presence.close()
        window.destroy()

    def preference_changed(*_):
        try:
            save_preferences()
        except OSError:
            status.set("Preferences could not be saved. Check that the companion folder is writable.")
    for variable in (retail, mode, monitor_name, minimize, share_discord, auto_check):
        variable.trace_add("write", preference_changed)
    retail.trace_add("write", lambda *_: update_rank())

    try:
        session = load_session(args.session_file)
        account.set("Signed in as " + session.display_name)
        status.set("Checking your shared accountâ€¦")
    except Exception:
        pass
    window.protocol("WM_DELETE_WINDOW", close)
    poll()
    if session:
        window.after(200, refresh_account)
    if auto_check.get() and getattr(sys, "frozen", False):
        window.after(1000, check_updates)
    try:
        window.mainloop()
    finally:
        presence.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
