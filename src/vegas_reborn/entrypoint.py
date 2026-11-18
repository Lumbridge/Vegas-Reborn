"""CLI and signed-update routing, independent of desktop presentation."""
import argparse
import os
from pathlib import Path
import sys
import tkinter as tk
from tkinter import messagebox
from .updater import launch_helper

ROOT=Path(sys.executable).resolve().parent if getattr(sys,"frozen",False) else Path(__file__).resolve().parents[2]


def main():
    for name in ("stdout","stderr"):
        if getattr(sys,name) is None:
            setattr(sys,name,open(os.devnull,"w"))
    parser=argparse.ArgumentParser(description="Vegas Reborn companion for the original PC Rainbow Six Vegas")
    parser.add_argument("--config",type=Path,default=ROOT/"local/companion/service.json")
    parser.add_argument("--session-file",type=Path,default=ROOT/"local/companion/account-session.json")
    parser.add_argument("--apply-update",type=Path,help=argparse.SUPPRESS)
    parser.add_argument("--parent-pid",type=int,default=0,help=argparse.SUPPRESS)
    parser.add_argument("--install-staged-update",type=Path,help=argparse.SUPPRESS)
    launch=parser.add_mutually_exclusive_group()
    launch.add_argument("--play",action="store_true",help="Play after checking the saved account")
    launch.add_argument("--host",action="store_true",help="Host after checking the saved account")
    args=parser.parse_args()
    if args.apply_update:
        from .update_ui import run_update
        return run_update(args.apply_update,args.parent_pid)
    if args.install_staged_update:
        launch_helper(args.install_staged_update,os.getpid())
        return 0
    # A first-time visitor may have downloaded the updater ZIP from the website.
    # Handle that case before asking for an account or creating local game data.
    if (ROOT/"runtime-0.4.14/updater-only").is_file() and not any(
        (ROOT/"local/companion/bin"/name).exists()
        for name in ("VegasInjector.exe", "VegasSteamGate.dll", "VegasSteamControl.exe")
    ):
        root=tk.Tk()
        root.withdraw()
        try:
            open_download=messagebox.askyesno("Install Vegas Reborn",
                "For first-time setup, extract the full 0.4.13 Windows download into a new folder. "
                "Open that Companion and choose Updates to install 0.4.14.\n\nOpen the full download page?",
                parent=root)
            if open_download:
                import webbrowser
                webbrowser.open("https://github.com/Lumbridge/Vegas-Reborn/releases/tag/v0.4.13")
        finally:
            root.destroy()
        return 0
    # Importing the full UI is unnecessary for help or update-helper runs.
    from .application import CompanionController, CompanionSetupError
    root=tk.Tk()
    try:
        controller=CompanionController(root,args)
    except (CompanionSetupError,FileNotFoundError):
        messagebox.showerror("Companion setup required","The companion files are missing or invalid. Extract the complete download again, then retry.",parent=root)
        root.destroy()
        return 1
    return controller.run()
