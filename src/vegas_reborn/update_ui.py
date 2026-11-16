"""Visible progress for the staged updater after the main companion exits."""
import queue
import threading
import tkinter as tk
from tkinter import ttk, messagebox

from .updater import UpdateError, apply_update


def run_update(plan, parent_pid):
    root=tk.Tk()
    root.title("Updating Vegas Reborn")
    root.geometry("480x180")
    root.resizable(False,False)
    from .ui.theme import apply_theme
    from .ui.graphics import Graphics
    graphics=Graphics(root)
    apply_theme(root,graphics)
    panel=ttk.Frame(root,padding=24)
    panel.pack(fill="both",expand=True)
    status=tk.StringVar(root,value="Preparing the update...")
    ttk.Label(panel,text="Updating Vegas Reborn",font=("Segoe UI",14,"bold")).pack(anchor="w")
    ttk.Label(panel,textvariable=status,wraplength=425).pack(anchor="w",pady=(12,12))
    activity=ttk.Progressbar(panel,mode="indeterminate")
    activity.pack(fill="x");activity.start(14)
    messages=queue.Queue()
    result=[1]
    root.protocol("WM_DELETE_WINDOW",lambda:None)
    def worker():
        try:
            apply_update(plan,parent_pid,progress=lambda text:messages.put(("progress",text)))
            messages.put(("done",None))
        except Exception as error:
            messages.put(("error",str(error) if isinstance(error,UpdateError)
                          else "The update could not finish. Open Vegas Reborn again to retry."))
    def poll():
        # Bound each drain so progress events cannot starve the window's paint.
        for _ in range(100):
            try:kind,value=messages.get_nowait()
            except queue.Empty:break
            if kind=="progress":status.set(value)
            elif kind=="done":
                activity.stop();result[0]=0;root.destroy();return
            else:
                activity.stop()
                status.set("Update not completed.")
                messagebox.showerror("Update not completed",value,parent=root)
                root.destroy();return
        root.after(40,poll)
    threading.Thread(target=worker,daemon=True,name="VegasUpdateInstall").start()
    root.after(40,poll)
    root.mainloop()
    return result[0]
