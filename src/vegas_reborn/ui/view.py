"""Six focused pages with persistent navigation and session status."""
import tkinter as tk
from tkinter import ttk
from PIL import Image, ImageOps, ImageTk
from .graphics import ASSETS, Graphics
from .theme import BG, SIDEBAR, PANEL, LINE, TEXT, MUTED, ACCENT, GREEN, apply_theme
from ..display import MODES
from ..version import VERSION


class UiValues:
    def __init__(self, root, preferences, monitors):
        def text(value):
            return tk.StringVar(root,value=value)
        self.account=text("Not signed in")
        self.rank=text("Sign in to view character data.")
        self.status=text("Sign in to launch the game.")
        self.host_status=text("Your dedicated server is offline.")
        self.discord_status=text("Open Discord desktop to connect.")
        self.update_status=text("Version " + VERSION + " is installed.")
        self.retail=text(preferences.game_folder)
        self.mode=text(preferences.display_mode)
        self.minimize=tk.BooleanVar(root,value=preferences.minimize_on_play)
        self.share_discord=tk.BooleanVar(root,value=preferences.discord_presence)
        self.auto_check=tk.BooleanVar(root,value=preferences.check_updates)
        self.claimed_name=text("")
        self.rename_hint=text("")
        self.monitor_labels={"Primary monitor (automatic)":""}
        self.monitor_labels.update({monitor.label:monitor.device for monitor in monitors})
        selected=next((label for label,device in self.monitor_labels.items() if device==preferences.monitor_device),"Primary monitor (automatic)")
        self.monitor_name=text(selected)
        self.display_summary=text(preferences.display_mode)
        self.launch_hint=text("Sign in to continue")


class ScrollPage(ttk.Frame):
    def __init__(self, parent):
        super().__init__(parent)
        self.canvas=tk.Canvas(self,background=BG,highlightthickness=0,borderwidth=0,yscrollincrement=22)
        self.scroll=ttk.Scrollbar(self,orient="vertical",command=self.canvas.yview)
        self.canvas.pack(side="left",fill="both",expand=True)
        self.canvas.configure(yscrollcommand=self._scroll_changed)
        self.body=ttk.Frame(self.canvas,padding=(28,0,22,24))
        self.item=self.canvas.create_window((0,0),window=self.body,anchor="nw")
        self.body.bind("<Configure>",lambda event:self.canvas.configure(scrollregion=self.canvas.bbox("all")))
        self.canvas.bind("<Configure>",lambda event:self.canvas.itemconfigure(self.item,width=event.width))

    def wheel(self,event):
        if self.body.winfo_height()>self.canvas.winfo_height():
            self.canvas.yview_scroll(-int(event.delta/120),"units")

    def _scroll_changed(self,start,end):
        self.scroll.set(start,end)
        if float(start)<=0 and float(end)>=1:
            self.scroll.pack_forget()
        elif not self.scroll.winfo_manager():
            self.scroll.pack(side="right",fill="y",before=self.canvas)


class CompanionView:
    PAGES=(
        ("overview","Overview","home","Launch the game and view your settings."),
        ("account","Account","account","Manage your account, player name and character data."),
        ("display","Display & game","display","Game location, screen mode and launch preferences."),
        ("connections","Connections","connections","Manage Discord activity sharing."),
        ("updates","Updates","updates","Check for companion updates."),
    )
    HOST_PAGE=("host","Dedicated server","host","Separate dedicated-server process controls.")

    def __init__(self, root, values, actions):
        self.root,self.values,self.actions=root,values,actions
        self.graphics=Graphics(root)
        self.style=apply_theme(root,self.graphics)
        self.current_page="overview"
        self.nav_buttons={}
        self.pages={}
        self.widgets={}
        root.title("Vegas Reborn")
        width=min(1200,max(960,root.winfo_screenwidth()-100))
        height=min(840,max(650,root.winfo_screenheight()-100))
        root.geometry(f"{width}x{height}")
        root.minsize(940,640)
        root.pack_propagate(False)
        sidebar=ttk.Frame(root,style="Side.TFrame",width=220,padding=(15,24,15,18))
        sidebar.pack(side="left",fill="y");sidebar.pack_propagate(False)
        brand=ttk.Frame(sidebar,style="Side.TFrame")
        brand.pack(fill="x",pady=(0,32))
        ttk.Label(brand,image=self.graphics.mark(40),background=SIDEBAR).pack(side="left",padx=(0,9))
        ttk.Label(brand,text="VEGAS\nREBORN",style="SideBrand.TLabel",justify="left").pack(side="left")
        ttk.Label(sidebar,text="COMPANION",style="Side.TLabel",font=("Segoe UI",8,"bold")).pack(anchor="w",padx=12,pady=(0,10))
        for index,(key,label,icon,hint) in enumerate(self.PAGES):
            button=ttk.Button(sidebar,text="  "+label,image=self.graphics.icon(icon),compound="left",
                              style="Nav.TButton",command=lambda page=key:self.show_page(page))
            button.pack(fill="x",pady=3)
            self.nav_buttons[key]=button
            root.bind(f"<Alt-Key-{index+1}>",lambda event,page=key:self.show_page(page))
        bottom=ttk.Frame(sidebar,style="Side.TFrame")
        bottom.pack(side="bottom",fill="x")
        tk.Frame(bottom,background=LINE,height=1).pack(fill="x",pady=(0,16))
        ttk.Label(bottom,text="ORIGINAL PC EDITION",style="Side.TLabel",font=("Segoe UI",8,"bold")).pack(anchor="w",padx=10)
        ttk.Label(bottom,text="Companion "+VERSION,style="Side.TLabel").pack(anchor="w",padx=10,pady=(4,0))
        ttk.Label(bottom,text="Alt + 1–5 to navigate",style="Side.TLabel",font=("Segoe UI",8)).pack(anchor="w",padx=10,pady=(15,0))
        main=ttk.Frame(root)
        main.pack(side="left",fill="both",expand=True)
        top=ttk.Frame(main,padding=(28,22,28,18))
        top.pack(fill="x")
        titles=ttk.Frame(top);titles.pack(side="left",fill="x",expand=True)
        self.page_title=ttk.Label(titles,text="Overview",style="PageTitle.TLabel")
        self.page_title.pack(anchor="w")
        self.page_hint=ttk.Label(titles,text=self.PAGES[0][3],style="Muted.TLabel")
        self.page_hint.pack(anchor="w",pady=(4,0))
        self.account_chip=ttk.Button(top,text="  Sign in",image=self.graphics.icon("account",18),compound="left",
                                     style="Quiet.TButton",command=lambda:self.show_page("account"))
        self.account_chip.pack(side="right",padx=(18,0))
        footer=ttk.Frame(main,padding=(28,12,28,14))
        footer.pack(side="bottom",fill="x")
        self.activity=ttk.Progressbar(footer,mode="indeterminate",length=90)
        status_label=ttk.Label(footer,textvariable=values.status,style="Muted.TLabel",wraplength=680)
        status_label.pack(side="left",fill="x",expand=True)
        footer.bind("<Configure>",lambda event:status_label.configure(wraplength=max(260,event.width-170)))
        tk.Frame(main,background=LINE,height=1).pack(side="bottom",fill="x",padx=28)
        self.page_host=ttk.Frame(main);self.page_host.pack(fill="both",expand=True)
        self.page_host.grid_propagate(False)
        self.page_host.rowconfigure(0,weight=1)
        self.page_host.columnconfigure(0,weight=1)
        for key,*_ in self.PAGES:
            self.pages[key]=ScrollPage(self.page_host)
        # The legacy --host path remains accessible for existing shortcuts,
        # but dedicated hosting is not part of the normal navigation.
        self.pages["host"]=ScrollPage(self.page_host)
        for page in self.pages.values():
            page.grid(row=0,column=0,sticky="nsew")
        self._overview()
        self._account()
        self._display()
        self._host()
        self._connections()
        self._updates()
        root.bind("<MouseWheel>",lambda event:self.pages[self.current_page].wheel(event))
        self.show_page("overview")
        root.after_idle(self._dark_caption)

    def button(self,parent,key,label,action,icon=None,style="Secondary.TButton",side="left"):
        kwargs={"text":label,"style":style,"command":action}
        if icon:
            kwargs.update(image=(self.graphics.icon(icon,18,"#1b201b" if style=="Primary.TButton" else TEXT),
                                 "disabled",self.graphics.icon(icon,18,"#667369")),compound="left")
        button=ttk.Button(parent,**kwargs)
        button.pack(side=side,padx=(0,9))
        self.widgets[key]=button
        return button

    def panel(self,parent,title=None,hint=None):
        frame=tk.Frame(parent,background=PANEL,highlightbackground=LINE,highlightthickness=1,borderwidth=0)
        frame.pack(fill="x",pady=(0,16))
        inner=ttk.Frame(frame,style="Panel.TFrame",padding=22)
        inner.pack(fill="both",expand=True)
        if title:
            ttk.Label(inner,text=title,style="Heading.TLabel").pack(anchor="w")
        if hint:
            label=ttk.Label(inner,text=hint,style="PanelMuted.TLabel",wraplength=740)
            label.pack(anchor="w",pady=(6,16))
            inner.bind("<Configure>",lambda event:label.configure(wraplength=max(200,event.width-44)),add="+")
        return frame,inner

    def _overview(self):
        body=self.pages["overview"].body
        self.hero=tk.Canvas(body,height=310,highlightthickness=1,highlightbackground=LINE,background=PANEL,borderwidth=0)
        self.hero.pack(fill="x",pady=(0,20))
        with Image.open(ASSETS/"vegas-night.png") as source:
            self.hero_source=source.convert("RGB")
        self.hero_image_item=self.hero.create_image(0,0,anchor="nw")
        self.hero.create_text(30,32,text="ORIGINAL PC GAME",anchor="nw",fill=ACCENT,font=("Segoe UI",9,"bold"))
        self.hero.create_text(28,60,text="Vegas Reborn",anchor="nw",fill=TEXT,font=("Segoe UI",35,"bold"))
        self.hero.create_text(30,128,text="Rainbow Six Vegas\nSteam is required to launch.",anchor="nw",fill="#c3ccc5",font=("Segoe UI",11),width=390)
        self.hero_buttons=ttk.Frame(self.hero,style="Panel.TFrame")
        self.primary_button=self.button(self.hero_buttons,"play","Launch game",self.actions["launch"],"play","Primary.TButton")
        self.hero.create_window(30,198,window=self.hero_buttons,anchor="nw")
        self.hero.create_text(32,276,text="STEAM REQUIRED  /  ORIGINAL PC GAME",anchor="nw",fill="#a5b1a8",font=("Segoe UI",8,"bold"))
        self.hero.bind("<Configure>",self._resize_hero)
        summary=ttk.Frame(body);summary.pack(fill="x")
        summary.columnconfigure(0,weight=1,uniform="summary");summary.columnconfigure(1,weight=1,uniform="summary")
        for column,title,icon,var,link,page in (
            (0,"CHARACTER","shield",self.values.rank,"Account settings","account"),
            (1,"DISPLAY SETTINGS","display",self.values.display_summary,"Display settings","display"),
        ):
            frame=tk.Frame(summary,background=PANEL,highlightbackground=LINE,highlightthickness=1)
            frame.grid(row=0,column=column,sticky="nsew",padx=(0,8) if column==0 else (8,0))
            inner=ttk.Frame(frame,style="Panel.TFrame",padding=20);inner.pack(fill="both",expand=True)
            heading=ttk.Frame(inner,style="Panel.TFrame");heading.pack(fill="x",pady=(0,13))
            ttk.Label(heading,image=self.graphics.icon(icon,20,ACCENT),background=PANEL).pack(side="left",padx=(0,9))
            ttk.Label(heading,text=title,style="PanelMuted.TLabel",font=("Segoe UI",8,"bold")).pack(side="left")
            label=ttk.Label(inner,textvariable=var,style="Panel.TLabel",wraplength=310,font=("Segoe UI",11))
            label.pack(anchor="w",fill="x",pady=(0,12))
            self.button(inner,"summary_"+page,link,lambda value=page:self.show_page(value),"arrow","Quiet.TButton")
            inner.bind("<Configure>",lambda event,item=label:item.configure(wraplength=max(140,event.width-40)),add="+")

    def _resize_hero(self,event):
        if event.width<2 or event.width==getattr(self,"_hero_width",None):
            return
        self._hero_width=event.width
        image=ImageOps.fit(self.hero_source,(event.width,310),Image.Resampling.LANCZOS,centering=(.5,.72))
        self.hero_photo=ImageTk.PhotoImage(image,master=self.root)
        self.hero.itemconfigure(self.hero_image_item,image=self.hero_photo)

    def _account(self):
        body=self.pages["account"].body
        _,inner=self.panel(body,"Account","Sign in to synchronize character data and progression.")
        ttk.Label(inner,textvariable=self.values.account,style="Rank.TLabel").pack(anchor="w",pady=(0,8))
        ttk.Label(inner,textvariable=self.values.rank,style="PanelMuted.TLabel",wraplength=700).pack(anchor="w",pady=(0,20))
        row=ttk.Frame(inner,style="Panel.TFrame");row.pack(fill="x")
        self.button(row,"connect","Sign in with Discord",self.actions["connect"],"account","Primary.TButton")
        self.button(row,"refresh","Refresh account",self.actions["refresh"],"sync")
        self.button(row,"signout","Sign out",self.actions["signout"],"logout","Quiet.TButton")
        self.identity_card,name=self.panel(body,"Player name","Shared with the UE5 version. Use 3–16 letters, digits, _ - or .; start with a letter or digit.")
        row=ttk.Frame(name,style="Panel.TFrame");row.pack(fill="x")
        self.widgets["name_entry"]=ttk.Entry(row,textvariable=self.values.claimed_name)
        self.widgets["name_entry"].pack(side="left",fill="x",expand=True,padx=(0,12))
        self.button(row,"claim","Claim name",self.actions["claim"],style="Primary.TButton")
        self.widgets["name_entry"].bind("<Return>",lambda event:self.actions["claim"]())
        ttk.Label(name,textvariable=self.values.rename_hint,style="PanelMuted.TLabel",wraplength=700).pack(anchor="w",pady=(12,0))
        self.identity_card.pack_forget()
        self.character_card,saves=self.panel(body,"Character synchronization","Synchronize character data or open the local save folder.")
        row=ttk.Frame(saves,style="Panel.TFrame");row.pack(fill="x")
        self.button(row,"sync","Sync character",self.actions["sync"],"sync")
        self.button(row,"saves","Character folder",self.actions["saves"],"folder","Quiet.TButton")
        self.conflict_card,conflict=self.panel(body,"Character conflict","This PC and the cloud have different characters. Your higher XP is preserved either way.")
        row=ttk.Frame(conflict,style="Panel.TFrame");row.pack(fill="x")
        self.button(row,"cloud","Use cloud character",self.actions["cloud"],style="Primary.TButton")
        self.button(row,"local","Keep this PC's character",self.actions["local"])
        self.conflict_card.pack_forget()

    def _display(self):
        body=self.pages["display"].body
        _,inner=self.panel(body,"Display settings","These preferences apply the next time you launch the game.")
        fields=ttk.Frame(inner,style="Panel.TFrame");fields.pack(fill="x")
        fields.columnconfigure(0,weight=1);fields.columnconfigure(1,weight=2)
        ttk.Label(fields,text="Display mode",style="PanelMuted.TLabel").grid(row=0,column=0,sticky="w",pady=(0,7))
        ttk.Label(fields,text="Monitor",style="PanelMuted.TLabel").grid(row=0,column=1,sticky="w",padx=(14,0),pady=(0,7))
        mode=ttk.Combobox(fields,textvariable=self.values.mode,values=MODES,state="readonly",width=18)
        mode.grid(row=1,column=0,sticky="ew")
        monitor=ttk.Combobox(fields,textvariable=self.values.monitor_name,values=list(self.values.monitor_labels),state="readonly")
        monitor.grid(row=1,column=1,sticky="ew",padx=(14,0))
        self.widgets.update(mode=mode,monitor=monitor)
        ttk.Label(inner,text="Borderless fills your screen while letting other apps sit above the game.",style="PanelMuted.TLabel",wraplength=700).pack(anchor="w",pady=(14,18))
        ttk.Checkbutton(inner,text="Minimize the companion when the game starts",variable=self.values.minimize,style="Switch.TCheckbutton").pack(anchor="w")
        _,inner=self.panel(body,"Game folder","Choose the installed Rainbow Six Vegas folder containing Binaries and KellerGame.")
        row=ttk.Frame(inner,style="Panel.TFrame");row.pack(fill="x")
        self.widgets["folder_entry"]=ttk.Entry(row,textvariable=self.values.retail)
        self.widgets["folder_entry"].pack(side="left",fill="x",expand=True,padx=(0,12))
        self.button(row,"browse","Browse",self.actions["browse"],"folder")
        ttk.Label(inner,text="Your installed game is preserved. Vegas Reborn prepares its own copy.",style="PanelMuted.TLabel",wraplength=700).pack(anchor="w",pady=(14,0))

    def _host(self):
        body=self.pages["host"].body
        _,inner=self.panel(body,"Dedicated server","Start the original dedicated server, then find it in Online > Custom Match.")
        ttk.Label(inner,image=self.graphics.icon("host",64,ACCENT),background=PANEL).pack(anchor="w",pady=(3,20))
        ttk.Label(inner,textvariable=self.values.host_status,style="Panel.TLabel",font=("Segoe UI",12),wraplength=700).pack(anchor="w",pady=(0,20))
        row=ttk.Frame(inner,style="Panel.TFrame");row.pack(fill="x")
        self.button(row,"host","Host server",self.actions["host"],"play","Primary.TButton")
        self.button(row,"stop_host","Stop server",self.actions["stop_host"],"stop","Danger.TButton")
        _,inner=self.panel(body,"Player and host identities","The companion uses separate player and host identities under the same account.")
        self.button(inner,"host_overview","Back to overview",lambda:self.show_page("overview"),"arrow","Quiet.TButton")

    def _connections(self):
        _,inner=self.panel(self.pages["connections"].body,"Discord desktop","Activity sharing uses the same Discord application as the UE5 version.")
        ttk.Label(inner,image=self.graphics.icon("connections",52,ACCENT),background=PANEL).pack(anchor="w",pady=(0,20))
        ttk.Checkbutton(inner,text="Share my Vegas Reborn activity",variable=self.values.share_discord,style="Switch.TCheckbutton").pack(anchor="w")
        ttk.Label(inner,textvariable=self.values.discord_status,style="Panel.TLabel",wraplength=700).pack(anchor="w",pady=(16,12))
        ttk.Label(inner,text="Playing works without Discord desktop. Join invites for the original game are not available yet.",style="PanelMuted.TLabel",wraplength=700).pack(anchor="w",pady=(0,16))
        self.button(inner,"get_discord","Get Discord desktop",self.actions["get_discord"],"external","Quiet.TButton")

    def _updates(self):
        _,inner=self.panel(self.pages["updates"].body,"Companion updates","Version " + VERSION + " is installed.")
        ttk.Label(inner,image=self.graphics.icon("updates",52,ACCENT),background=PANEL).pack(anchor="w",pady=(0,20))
        ttk.Label(inner,textvariable=self.values.update_status,style="Panel.TLabel",wraplength=700,font=("Segoe UI",12)).pack(anchor="w",pady=(0,20))
        ttk.Checkbutton(inner,text="Check for updates at startup",variable=self.values.auto_check,style="Switch.TCheckbutton").pack(anchor="w",pady=(0,18))
        row=ttk.Frame(inner,style="Panel.TFrame");row.pack(fill="x")
        self.button(row,"check_update","Check for updates",self.actions["check_updates"],"sync")
        self.button(row,"install_update","Update and restart",self.actions["update"],"arrow","Primary.TButton")
        ttk.Label(inner,text="Close the game before installing an update. Downloaded files are verified, and character data and settings are preserved.",style="PanelMuted.TLabel",wraplength=700).pack(anchor="w",pady=(22,16))
        self.button(inner,"release_notes","View release notes",self.actions["release_notes"],"external","Quiet.TButton")

    def show_page(self,key):
        if key not in self.pages:
            return
        self.pages[key].tkraise()
        self.current_page=key
        for name,button in self.nav_buttons.items():
            button.configure(style="SelectedNav.TButton" if name==key else "Nav.TButton")
        _,label,_,hint=next(item for item in (*self.PAGES,self.HOST_PAGE) if item[0]==key)
        self.page_title.configure(text="Account & character" if key=="account" else label)
        self.page_hint.configure(text=hint)

    def show_conflict(self,visible):
        if visible:
            self.conflict_card.pack(fill="x",pady=(0,16))
            self.show_page("account")
            self.root.after_idle(lambda:self._scroll_to(self.conflict_card))
        else:
            self.conflict_card.pack_forget()

    def _scroll_to(self,widget):
        page=self.pages["account"]
        page.canvas.yview_moveto(widget.winfo_y()/max(1,page.body.winfo_height()))

    def focus_name(self):
        self.show_page("account")
        def focus():
            self._scroll_to(self.identity_card)
            self.widgets["name_entry"].focus_set()
        self.root.after_idle(focus)

    def render(self,state):
        session=state.session
        cooldown=bool(session and session.rename_on_cooldown)
        key=(state.busy,state.hosting,state.closing,state.playing,state.updating,state.account_verified,
             state.sync_conflict,getattr(state.available_release,"version",None),
             (session.discord_id,session.display_name,session.claimed_name,session.name_claims_enabled,session.claim_required,session.name_change_allowed,cooldown)
             if session else None,self.values.mode.get(),self.values.monitor_name.get())
        if key==getattr(self,"_render_key",None):
            return
        self._render_key=key
        def enable(key,condition):
            self.widgets[key].configure(state="normal" if condition and not state.closing else "disabled")
        idle=not state.busy
        signed=state.session is not None
        ready=signed and state.account_verified and not state.sync_conflict and not (state.session.name_claims_enabled and state.session.claim_required)
        enable("connect",idle and not state.hosting)
        self.widgets["connect"].configure(text="Switch account" if signed else "Sign in with Discord")
        enable("refresh",signed and idle and not state.hosting)
        enable("signout",signed and idle and not state.hosting)
        enable("sync",signed and idle)
        enable("saves",signed and idle)
        enable("host",ready and idle and not state.hosting)
        enable("stop_host",state.hosting)
        enable("browse",idle and not state.hosting)
        enable("check_update",not state.updating)
        enable("install_update",state.available_release is not None and not (state.updating or state.busy or state.hosting))
        enable("cloud",signed and idle)
        enable("local",signed and idle)
        self.widgets["folder_entry"].configure(state="normal" if idle and not state.hosting and not state.closing else "disabled")
        self.widgets["mode"].configure(state="readonly" if idle and not state.closing else "disabled")
        self.widgets["monitor"].configure(state="readonly" if idle and self.values.mode.get()=="Borderless" and not state.closing else "disabled")
        claims=signed and state.session.name_claims_enabled
        if claims:
            if not self.identity_card.winfo_manager():
                self.identity_card.pack(fill="x",pady=(0,16),before=self.character_card)
            self.widgets["claim"].configure(text="Claim name" if state.session.claim_required else "Save name")
        else:
            self.identity_card.pack_forget()
        enable("claim",claims and idle and not state.hosting and not cooldown)
        name_state="readonly" if cooldown else "normal"
        self.widgets["name_entry"].configure(state=name_state if claims and idle and not state.hosting and not state.closing else "disabled")
        self.values.rename_hint.set("Next rename: "+session.name_change_allowed[:10]+". Your name is read-only until then."
            if cooldown else "Names are unique. A previous name is held for 14 days after a rename.")
        if state.playing:
            primary="Game running"
        elif state.busy:
            primary="Please wait"
        elif not signed:
            primary="Sign in to launch"
        elif state.session.name_claims_enabled and state.session.claim_required:
            primary="Choose player name"
        elif state.sync_conflict:
            primary="Choose character"
        elif not state.account_verified:
            primary="Retry account check"
        else:
            primary="Launch game"
        self.primary_button.configure(text=primary,state="normal" if idle and not state.closing else "disabled")
        name=(state.session.claimed_name or state.session.display_name) if signed else "Sign in"
        name=name.replace("\r"," ").replace("\n"," ")
        self.account_chip.configure(text="  "+name[:18]+("…" if len(name)>18 else ""))
        self.values.display_summary.set(self.values.mode.get()+"\n"+self.values.monitor_name.get())
        if state.busy or state.updating:
            if not getattr(self,"_animating",False):
                self.activity.pack(side="right",padx=(14,0))
                self.activity.start(14);self._animating=True
        elif getattr(self,"_animating",False):
            self.activity.stop();self._animating=False
            self.activity.pack_forget()

    def _dark_caption(self):
        import ctypes as c
        from ctypes import wintypes as w
        try:
            u=c.WinDLL("user32");dwm=c.WinDLL("dwmapi")
            u.GetParent.argtypes=[w.HWND];u.GetParent.restype=w.HWND
            dwm.DwmSetWindowAttribute.argtypes=[w.HWND,w.DWORD,c.c_void_p,w.DWORD]
            hwnd=u.GetParent(self.root.winfo_id())
            for attribute,value in ((20,1),(35,0x00151611),(36,0x00e8f0f1)):
                number=w.DWORD(value)
                dwm.DwmSetWindowAttribute(hwnd,attribute,c.byref(number),c.sizeof(number))
        except (AttributeError,OSError):
            pass
