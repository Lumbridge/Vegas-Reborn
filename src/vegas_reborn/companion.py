"""Normal-play companion lifecycle and private on-demand native credential IPC."""
from __future__ import annotations
from contextlib import contextmanager
import ctypes
import hashlib
import hmac
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import socket
import subprocess
import threading
import time

from .discord_client import DiscordClient, AccountSession
from .retail_sync import RetailProfileSync, retail_game_running

ROOT = Path(__file__).resolve().parents[2]
RETAIL_SHA256 = "667f659b3c128ab14506a464a462a592e16aca906084cabfc2d890e9e4bd5acc"
DEFAULT_RETAIL = Path("E:/SteamLibrary/steamapps/common/Rainbow Six Vegas")


class CompanionError(Exception):
    pass


class CredentialIPC:
    """Loopback capability endpoint; no Discord bearer is handed to the game."""
    def __init__(self, session, *, client=None, notify=lambda text: None, dedicated=False,
                 statistics_codec=""):
        self.session, self.client, self.notify = session, client or DiscordClient(), notify
        self.dedicated = dedicated
        if statistics_codec not in ("", "native-base-v1"):
            raise CompanionError("Unsupported native statistics codec")
        self.statistics_codec = statistics_codec
        self.capability = secrets.token_hex(32)
        self.lock = threading.Lock()
        self.closed = False
        owner = self

        class Handler(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, *args):
                pass

            def do_POST(self):
                payload, status = b"Unavailable", 503
                if self.path != "/native-credential":
                    status, payload = 404, b"Not found"
                elif (self.headers.get("Transfer-Encoding") is not None
                      or self.headers.get("Content-Length") != "0"
                      or not hmac.compare_digest(self.headers.get("Authorization", ""),
                                                 "Bearer " + owner.capability)):
                    status, payload = 401, b"Unauthorized"
                elif not owner.closed and owner.lock.acquire(blocking=False):
                    try:
                        options = {"statistics_codec": owner.statistics_codec} if owner.statistics_codec else {}
                        if owner.dedicated:
                            options["dedicated"] = True
                        lease = owner.client.mint_native(owner.session, **options)
                        payload = (lease.username + "\n" + lease.password + "\n").encode("ascii")
                        status = 200
                        owner.notify("Discord credentials supplied to the game.")
                    except Exception:
                        owner.notify("Game sign-in failed. Sign in to Discord again, then retry in the game.")
                    finally:
                        owner.lock.release()
                self.send_response(status)
                self.send_header("Content-Type", "text/plain")
                self.send_header("Content-Length", str(len(payload)))
                self.send_header("Cache-Control", "no-store")
                self.send_header("Connection", "close")
                self.end_headers()
                try:
                    self.wfile.write(payload)
                except OSError:
                    pass
                self.close_connection = True

        class Server(ThreadingHTTPServer):
            daemon_threads = True
            def get_request(self):
                connection, address = super().get_request()
                connection.settimeout(5)
                return connection, address

            def handle_error(self, *args):
                pass

        self.server = Server(("127.0.0.1", 0), Handler)
        self.port = self.server.server_address[1]
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def close(self):
        self.closed = True
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=6)


def _private_write(path, data):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + "." + secrets.token_hex(6))
    fd = os.open(temporary, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    try:
        with os.fdopen(fd, "wb") as output:
            output.write(data)
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def validate_compatibility(compatibility, *, claimed_name=None, display_mode="Game default"):
    if not compatibility.get("ready"):
        if compatibility.get("signatures") != 16383:
            raise CompanionError("The game's native code does not match this compatibility module")
        if compatibility.get("executablePagesReady") is False or any(
                compatibility.get(key) == 7 for key in ("unlockHookStatus", "taskHookStatus", "prefillHookStatus")):
            raise CompanionError("The game did not finish preparing its executable code. Close the game and retry launching.")
        raise CompanionError("The game's code matched, but compatibility setup failed. Update the companion and retry.")
    if (compatibility.get("nativeVersion") != 11
            or not compatibility.get("proceduralAnimationVerified")
            or not compatibility.get("nativeStatisticsVerified")
            or not compatibility.get("nativeRankedHostVerified")
            or not compatibility.get("nativeSoloHuntVerified")
            or not compatibility.get("nativeUnlockPolicyVerified")
            or not compatibility.get("nativeRankVerified")
            or not compatibility.get("rankedAccessVerified")):
        raise CompanionError("Update the companion's online compatibility module before playing")
    if claimed_name and (not compatibility.get("claimedNameVerified")
                         or not compatibility.get("claimedSessionNameVerified")):
        raise CompanionError("The game's player-name setup was not verified; update the companion and retry")
    if display_mode == "Borderless" and not compatibility.get("displayStyleVerified"):
        raise CompanionError("Borderless setup was not verified. Update the companion's native compatibility module, then retry.")


def launch_options_ready(report, display_mode, skip_intros):
    if report.get("hookFailed"):
        raise CompanionError("The game's requested display settings could not be installed. Update the companion and retry.")
    if skip_intros and not report.get("introSequenceHookInstalled"):
        return False
    if display_mode in ("Windowed", "Borderless"):
        return bool(report.get("viewportHookInstalled") and report.get("deviceVerified") and report.get("actualWindowed"))
    return True


def load_configuration(path):
    path = Path(path).resolve()
    value = json.loads(path.read_text(encoding="utf-8"))
    if (value.get("version") != 1 or value.get("host") != "vegas.lumbridge.co.uk"
            or value.get("port") != 3074 or not re.fullmatch(r"[0-9a-f]{48}", value.get("nativeLicenseId", ""))
            or not re.fullmatch(r"[0-9a-f]{64}", value.get("rootPublicSha256", ""))):
        raise CompanionError("Invalid companion service configuration")
    public_key = path.parent / "root-public.der"
    if hashlib.sha256(public_key.read_bytes()).hexdigest() != value["rootPublicSha256"]:
        raise CompanionError("Companion public key failed its integrity check")
    return value, public_key


def prepare_game(source=DEFAULT_RETAIL, *, destination=None, notify=lambda text: None):
    """Copy owned assets, preserving the installed reference and user profiles."""
    source = Path(source).resolve()
    destination = Path(destination or ROOT / "local/companion/game").resolve()
    local = (ROOT / "local").resolve()
    if not destination.is_relative_to(local) or source.is_relative_to(destination) or destination.is_relative_to(source):
        raise CompanionError("The companion game copy must be separate from the installation")
    executable = source / "Binaries/R6Vegas_Game.exe"
    if hashlib.sha256(executable.read_bytes()).hexdigest() != RETAIL_SHA256:
        raise CompanionError("This game version is not supported by the compatibility module")
    marker = destination / ".companion-ready.json"
    if not marker.exists():
        if destination.exists():
            raise CompanionError("An unfinished game copy exists; preserve it before preparing another")
        notify("Preparing a separate game copy. The installed game is preserved.")
        def ignore(directory, names):
            return [name for name in names if name.lower() in ("session", "memdump.dmp")
                    or name.lower().endswith(".log")]
        shutil.copytree(source, destination, ignore=ignore)
        _private_write(marker, json.dumps({"version": 1, "sourceSha256": RETAIL_SHA256}).encode())
    if hashlib.sha256((destination / "Binaries/R6Vegas_Game.exe").read_bytes()).hexdigest() != RETAIL_SHA256:
        raise CompanionError("The companion game copy failed its integrity check")
    for filename, expected in {
        "PhysXLoader.dll": "313e6065f14a033147ee08d6ad3992d7511bca4bb43568d881101a0e0734cc16",
        "PhysXCore.dll": "9467d747076f0312682f572e654acdfc7dc65a72cf8d7be75bc97a064116621d",
    }.items():
        if hashlib.sha256((destination / "Binaries" / filename).read_bytes()).hexdigest() != expected:
            raise CompanionError("The bundled physics runtime is not the supported original version")
    for relative in ("KellerGame/Config/DefaultEngine.ini", "KellerGame/Config/PC/DefaultEngine.ini",
                     "KellerGame/Config/PC/KellerEngine.ini", "KellerGame/Config/PCKellerEngine.ini"):
        target = destination / relative
        if not target.exists():
            continue
        text = target.read_text(encoding="utf-8-sig")
        section, lines = "", []
        for line in text.splitlines():
            if line.startswith("["):
                section = line
            if section == "[URL]" and re.match(r"(LocalMap|Map|LocalMapRetail|MapRetail)=", line):
                line = line.split("=", 1)[0] + "=Menu.rmpc"
            if section == "[URL]" and re.match(r"MapExt(Retail)?=", line):
                line = line.split("=", 1)[0] + "=rmpc"
            if section == "[D3DDrv.D3DRenderDevice]" and line.startswith("DisableOcclusion="):
                # The original blocking D3D9 occlusion-query wait can hang
                # lobby travel on current drivers. Use the game's own switch
                # in this private copy; retain native rendering/results.
                line = "DisableOcclusion=True"
            lines.append(line)
        _private_write(target, ("\n".join(lines) + "\n").encode())
    return destination


def _native(arguments):
    response = subprocess.run([str(item) for item in arguments], capture_output=True, timeout=40,
                              creationflags=subprocess.CREATE_NO_WINDOW)
    if response.returncode:
        raise CompanionError("Native launch setup failed; the installed game was preserved")
    return json.loads(response.stdout)


class RetailLauncher:
    def __init__(self, config, *, notify=lambda text: None):
        self.config, self.public_key = load_configuration(config)
        self.notify = notify
        self.sync = RetailProfileSync()
        self.running = False

    def play(self, session, *, source=DEFAULT_RETAIL, display_mode="Game default", monitor_device="",
             skip_intros=False, on_started=lambda: None):
        if os.name != "nt":
            raise CompanionError("The original retail client requires Windows")
        if retail_game_running():
            raise CompanionError("Close the running retail game before launching another")
        self.running = True
        ipc, gate, child, mutex = None, None, None, None
        started = False
        control = ROOT / "local/companion/bin/VegasSteamControl.exe"
        gate_dll = ROOT / "local/companion/bin/VegasSteamGate.dll"
        try:
            from ctypes import wintypes
            kernel = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel.CreateMutexW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.LPCWSTR]
            kernel.CreateMutexW.restype = wintypes.HANDLE
            kernel.CloseHandle.argtypes = [wintypes.HANDLE]
            mutex = kernel.CreateMutexW(None, True, "Local\\VegasRetailCompanion")
            if not mutex or ctypes.get_last_error() == 183:
                raise CompanionError("Another companion is already launching the game")
            self.notify("Checking the account's cloud characterÃ¢â‚¬Â¦")
            # Rehydrate the claim from its owner-authenticated source. A cached
            # display name cannot authorize an in-game identity or rename.
            session = DiscordClient().refresh_account(session)
            if session.name_claims_enabled and session.claim_required:
                raise CompanionError("Choose your player name in the companion before playing")
            self.sync.pull(session)
            game = prepare_game(source, notify=self.notify)
            from .trueskill_carrier import ensure_carrier
            try:
                statistics_codec = ensure_carrier(Path(source).resolve(), game)
            except (OSError, ValueError) as error:
                raise CompanionError("The copied game's leaderboard compatibility setup failed") from error
            if statistics_codec != "native-base-v1":
                raise CompanionError("The copied game's leaderboard compatibility was not verified")
            from .startup_movies import configure_startup_movies
            configure_startup_movies(game, skip_intros)
            from .display import Desktop, configure_display, select_monitor
            desktop = Desktop() if display_mode in ("Borderless", "Windowed") else None
            monitor = select_monitor(desktop.monitors(), monitor_device) if desktop else None
            configure_display(game, display_mode, monitor)
            account = self.sync.account_directory(session)
            claim_file = account / "claimed-name.txt"
            if session.claimed_name is not None:
                _private_write(claim_file, (session.claimed_name + "\n").encode("ascii"))
            else:
                claim_file.unlink(missing_ok=True)
            _private_write(account / "display-mode.txt",
                           b"borderless\n" if display_mode == "Borderless" else b"windowed\n" if display_mode == "Windowed" else b"native\n")
            _private_write(account / "skip-intros.txt", b"skip\n" if skip_intros else b"play\n")
            (account / "launch-options-status.json").unlink(missing_ok=True)
            profile = self.sync.profile_directory(session)
            profile.mkdir(parents=True, exist_ok=True)
            license_file = profile / "PlayerProfile.ini"
            if not license_file.exists():
                _private_write(license_file, ("[CDKEY]\nKEYID=" + self.config["nativeLicenseId"] + "\n").encode())
            config = game / "KellerGame/Config/PC/AgoraConfig.lua"
            text = config.read_text(encoding="utf-8-sig")
            text, count = re.subn(r'(?m)^UbiSigKeyPub\s*=.*?;\s*$',
                lambda _: 'UbiSigKeyPub = "' + self.public_key.as_posix() + '";', text)
            if count != 1:
                raise CompanionError("The copied game's trust configuration is unsupported")
            _private_write(config, text.encode())
            # Mint only for the stable alias here; this lease is revoked by
            # the on-demand game Login request. Its password is never saved.
            lease = DiscordClient().mint_native(session, statistics_codec=statistics_codec)
            ipc = CredentialIPC(session, notify=self.notify, statistics_codec=statistics_codec)
            address = socket.gethostbyname(self.config["host"])
            ipaddress.IPv4Address(address)
            _private_write(account / "native-route.txt",
                f"{address}\n3074\n{ipc.port}\n{ipc.capability}\n{lease.username}\n".encode())
            process_rows = subprocess.check_output(["tasklist", "/FI", "IMAGENAME eq steam.exe", "/FO", "CSV", "/NH"],
                                                    creationflags=subprocess.CREATE_NO_WINDOW).decode(errors="replace")
            match = re.search(r'(?mi)^"steam\.exe","([0-9]+)"', process_rows)
            if not match:
                raise CompanionError("Start Steam and sign in before playing")
            steam_pid = int(match[1])
            child_file = account / "child.json"
            child_file.unlink(missing_ok=True)
            ready_file = account / "compat-status.json"
            ready_file.unlink(missing_ok=True)
            launch_config = account / "launch.utf16"
            _private_write(launch_config, (str(Path(source).resolve() / "Binaries/R6Vegas_Game.exe") + "\n"
                + str(game / "Binaries") + "\nwinsta0\\Default\n" + str(account)).encode("utf-16-le"))
            gate = _native([control, "--configure", steam_pid, gate_dll, "StartGate", launch_config])
            subprocess.Popen(["C:/Program Files (x86)/Steam/steam.exe", "-applaunch", "13540",
                              "Menu?GAME=R6Game.R6MenuGameInfo?PB=0", "-NoHomeDir"], creationflags=subprocess.CREATE_NO_WINDOW)
            deadline = time.monotonic() + 45
            while not child_file.exists():
                if time.monotonic() > deadline:
                    raise CompanionError("Steam did not start the game")
                time.sleep(.1)
            child = json.loads(child_file.read_text())
            _native([ROOT / "local/companion/bin/VegasInjector.exe", child["pid"], child["tid"],
                     ROOT / "local/companion/bin/VegasCompanion-11.dll", account, game, Path(source).resolve()])
            _native([control, "--stop-gate", steam_pid, gate["module"], gate_dll])
            gate = None
            deadline = time.monotonic() + 65
            while not ready_file.exists():
                if time.monotonic() > deadline:
                    raise CompanionError("The game's compatibility module did not become ready")
                time.sleep(.1)
            compatibility = json.loads(ready_file.read_text())
            validate_compatibility(compatibility, claimed_name=session.claimed_name, display_mode=display_mode)
            if display_mode in ("Windowed", "Borderless") or skip_intros:
                self.notify("Verifying the game's requested display and intro settings...")
                options_file=account / "launch-options-status.json"
                deadline=time.monotonic()+15
                while True:
                    try:
                        options=json.loads(options_file.read_text())
                    except (OSError,ValueError):
                        options={}
                    if launch_options_ready(options,display_mode,skip_intros):
                        break
                    if time.monotonic()>deadline:
                        raise CompanionError("The game's requested display or intro settings were not verified. Close the game and retry.")
                    time.sleep(.1)
            started = True
            self.notify("Game running. Choose Multiplayer Ã¢â€ â€™ Online and click Login. Sign-in is automatic.")
            on_started()
            kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
            kernel.OpenProcess.restype = wintypes.HANDLE
            kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
            process = kernel.OpenProcess(0x100000, False, child["pid"])
            if not process:
                raise CompanionError("The game exited during startup")
            try:
                while kernel.WaitForSingleObject(process, 500) == 258:
                    if desktop:
                        try:
                            if display_mode == "Windowed":
                                desktop.fit_windowed(child["pid"])
                            else:
                                current = select_monitor(desktop.monitors(), monitor_device)
                                if current:
                                    desktop.fit_borderless(child["pid"], current)
                        except OSError:
                            self.notify(f"Could not apply {display_mode.lower()} mode. The game is still running; restart it to retry.")
                            desktop = None
            finally:
                kernel.CloseHandle(process)
            self.notify("Saving the character and progression to your accountÃ¢â‚¬Â¦")
            result = self.sync.push(session) if (profile / "R6_PECPEC").exists() else None
            self.notify("Character saved to your account." if result else "Game closed. No character was created.")
            return result
        finally:
            try:
                if child and not started:
                    # Only a child reported by our active Steam gate is owned.
                    # Setup failures must not leave its main thread suspended.
                    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
                    kernel.OpenProcess.restype = wintypes.HANDLE
                    owned = kernel.OpenProcess(1, False, child["pid"])
                    if owned:
                        kernel.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
                        kernel.TerminateProcess(owned, 1)
                        kernel.CloseHandle(owned)
                if gate:
                    _native([control, "--stop-gate", steam_pid, gate["module"], gate_dll])
            finally:
                if ipc:
                    ipc.close()
                if mutex:
                    kernel.CloseHandle(mutex)
                self.running = False
