"""Render only this tool's own synthetic Tk window; no desktop/game capture."""
import argparse
import ctypes as c
from ctypes import wintypes as w
from pathlib import Path
import sys
import tkinter as tk
from PIL import Image

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/"src"))
from vegas_reborn.application import AppState
from vegas_reborn.companion_settings import Preferences
from vegas_reborn.discord_client import AccountSession
from vegas_reborn.ui.view import CompanionView,UiValues


def render_window(root,path):
    u,g=c.WinDLL("user32"),c.WinDLL("gdi32")
    u.GetParent.argtypes=[w.HWND];u.GetParent.restype=w.HWND
    u.GetWindowRect.argtypes=[w.HWND,c.POINTER(w.RECT)]
    u.GetWindowDC.argtypes=[w.HWND];u.GetWindowDC.restype=w.HDC
    u.ReleaseDC.argtypes=[w.HWND,w.HDC]
    u.PrintWindow.argtypes=[w.HWND,w.HDC,w.UINT]
    g.CreateCompatibleDC.argtypes=[w.HDC];g.CreateCompatibleDC.restype=w.HDC
    g.CreateCompatibleBitmap.argtypes=[w.HDC,c.c_int,c.c_int];g.CreateCompatibleBitmap.restype=w.HBITMAP
    g.SelectObject.argtypes=[w.HDC,w.HANDLE];g.SelectObject.restype=w.HANDLE
    g.DeleteObject.argtypes=[w.HANDLE];g.DeleteDC.argtypes=[w.HDC]
    g.GetDIBits.argtypes=[w.HDC,w.HBITMAP,w.UINT,w.UINT,c.c_void_p,c.c_void_p,w.UINT]
    hwnd=u.GetParent(root.winfo_id())
    rect=w.RECT();u.GetWindowRect(hwnd,c.byref(rect))
    width,height=rect.right-rect.left,rect.bottom-rect.top
    dc=u.GetWindowDC(hwnd);memory=g.CreateCompatibleDC(dc);bitmap=g.CreateCompatibleBitmap(dc,width,height)
    old=g.SelectObject(memory,bitmap)
    class Header(c.Structure):
        _fields_=[("size",w.DWORD),("width",w.LONG),("height",w.LONG),("planes",w.WORD),("bits",w.WORD),
                  ("compression",w.DWORD),("image_bytes",w.DWORD),("x",w.LONG),("y",w.LONG),("used",w.DWORD),("important",w.DWORD)]
    try:
        if not u.PrintWindow(hwnd,memory,2):
            raise RuntimeError("The own-window render did not complete")
        header=Header(c.sizeof(Header),width,-height,1,32,0,width*height*4,0,0,0,0)
        data=c.create_string_buffer(width*height*4)
        if not g.GetDIBits(memory,bitmap,0,height,data,c.byref(header),0):
            raise RuntimeError("Could not read the own-window render buffer")
        Image.frombytes("RGB",(width,height),data.raw,"raw","BGRX").save(path)
    finally:
        g.SelectObject(memory,old);g.DeleteObject(bitmap);g.DeleteDC(memory);u.ReleaseDC(hwnd,dc)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output",type=Path,default=ROOT/"local/design-preview")
    parser.add_argument("--compact",action="store_true")
    args=parser.parse_args();args.output.mkdir(parents=True,exist_ok=True)
    root=tk.Tk();root.withdraw()
    values=UiValues(root,Preferences(),[])
    actions={name:lambda:None for name in ("launch","connect","refresh","signout","claim","sync","saves","browse","host","stop_host","check_updates","update","cloud","local","get_discord","release_notes")}
    view=CompanionView(root,values,actions)
    values.account.set("Signed in as Operator")
    values.rank.set("Your saved character\nShared progression")
    values.status.set("Ready to play. Steam must be running and signed in.")
    state=AppState(session=AccountSession("199999999999999999","Operator","a"*64,"Operator",True,False),account_verified=True)
    view.render(state)
    root.geometry(("940x640" if args.compact else "1200x840")+"+80+80")
    root.deiconify();root.update()
    try:
        for page in view.pages:
            view.show_page(page);root.update()
            render_window(root,args.output/(page+".png"))
        print("Rendered pages at one fixed window size using synthetic UI state:",args.output.resolve())
    finally:
        root.destroy()


if __name__=="__main__":main()
