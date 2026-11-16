"""CLI and signed-update routing, independent of desktop presentation."""
import argparse
import os
from pathlib import Path
import sys
import tkinter as tk
from tkinter import messagebox
from .updater import UpdateError, apply_update, launch_helper

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
        try:
            apply_update(args.apply_update,args.parent_pid)
            return 0
        except UpdateError as error:
            messagebox.showerror("Update not installed",str(error))
            return 1
    if args.install_staged_update:
        launch_helper(args.install_staged_update,os.getpid())
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
