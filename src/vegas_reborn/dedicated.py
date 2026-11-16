"""Normal original dedicated hosting with private native Discord admission.

No elapsed lifetime limit is applied by run_dedicated. Only the owned original
server is stopped; installed assets, playing-client profiles and cloud saves
are never used as the dedicated server's writable state.
"""
import ctypes
from ctypes import wintypes
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import re
import shutil
import socket
import subprocess
import sys
import time
import uuid
import threading
from contextlib import contextmanager
from dataclasses import dataclass

ROOT = Path(sys.executable).resolve().parent if getattr(sys, "frozen", False) else Path(__file__).resolve().parents[2]
OWNED = ROOT / "local/dedicated-reference"
DEFAULT_RETAIL = Path("E:/SteamLibrary/steamapps/common/Rainbow Six Vegas")
EXPECTED = "f4388d309da94b010fb23999c09c8ba92270831d0fd2432b78a21299bd6684cb"
PHYSX_EXPECTED = {
    "PhysXLoader.dll": "313e6065f14a033147ee08d6ad3992d7511bca4bb43568d881101a0e0734cc16",
    "PhysXCore.dll": "9467d747076f0312682f572e654acdfc7dc65a72cf8d7be75bc97a064116621d",
}
OPTIONS = "R6VegasDedicatedLAN"
from .companion import CredentialIPC, _private_write, load_configuration
from .discord_client import DiscordClient, load_session


class DedicatedError(Exception):
    """Fixed lifecycle/precondition codes; never expose credentials."""


@dataclass(frozen=True)
class DedicatedResult:
    pid: int
    runtime: Path
    exit_code: int | None
    stop_reason: str
    forced_termination: bool
    source_unchanged: bool
    global_profiles_unchanged: bool


def _paths(source, owned_root=None):
    source = Path(source).resolve()
    owned = Path(owned_root or ROOT / "local/dedicated-reference").resolve()
    local = (ROOT / "local").resolve()
    if (not local.is_relative_to(ROOT.resolve()) or not owned.is_relative_to(local)
            or owned.is_relative_to(source) or source.is_relative_to(owned)):
        raise DedicatedError("dedicated_copy_path_invalid")
    return source, owned


def _session_path(session_file):
    path = Path(session_file).resolve()
    if not path.is_relative_to((ROOT / "local").resolve()) or not path.is_file():
        raise DedicatedError("private_session_must_be_local")
    return path


@contextmanager
def _exclusive_host(owned):
    if os.name != "nt":
        raise DedicatedError("dedicated_hosting_requires_windows")
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateMutexW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.LPCWSTR]
    kernel.CreateMutexW.restype = wintypes.HANDLE
    kernel.ReleaseMutex.argtypes = [wintypes.HANDLE]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    suffix = hashlib.sha256(str(Path(owned).resolve()).lower().encode()).hexdigest()[:24]
    handle = kernel.CreateMutexW(None, True, "Local\\VegasOriginalDedicated_" + suffix)
    if not handle:
        raise DedicatedError("dedicated_host_lock_failed")
    if ctypes.get_last_error() == 183:
        kernel.CloseHandle(handle)
        raise DedicatedError("dedicated_host_already_running")
    try:
        yield
    finally:
        kernel.ReleaseMutex(handle)
        kernel.CloseHandle(handle)


def sha(path):
    result = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(block)
    return result.hexdigest()


def snapshot(path):
    if not path.exists():
        return {}
    return {str(item.relative_to(path)).replace("\\", "/"): sha(item)
            for item in sorted(path.rglob("*")) if item.is_file()}


def global_profiles():
    shell = ctypes.WinDLL("shell32", use_last_error=True)
    shell.SHGetFolderPathW.argtypes = [wintypes.HWND, ctypes.c_int, wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR]
    folder = ctypes.create_unicode_buffer(32768)
    if shell.SHGetFolderPathW(None, 5, None, 0, folder) != 0:
        raise DedicatedError("cannot_resolve_original_documents_profile")
    roots = [Path(folder.value) / "Ubisoft/R6Vegas"]
    for name in ("APPDATA", "LOCALAPPDATA"):
        if os.environ.get(name):
            roots.append(Path(os.environ[name]) / "Ubisoft/R6Vegas")
    return {str(path): snapshot(path) for path in roots}


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.parent / (".dedicated-" + uuid.uuid4().hex)
    with temporary.open("x", encoding="utf-8") as output:
        output.write(json.dumps(value, indent=2) + "\n")
    os.replace(temporary, path)


def _prepare(source, owned, *, online=False, config_path=None, notify=lambda text: None):
    source, owned = Path(source).resolve(), Path(owned).resolve()
    notify("Checking the original dedicated server files...")
    local = (ROOT / "local").resolve()
    if (not local.is_relative_to(ROOT.resolve()) or not owned.is_relative_to(local)
            or owned.is_relative_to(source) or source.is_relative_to(owned)):
        raise DedicatedError("dedicated_copy_path_invalid")
    executable = source / "Binaries/R6VegasServer.exe"
    if sha(executable) != EXPECTED:
        raise DedicatedError("installed_server_identity_changed")
    for name, expected in PHYSX_EXPECTED.items():
        if sha(source / "Binaries" / name) != expected:
            raise DedicatedError("installed_physics_binary_identity_changed")
    owned.mkdir(parents=True, exist_ok=True)
    game = owned / "game"
    if not game.resolve().is_relative_to(owned):
        raise DedicatedError("dedicated_copy_cannot_follow_external_link")
    before = snapshot(source)
    profiles = global_profiles()
    if not game.exists():
        shutil.copytree(source, game, ignore=shutil.ignore_patterns("*.log", "MemDump.dmp", "session"))
    if sha(game / "Binaries/R6VegasServer.exe") != EXPECTED:
        raise DedicatedError("copied_server_identity_changed")
    for name, expected in PHYSX_EXPECTED.items():
        if sha(game / "Binaries" / name) != expected:
            raise DedicatedError("copied_physics_binary_identity_changed")
    # A separate options file preserves the supplied original defaults.
    original_options = (source / "KellerGame/Config/PC/R6VegasServerConfig.ini").read_bytes()
    text = original_options.decode("latin1")
    text = re.sub(r"(?m)^m_bPunkBusterSv=.*$", "m_bPunkBusterSv=False", text)
    target = game / ("KellerGame/Config/PC/" + OPTIONS + ".ini")
    if not target.resolve().is_relative_to(game.resolve()):
        raise DedicatedError("dedicated_options_cannot_follow_external_link")
    temporary = target.parent / (".dedicated-" + uuid.uuid4().hex)
    with temporary.open("xb") as output:
        output.write(text.encode("latin1"))
    os.replace(temporary, target)
    public_hash = None
    if online:
        config, public = load_configuration(config_path or ROOT / "local/companion/service.json")
        copied_public = owned / "root-public.der"
        _private_write(copied_public, public.read_bytes())
        public_hash = config["rootPublicSha256"]
        lua = game / "KellerGame/Config/PC/AgoraConfig.lua"
        if not lua.resolve().is_relative_to(game.resolve()):
            raise DedicatedError("dedicated_trust_config_cannot_follow_external_link")
        lua_text, count = re.subn(r'(?m)^UbiSigKeyPub\s*=\s*"[^"]*";',
            'UbiSigKeyPub = "' + str(copied_public).replace("\\", "/") + '";', lua.read_text())
        if count != 1:
            raise DedicatedError("unsupported_owned_dedicated_trust_configuration")
        _private_write(lua, lua_text.encode())
    after = snapshot(source)
    profiles_after = global_profiles()
    receipt = {"kind": "isolated-original-dedicated-preparation", "source": str(source), "game": str(game),
        "serverSha256": EXPECTED, "sourceFiles": before,
        "sourceUnchanged": before == after, "globalProfilesBefore": profiles,
        "globalProfilesUnchanged": profiles == profiles_after,
        "optionsOriginalSha256": hashlib.sha256(original_options).hexdigest(),
        "optionsOwnedSha256": sha(game / ("KellerGame/Config/PC/" + OPTIONS + ".ini")),
        "mode": "online" if online else "lan", "copiedPublicKeySha256": public_hash,
        "launched": False}
    write_json(owned / "preparation.json", receipt)
    if not receipt["sourceUnchanged"] or not receipt["globalProfilesUnchanged"]:
        raise DedicatedError("source_or_global_profiles_changed_during_preparation")
    return game


class StartupInfo(ctypes.Structure):
    _fields_ = [("cb", wintypes.DWORD), ("reserved", wintypes.LPWSTR), ("desktop", wintypes.LPWSTR),
        ("title", wintypes.LPWSTR), ("x", wintypes.DWORD), ("y", wintypes.DWORD),
        ("xSize", wintypes.DWORD), ("ySize", wintypes.DWORD), ("xCount", wintypes.DWORD),
        ("yCount", wintypes.DWORD), ("fill", wintypes.DWORD), ("flags", wintypes.DWORD),
        ("show", wintypes.WORD), ("reservedCount", wintypes.WORD), ("reservedBytes", ctypes.c_void_p),
        ("input", wintypes.HANDLE), ("output", wintypes.HANDLE), ("error", wintypes.HANDLE)]


class ProcessInfo(ctypes.Structure):
    _fields_ = [("process", wintypes.HANDLE), ("thread", wintypes.HANDLE),
                ("pid", wintypes.DWORD), ("tid", wintypes.DWORD)]


def stop_owned(kernel, child):
    if kernel.WaitForSingleObject(child.process, 0) == 0:
        return False
    user = ctypes.WinDLL("user32", use_last_error=True)
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    user.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    user.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
    user.EnumWindows.argtypes = [callback_type, wintypes.LPARAM]
    def close(window, _):
        pid = wintypes.DWORD()
        user.GetWindowThreadProcessId(window, ctypes.byref(pid))
        if pid.value == child.pid:
            user.PostMessageW(window, 0x10, 0, 0)  # WM_CLOSE to owned windows only.
        return True
    user.EnumWindows(callback_type(close), 0)
    if kernel.WaitForSingleObject(child.process, 5000) == 0:
        return False
    kernel.TerminateProcess(child.process, 0)
    if kernel.WaitForSingleObject(child.process, 10000) != 0:
        raise DedicatedError("owned_dedicated_cleanup_failed")
    return True


def _run(game, *, source, owned, stop_event, session_file=None, config_path=None, notify=lambda text: None, seconds=None, on_started=None):
    if stop_event.is_set():
        raise DedicatedError("dedicated_start_cancelled")
    online = session_file is not None
    source = Path(source).resolve()
    dll = (Path(sys._MEIPASS) / "native/VegasDedicated-4.dll"
           if getattr(sys, "frozen", False)
           else ROOT / "local/companion/bin/VegasDedicated-4.dll")
    injector = ROOT / "local/companion/bin/VegasInjector.exe"
    if not dll.is_file() or not injector.is_file():
        raise DedicatedError("build_dedicated_module_and_existing_injector_first")
    runtime = owned / "runs" / uuid.uuid4().hex
    runtime.mkdir(parents=True, mode=0o700)
    # Public dummy fields satisfy the module's route schema. No IPC listener,
    # Discord session, native lease or password is supplied for this LAN run.
    _private_write(runtime / "native-route.txt", ("35.178.236.164\n3074\n9\n" + "0" * 64 + "\nlanfixture00001\n").encode("ascii"))
    (runtime / "profile").mkdir(mode=0o700)
    before, profiles = snapshot(source), global_profiles()
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateProcessW.argtypes = [wintypes.LPCWSTR, wintypes.LPWSTR, ctypes.c_void_p, ctypes.c_void_p,
        wintypes.BOOL, wintypes.DWORD, ctypes.c_void_p, wintypes.LPCWSTR,
        ctypes.POINTER(StartupInfo), ctypes.POINTER(ProcessInfo)]
    kernel.CreateProcessW.restype = wintypes.BOOL
    kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
    kernel.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    exe = game / "Binaries/R6VegasServer.exe"
    # Original shipped template, with LAN selected, PB disabled and separate
    # options. MULTIHOME is independently verified in the original parser.
    url = ("MP_Streets_01?AgO=0?AgU=?AgP=?SrvOptionFile=" + OPTIONS +
        "?RG=0?RC=3?TBR=0?PB=0?GAME=R6Game.R6TeamDeathmatchGame?TKPM=5?FF=0?TL=180?MP=16?AR=1?ML=3?TR=0?GPL=0?LT=0?TKP=1?TB=5?GD=2?PW=")
    arguments = [str(exe), "TEXTSERVER", url]
    # The loopback interface is useful only for the local LAN fixture. An
    # online NAT socket must use its actual routed interface, as in the shipped
    # original command template, rather than retain a loopback source address.
    if not online:
        arguments.append("MULTIHOME=127.0.0.3")
    startup, child = StartupInfo(), ProcessInfo()
    startup.cb = ctypes.sizeof(startup)
    startup.flags, startup.show = 1, 0  # STARTF_USESHOWWINDOW, SW_HIDE.
    startup.desktop = "winsta0\\Default"
    command = ctypes.create_unicode_buffer(subprocess.list2cmdline(arguments))
    receipt = {"kind": "original-dedicated-lan-run", "arguments": arguments, "runtime": str(runtime),
        "startedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "serverSha256": EXPECTED, "adapterSha256": sha(dll), "injected": False,
        "secretsInArguments": False, "bearerPassedToGame": False, "ipcListenerStarted": False, "seconds": seconds,
        "sourceBefore": before, "globalProfilesBefore": profiles,
        "acceptance": "Process lifecycle only; native bind/map/join/round evidence must be recorded independently."}
    forced, ipc = False, None
    ipc_events = []
    try:
        if online:
            notify("Signing in for dedicated hosting...")
            session = load_session(session_file)
            client = DiscordClient()
            # Obtain only the stable alias from this first verified mint. The
            # password is never persisted/placed on a command line. Task start
            # requests a fresh one-use lease from the same authenticated account.
            alias = client.mint_native(session, dedicated=True).username
            ipc = CredentialIPC(session, client=client, notify=ipc_events.append, dedicated=True)
            configuration, _ = load_configuration(config_path or ROOT / "local/companion/service.json")
            private_profile = runtime / "profile/Ubisoft/R6Vegas"
            _private_write(private_profile / "PlayerProfile.ini", ("[CDKEY]\nKEYID=" + configuration["nativeLicenseId"] + "\n").encode())
            route_ip = str(ipaddress.IPv4Address(socket.gethostbyname(configuration["host"])))
            _private_write(runtime / "native-route.txt", (route_ip + "\n" + str(configuration["port"]) + "\n" + str(ipc.port) + "\n" + ipc.capability + "\n" + alias + "\n").encode())
            url = url.replace("?AgO=0?AgU=?AgP=?", "?AgO=1?AgU=" + alias + "?AgP=UseDiscord?")
            arguments[2] = url
            command = ctypes.create_unicode_buffer(subprocess.list2cmdline(arguments))
            receipt.update(kind="original-dedicated-online-run", ipcListenerStarted=True,
                credentialMode="verified-Discord-dedicated-role/on-demand-native-one-use-lease",
                nativeRole="dedicated", serverCloudProfileSync=False)
        if not kernel.CreateProcessW(str(exe), command, None, None, False,
            0x00000004 | 0x08000000, None, str(game / "Binaries"), ctypes.byref(startup), ctypes.byref(child)):
            raise DedicatedError("dedicated_create_process_failed_" + str(ctypes.get_last_error()))
        receipt["pid"], receipt["tid"] = child.pid, child.tid
        write_json(runtime / "child.json", {"pid": child.pid, "tid": child.tid})
        result = subprocess.run([str(injector), str(child.pid), str(child.tid), str(dll), str(runtime), str(game), str(source)],
            capture_output=True, timeout=30, creationflags=subprocess.CREATE_NO_WINDOW)
        if result.returncode:
            raise DedicatedError("dedicated_injection_failed_" + str(result.returncode))
        receipt["injected"] = True
        receipt["injector"] = json.loads(result.stdout)
        status = runtime / "compat-status.json"
        ready_deadline = time.monotonic() + 65
        while not status.exists():
            if stop_event.is_set():
                raise DedicatedError("dedicated_start_cancelled")
            if kernel.WaitForSingleObject(child.process, 100) != 258:
                raise DedicatedError("dedicated_exited_during_native_setup")
            if time.monotonic() >= ready_deadline:
                raise DedicatedError("dedicated_native_setup_timeout")
        compatibility = json.loads(status.read_text())
        receipt["compatibility"] = compatibility
        if (not compatibility.get("ready") or compatibility.get("nativeVersion") != 4
                or not compatibility.get("nativeStatisticsVerified")
                or not compatibility.get("nativeRankedHostVerified")
                or not compatibility.get("nativeSoloHuntVerified")):
            raise DedicatedError("dedicated_native_statistics_setup_not_verified")
        write_json(runtime / "running.json", {"pid": child.pid, "runtime": str(runtime), "stopRequest": str(runtime / "stop.request")})
        started = {"pid": child.pid, "runtime": str(runtime), "stopRequest": str(runtime / "stop.request")}
        if on_started:
            on_started(started)
        notify("Dedicated server running. Join through Online â†’ Custom Match.")
        deadline = time.monotonic() + seconds if seconds is not None else None
        receipt["stopReason"] = "native_process_exit"
        while kernel.WaitForSingleObject(child.process, 250) == 258:
            if stop_event.is_set() or (runtime / "stop.request").exists():
                receipt["stopReason"] = "stop_request"
                break
            if deadline is not None and time.monotonic() >= deadline:
                receipt["stopReason"] = "deadline"
                break
        status = runtime / "compat-status.json"
        receipt["compatibility"] = json.loads(status.read_text()) if status.exists() else None
    except Exception as error:
        receipt["errorType"] = type(error).__name__
        if isinstance(error, DedicatedError):
            raise
        raise DedicatedError("dedicated_start_or_runtime_failed") from None
    finally:
        try:
            if child.process:
                try:
                    forced = stop_owned(kernel, child)
                    code = wintypes.DWORD()
                    kernel.GetExitCodeProcess(child.process, ctypes.byref(code))
                    receipt["exitCode"] = code.value
                finally:
                    if child.thread:
                        kernel.CloseHandle(child.thread)
                    kernel.CloseHandle(child.process)
        finally:
            if ipc:
                ipc.close()
                receipt["ipcClosed"] = True
            receipt["credentialEvents"] = ipc_events
            receipt["nativeCredentialsDelivered"] = any(event == "Discord credentials supplied to the game." for event in ipc_events)
        receipt["forcedTermination"] = forced
        receipt["finishedUtc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
        native_log = game / ("Binaries/Log_" + OPTIONS + ".log")
        if native_log.is_file():
            _private_write(runtime / "native-server.log", native_log.read_bytes())
            receipt["nativeLogSha256"] = sha(runtime / "native-server.log")
        receipt["sourceUnchanged"] = snapshot(source) == before
        receipt["globalProfilesUnchanged"] = global_profiles() == profiles
        receipt["ownedProfileAfter"] = snapshot(runtime / "profile")
        write_json(runtime / "receipt.json", receipt)
        (runtime / "running.json").unlink(missing_ok=True)
        notify("Dedicated server stopped.")
        if not receipt["sourceUnchanged"] or not receipt["globalProfilesUnchanged"]:
            raise DedicatedError("source_or_global_profiles_changed_during_run")


    return DedicatedResult(child.pid, runtime, receipt.get("exitCode"), receipt.get("stopReason", "setup_failed"), forced, receipt["sourceUnchanged"], receipt["globalProfilesUnchanged"])


def prepare_dedicated(*, source=DEFAULT_RETAIL, config_path=None, owned_root=None,
                      online=False, notify=lambda text: None):
    """Prepare only; no process, credential mint or IPC listener is started."""
    source, owned = _paths(source, owned_root)
    with _exclusive_host(owned):
        return _prepare(source, owned, online=online, config_path=config_path, notify=notify)


def run_dedicated(session_file, *, source=DEFAULT_RETAIL, config_path=None,
                  stop_event=None, notify=lambda text: None, owned_root=None):
    """Run normal online hosting until native exit or stop_event; no time limit.

    Returns DedicatedResult after owned cleanup/preservation checks. notify is
    called from the hosting worker with status strings, not a gameplay-ready
    claim. A distinct h-role under the verified Discord account owns the host.
    """
    source, owned = _paths(source, owned_root)
    session_file = _session_path(session_file)
    stop_event = stop_event if stop_event is not None else threading.Event()
    with _exclusive_host(owned):
        if stop_event.is_set():
            raise DedicatedError("dedicated_start_cancelled")
        game = _prepare(source, owned, online=True, config_path=config_path, notify=notify)
        return _run(game, source=source, owned=owned, stop_event=stop_event,
                    session_file=session_file, config_path=config_path, notify=notify)


def run_bounded(*, seconds=120, session_file=None, source=DEFAULT_RETAIL,
                config_path=None, owned_root=None, stop_event=None,
                notify=lambda text: None, on_started=None):
    """Explicit research entry point. Normal hosting uses run_dedicated."""
    if type(seconds) is not int or not 5 <= seconds <= 1200:
        raise DedicatedError("dedicated_research_duration_outside_bounds")
    source, owned = _paths(source, owned_root)
    if session_file is not None:
        session_file = _session_path(session_file)
    stop_event = stop_event if stop_event is not None else threading.Event()
    with _exclusive_host(owned):
        game = _prepare(source, owned, online=session_file is not None,
                        config_path=config_path, notify=notify)
        return _run(game, source=source, owned=owned, stop_event=stop_event,
                    session_file=session_file, config_path=config_path, seconds=seconds,
                    notify=notify, on_started=on_started)
