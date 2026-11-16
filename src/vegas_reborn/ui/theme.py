"""Design tokens, rounded native ttk elements, and accessible focus states."""
import tkinter as tk
from tkinter import ttk
from PIL import Image, ImageDraw, ImageTk

BG = "#111615"
SIDEBAR = "#151b19"
PANEL = "#1b2220"
LINE = "#303a35"
TEXT = "#f1f0e8"
MUTED = "#a4b0a7"
ACCENT = "#efbd73"
GREEN = "#8ebda5"


def apply_theme(root, graphics):
    root.configure(background=BG)
    style = ttk.Style(root)
    style.theme_use("clam")
    style.configure(".", font=("Segoe UI",10), background=BG, foreground=TEXT)
    for name,color in (("TFrame",BG),("Panel.TFrame",PANEL),("Side.TFrame",SIDEBAR)):
        style.configure(name,background=color)
    for name,bg,fg,font in (
        ("TLabel",BG,TEXT,("Segoe UI",10)),
        ("Muted.TLabel",BG,MUTED,("Segoe UI",10)),
        ("Panel.TLabel",PANEL,TEXT,("Segoe UI",10)),
        ("PanelMuted.TLabel",PANEL,MUTED,("Segoe UI",10)),
        ("Heading.TLabel",PANEL,TEXT,("Segoe UI",14,"bold")),
        ("PageTitle.TLabel",BG,TEXT,("Segoe UI",23,"bold")),
        ("Eyebrow.TLabel",BG,ACCENT,("Segoe UI",9,"bold")),
        ("Side.TLabel",SIDEBAR,MUTED,("Segoe UI",9)),
        ("SideBrand.TLabel",SIDEBAR,TEXT,("Segoe UI",13,"bold")),
        ("Rank.TLabel",PANEL,ACCENT,("Segoe UI",17,"bold")),
    ):
        style.configure(name,background=bg,foreground=fg,font=font)
    # Image elements retain ttk keyboard/focus/disabled semantics while using
    # a consistent rounded shape instead of the platform's stock rectangles.
    references = []
    def border(fill, outline=None):
        image=Image.new("RGBA",(128,128))
        d=ImageDraw.Draw(image)
        d.rounded_rectangle((2,2,125,125),radius=28,fill=fill,outline=outline,width=4)
        photo=ImageTk.PhotoImage(image.resize((32,32),Image.Resampling.LANCZOS),master=root)
        references.append(photo)
        return photo
    for name,normal,hover,pressed,fg,outline in (
        ("Primary",ACCENT,"#ffcf89","#dba55d","#1b201b",None),
        ("Secondary","#263029","#333f36","#1c251f",TEXT,LINE),
        ("Quiet",PANEL,"#29342d","#202922",MUTED,None),
        ("Nav",SIDEBAR,"#202b24","#26342a",MUTED,None),
        ("SelectedNav","#30382a","#3a4331","#30382a",ACCENT,None),
        ("Danger","#302320","#44302b","#36211c","#e8a093",None),
    ):
        photos=[border(normal,outline),border(hover,outline),border(pressed,ACCENT),border("#242c26"),border(normal,ACCENT)]
        element=name+".border"
        style.element_create(element,"image",photos[0],("disabled",photos[3]),("pressed",photos[2]),("active",photos[1]),("focus",photos[4]),
                             border=10,sticky="nsew")
        layout=[(element,{"sticky":"nsew","children":[
            ("Button.padding",{"sticky":"nsew","children":[("Button.label",{"sticky":"nsew"})]})]})]
        style.layout(name+".TButton",layout)
        style.configure(name+".TButton",padding=(15,11),foreground=fg,anchor="center",borderwidth=0,
                        focuscolor=ACCENT,focusthickness=1,font=("Segoe UI",10,"bold"))
        surface=SIDEBAR if name in ("Nav","SelectedNav") else PANEL
        # Transparent rounded corners expose the widget background. Override
        # clam's inherited light active/disabled maps as well as normal fill.
        style.configure(name+".TButton",background=surface,relief="flat")
        style.map(name+".TButton",background=[("disabled",surface),("pressed",surface),("active",surface)],
                  relief=[("disabled","flat"),("pressed","flat"),("active","flat")])
        style.map(name+".TButton",foreground=[("disabled","#667369")])
    for name in ("Nav","SelectedNav"):
        style.configure(name+".TButton",anchor="w",padding=(14,12),font=("Segoe UI",10))
    style.configure("Primary.TButton",padding=(24,13),font=("Segoe UI",11,"bold"))
    style.configure("TEntry",fieldbackground="#111713",foreground=TEXT,insertcolor=TEXT,padding=11,
                    bordercolor=LINE,lightcolor=LINE,darkcolor=LINE)
    style.map("TEntry",bordercolor=[("focus",ACCENT)],lightcolor=[("focus",ACCENT)],darkcolor=[("focus",ACCENT)])
    style.configure("TCombobox",fieldbackground="#111713",background="#29352c",foreground=TEXT,padding=10,
                    bordercolor=LINE,arrowcolor=MUTED,lightcolor=LINE,darkcolor=LINE)
    style.map("TCombobox",fieldbackground=[("readonly","#111713"),("disabled","#202822")],
              foreground=[("disabled","#667369"),("readonly",TEXT)])
    switches=[]
    for on in (False,True):
        image=Image.new("RGBA",(160,88));d=ImageDraw.Draw(image)
        d.rounded_rectangle((4,8,155,79),radius=36,fill=ACCENT if on else "#455047")
        x=112 if on else 46
        d.ellipse((x-25,19,x+25,69),fill="#192119" if on else "#c3ccc2")
        switches.append(ImageTk.PhotoImage(image.resize((40,22),Image.Resampling.LANCZOS),master=root))
    references.extend(switches)
    style.element_create("Switch.indicator","image",switches[0],("selected",switches[1]),sticky="w")
    style.layout("Switch.TCheckbutton",[("Checkbutton.padding",{"sticky":"nsew","children":[
        ("Switch.indicator",{"side":"left","sticky":"w"}),
        ("Checkbutton.focus",{"side":"left","sticky":"w","children":[("Checkbutton.label",{"sticky":"w"})]})]})])
    style.configure("Switch.TCheckbutton",background=PANEL,foreground=TEXT,padding=(0,5),font=("Segoe UI",10),focuscolor=ACCENT)
    style.map("Switch.TCheckbutton",background=[("active",PANEL)],foreground=[("disabled","#667369")])
    style.configure("Horizontal.TProgressbar",background=ACCENT,troughcolor=BG,borderwidth=0,lightcolor=ACCENT,darkcolor=ACCENT)
    style.configure("Vertical.TScrollbar",background=LINE,troughcolor=BG,borderwidth=0,arrowcolor=MUTED,
                    lightcolor=LINE,darkcolor=LINE,bordercolor=BG,arrowsize=10,gripcount=0)
    root.option_add("*TCombobox*Listbox.background",PANEL)
    root.option_add("*TCombobox*Listbox.foreground",TEXT)
    root.option_add("*TCombobox*Listbox.selectBackground","#46533e")
    root.iconphoto(True,graphics.mark(64))
    root._theme_images=references
    return style
