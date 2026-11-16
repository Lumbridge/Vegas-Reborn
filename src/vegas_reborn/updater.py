"""Signed GitHub Releases updates; account/game directories are never replaced."""
from dataclasses import dataclass
import base64
import ctypes
from ctypes import wintypes
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import ssl
import stat
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zipfile
import truststore

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey
from .update_key import PUBLIC_KEY_HEX
from .version import REPOSITORY, VERSION

MAX_DOWNLOAD = 200 * 1024 * 1024
MAX_UNPACKED = 700 * 1024 * 1024
SEED_FILES = {"local/companion/service.json", "local/companion/root-public.der"}
ROOT_FILES = {"VegasReborn.exe", "README.txt", "LICENSE-Python.txt", "LICENSE-Minhook.txt", "THIRD-PARTY-NOTICES.txt", "version.json"}
NATIVE_FILES = {"VegasCompanion.dll", "VegasDedicated.dll", "VegasInjector.exe", "VegasSteamGate.dll", "VegasSteamControl.exe", "build.json"}


class UpdateError(Exception):
    """Fixed, user-safe update diagnostics."""


def version_tuple(value):
    if not isinstance(value, str) or not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", value):
        raise UpdateError("Invalid release version")
    return tuple(map(int, value.split(".")))


def managed_path(name, version):
    if not isinstance(name, str) or not name or "\\" in name or ":" in name:
        return False
    parts = name.split("/")
    if any(part in ("", ".", "..") or part.endswith((".", " "))
           or re.fullmatch(r"(?i)(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?", part)
           or any(ord(char) < 32 for char in part) for part in parts):
        return False
    if name in ROOT_FILES or name in SEED_FILES:
        return True
    if name.startswith("runtime-" + version + "/"):
        return True
    if name.startswith("local/companion/bin/") and len(parts) == 4:
        return parts[-1] in NATIVE_FILES or bool(re.fullmatch(r"VegasCompanion-[1-9][0-9]*\.dll", parts[-1]))
    return False


@dataclass(frozen=True)
class Release:
    version: str
    archive: str
    sha256: str
    size: int
    files: tuple
    manifest: bytes
    signature: bytes

    @property
    def base_url(self):
        return f"https://github.com/{REPOSITORY}/releases/download/v{self.version}/"


def verify_manifest(data, signature, *, public_key=PUBLIC_KEY_HEX):
    if len(data) > 2 * 1024 * 1024 or len(signature) != 64:
        raise UpdateError("Invalid signed update manifest")
    try:
        Ed25519PublicKey.from_public_bytes(bytes.fromhex(public_key)).verify(signature, data)
    except (ValueError, InvalidSignature):
        raise UpdateError("The update signature could not be verified") from None
    try:
        value = json.loads(data)
        version = value["version"]
        version_tuple(version)
        archive = f"VegasReborn-{version}-win64.zip"
        if (value["schema"] != 1 or value["repository"] != REPOSITORY or value["platform"] != "win64"
                or value["archive"] != archive or type(value["bytes"]) is not int
                or not 0 < value["bytes"] <= MAX_DOWNLOAD
                or not re.fullmatch(r"[0-9a-f]{64}", value["sha256"])):
            raise ValueError()
        files = value["files"]
        if not isinstance(files, list) or not 1 <= len(files) <= 5000:
            raise ValueError()
        seen, total = set(), 0
        for entry in files:
            path = entry["path"]
            if (not managed_path(path, version) or path.casefold() in seen or type(entry["bytes"]) is not int
                    or entry["bytes"] < 0 or not re.fullmatch(r"[0-9a-f]{64}", entry["sha256"])
                    or entry["mode"] != ("seed" if path in SEED_FILES else "replace")):
                raise ValueError()
            seen.add(path.casefold())
            total += entry["bytes"]
        if total > MAX_UNPACKED or not {"vegasreborn.exe", "version.json"}.issubset(seen):
            raise ValueError()
        return Release(version, archive, value["sha256"], value["bytes"], tuple(files), data, signature)
    except (ValueError, TypeError, KeyError, AttributeError):
        raise UpdateError("The signed update manifest is unsupported or unsafe") from None


class ReleaseRedirects(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, url):
        parsed = urllib.parse.urlsplit(url)
        if (parsed.scheme != "https" or parsed.hostname not in
                {"github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com", "github-releases.githubusercontent.com"}
                or parsed.username or parsed.password or parsed.port not in (None, 443)):
            raise UpdateError("The update download was redirected outside GitHub")
        return super().redirect_request(request, fp, code, message, headers, url)


def download(url, *, limit, destination=None):
    request = urllib.request.Request(url, headers={"User-Agent": "Vegas-Reborn-Companion/" + VERSION,
                                                    "Accept": "application/vnd.github+json" if "api.github.com/" in url else "application/octet-stream"})
    # Use Windows CryptoAPI certificate validation (including intermediate
    # chain discovery) without altering process-wide ssl or certificate stores.
    context = truststore.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    opener = urllib.request.build_opener(ReleaseRedirects(), urllib.request.HTTPSHandler(context=context))
    output = None
    chunks, count, deadline = [], 0, time.monotonic() + 180
    try:
        with opener.open(request, timeout=20) as response:
            length = response.headers.get("Content-Length")
            if length and int(length) > limit:
                raise UpdateError("The update download exceeds its size limit")
            if destination:
                output = Path(destination).open("xb")
            while True:
                if time.monotonic() > deadline:
                    raise UpdateError("The update download timed out")
                data = response.read(min(1024 * 1024, limit - count + 1))
                if not data:
                    break
                count += len(data)
                if count > limit:
                    raise UpdateError("The update download exceeds its size limit")
                if output:
                    output.write(data)
                else:
                    chunks.append(data)
        return count if destination else b"".join(chunks)
    except UpdateError:
        raise
    except (OSError, ValueError, urllib.error.URLError):
        raise UpdateError("Could not download the update. Check your connection and retry.") from None
    finally:
        if output:
            output.close()


def check_latest(current=VERSION):
    try:
        value = json.loads(download(f"https://api.github.com/repos/{REPOSITORY}/releases/latest", limit=1024 * 1024))
        version = value["tag_name"].removeprefix("v")
        if value.get("draft") or value.get("prerelease") or version_tuple(version) <= version_tuple(current):
            return None
        base = f"https://github.com/{REPOSITORY}/releases/download/v{version}/"
        assets = {asset["name"]: asset for asset in value["assets"]}
        for name in ("update.json", "update.sig", f"VegasReborn-{version}-win64.zip"):
            if assets[name]["browser_download_url"] != base + name:
                raise ValueError()
        data = download(base + "update.json", limit=2 * 1024 * 1024)
        signature = download(base + "update.sig", limit=64)
        release = verify_manifest(data, signature)
        asset = assets[release.archive]
        if release.version != version or asset["size"] != release.size:
            raise ValueError()
        digest = asset.get("digest")
        if digest is not None and digest != "sha256:" + release.sha256:
            raise ValueError()
        return release
    except UpdateError:
        raise
    except (KeyError, TypeError, ValueError, AttributeError):
        raise UpdateError("GitHub returned an invalid update release") from None


def record_check(root, release):
    """Small public-state diagnostic; never records account or HTTP headers."""
    path = beneath(Path(root).resolve(), ".updates/last-check.json")
    path.parent.mkdir(parents=True, exist_ok=True)
    value = {"checkedVersion": VERSION, "availableVersion": release.version if release else None,
             "status": "available" if release else "up-to-date", "checkedAt": int(time.time())}
    temporary = path.with_name(path.name + ".new")
    temporary.write_text(json.dumps(value), encoding="utf-8")
    os.replace(temporary, path)


def beneath(root, relative):
    target = (root / relative).resolve()
    if not target.is_relative_to(root.resolve()) or target == root.resolve():
        raise UpdateError("An update path escaped the companion folder")
    return target


def digest_file(path):
    result = hashlib.sha256()
    with Path(path).open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            result.update(block)
    return result.hexdigest()


def verify_tree(folder, release):
    expected = {entry["path"] for entry in release.files}
    actual = {path.relative_to(folder).as_posix() for path in Path(folder).rglob("*") if path.is_file()}
    if actual != expected:
        raise UpdateError("The staged update contains missing or unexpected files")
    for entry in release.files:
        path = beneath(folder, entry["path"])
        if not path.is_file() or path.stat().st_size != entry["bytes"] or digest_file(path) != entry["sha256"]:
            raise UpdateError("An update file failed its integrity check")
    version = json.loads((folder / "version.json").read_text(encoding="utf-8-sig"))
    if version.get("version") != release.version:
        raise UpdateError("The update application version does not match")


def extract_archive(archive, folder, release):
    if Path(archive).stat().st_size != release.size or digest_file(archive) != release.sha256:
        raise UpdateError("The update archive failed its integrity check")
    entries = {item["path"]: item for item in release.files}
    seen = set()
    with zipfile.ZipFile(archive) as bundle:
        if len(bundle.infolist()) > 10000:
            raise UpdateError("The update contains too many files")
        for member in bundle.infolist():
            if member.is_dir():
                continue
            if (not member.filename.startswith("VegasReborn/") or member.flag_bits & 1
                    or stat.S_ISLNK(member.external_attr >> 16)):
                raise UpdateError("The update archive contains an unsafe entry")
            name = member.filename[len("VegasReborn/"):]
            if name not in entries or name in seen or not managed_path(name, release.version):
                raise UpdateError("The update archive contains unexpected files")
            entry = entries[name]
            if member.file_size != entry["bytes"]:
                raise UpdateError("An update file size does not match")
            target = beneath(folder, name)
            target.parent.mkdir(parents=True, exist_ok=True)
            with bundle.open(member) as source, target.open("xb") as output:
                shutil.copyfileobj(source, output, 1024 * 1024)
            seen.add(name)
    if seen != set(entries):
        raise UpdateError("The update archive is incomplete")
    verify_tree(folder, release)


def prepare_update(root, release, *, downloader=download):
    root = Path(root).resolve()
    stage = beneath(root, ".updates/staging/" + uuid.uuid4().hex)
    stage.mkdir(parents=True)
    archive, payload = stage / release.archive, stage / "VegasReborn"
    downloader(release.base_url + release.archive, limit=release.size, destination=archive)
    payload.mkdir()
    extract_archive(archive, payload, release)
    plan = {"schema": 1, "root": str(root), "payload": str(payload),
            "manifest": base64.b64encode(release.manifest).decode(), "signature": base64.b64encode(release.signature).decode()}
    path = stage / "plan.json"
    path.write_text(json.dumps(plan), encoding="utf-8")
    return path


def load_plan(path):
    path = Path(path).resolve()
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
        root = Path(value["root"]).resolve()
        stage_root = beneath(root, ".updates/staging")
        payload = Path(value["payload"]).resolve()
        if (value["schema"] != 1 or path.name != "plan.json" or path.parent.parent != stage_root
                or payload != path.parent / "VegasReborn"):
            raise ValueError()
        release = verify_manifest(base64.b64decode(value["manifest"], validate=True), base64.b64decode(value["signature"], validate=True))
        verify_tree(payload, release)
        return root, payload, release
    except UpdateError:
        raise
    except (OSError, KeyError, ValueError, TypeError):
        raise UpdateError("The staged update plan is invalid") from None


def game_processes_running():
    if os.name != "nt":
        return False
    result = subprocess.run(["tasklist", "/FO", "CSV", "/NH"], capture_output=True, timeout=10,
                            creationflags=subprocess.CREATE_NO_WINDOW)
    if result.returncode:
        raise UpdateError("Could not check running games before updating")
    return bool(re.search(rb'(?mi)^"R6Vegas(?:_Game|Server)\.exe"', result.stdout))


def install_payload(root, payload, release, *, active=game_processes_running):
    """Transactionally install only signed managed files; keep rollback copies."""
    root, payload = Path(root).resolve(), Path(payload).resolve()
    verify_tree(payload, release)
    installed = root / "version.json"
    if installed.exists():
        try:
            current = json.loads(installed.read_text(encoding="utf-8-sig"))["version"]
            if version_tuple(release.version) <= version_tuple(current):
                raise UpdateError("This update is not newer than the installed companion")
        except (OSError, ValueError, KeyError, TypeError):
            raise UpdateError("Could not verify the installed companion version") from None
    if active():
        raise UpdateError("Close the game and dedicated server before updating")
    backup = beneath(root, ".updates/backups/" + uuid.uuid4().hex)
    backup.mkdir(parents=True)
    changed = []
    try:
        # Install the executable last; all its new runtime files exist first.
        ordered = sorted(release.files, key=lambda entry: entry["path"] == "VegasReborn.exe")
        for entry in ordered:
            name = entry["path"]
            target = beneath(root, name)
            if entry["mode"] == "seed" and target.exists():
                continue
            source = beneath(payload, name)
            target.parent.mkdir(parents=True, exist_ok=True)
            old = beneath(backup, name) if target.exists() else None
            if old:
                old.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(target, old)
            temporary = target.with_name(target.name + ".update-" + uuid.uuid4().hex)
            try:
                shutil.copy2(source, temporary)
                if digest_file(temporary) != entry["sha256"]:
                    raise UpdateError("An installed update file failed verification")
                os.replace(temporary, target)
                changed.append((target, old))
            finally:
                temporary.unlink(missing_ok=True)
    except Exception as error:
        failed_rollback = False
        for target, old in reversed(changed):
            try:
                if old:
                    shutil.copy2(old, target)
                else:
                    target.unlink(missing_ok=True)
            except OSError:
                failed_rollback = True
        if failed_rollback:
            raise UpdateError("Update failed. Recovery copies are in .updates/backups; account and game data were preserved.") from None
        if isinstance(error, UpdateError):
            raise
        raise UpdateError("Update could not be installed. The previous app was restored; close other companion windows and retry.") from None


def launch_helper(plan, parent_pid):
    root, payload, release = load_plan(plan)
    if game_processes_running():
        raise UpdateError("Close the game and dedicated server before updating")
    executable = payload / "VegasReborn.exe"
    subprocess.Popen([str(executable), "--apply-update", str(plan), "--parent-pid", str(parent_pid)],
                     cwd=payload, creationflags=subprocess.CREATE_NO_WINDOW,
                     env={**os.environ, "PYINSTALLER_RESET_ENVIRONMENT": "1"})


def wait_for_parent(pid, root):
    if os.name != "nt" or pid <= 0:
        raise UpdateError("The updater requires a Windows companion process")
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
    kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel.OpenProcess(0x101000, False, pid)
    if not handle:
        if ctypes.get_last_error() == 87:  # Parent already exited.
            return
        raise UpdateError("Could not wait for the companion to close")
    try:
        # A process object can remain open after exit while its creator still
        # holds a handle. QueryFullProcessImageName may then fail even though
        # OpenProcess succeeded. It is already safe to replace the exited app.
        if kernel.WaitForSingleObject(handle, 0) == 0:
            return
        size, buffer = wintypes.DWORD(32768), ctypes.create_unicode_buffer(32768)
        if not kernel.QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(size)):
            if kernel.WaitForSingleObject(handle, 0) == 0:
                return
            raise UpdateError("Could not identify the companion process before updating")
        if Path(buffer.value).resolve() != Path(root).resolve() / "VegasReborn.exe":
            raise UpdateError("The updater parent is not this companion installation")
        if kernel.WaitForSingleObject(handle, 90000) != 0:
            raise UpdateError("The companion did not close. The update was not installed.")
    finally:
        kernel.CloseHandle(handle)


def apply_update(plan, parent_pid):
    root, payload, release = load_plan(plan)
    wait_for_parent(parent_pid, root)
    lock = beneath(root, ".updates/install.lock")
    import msvcrt
    # Windows releases this byte-range lock if the helper crashes. A stale
    # marker cannot permanently prevent a later update.
    with lock.open("a+b") as owner:
        if not lock.stat().st_size:
            owner.write(b"0")
            owner.flush()
        owner.seek(0)
        try:
            msvcrt.locking(owner.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError:
            raise UpdateError("Another update is already being installed") from None
        try:
            install_payload(root, payload, release)
        finally:
            owner.seek(0)
            msvcrt.locking(owner.fileno(), msvcrt.LK_UNLCK, 1)
    child = subprocess.Popen([str(root / "VegasReborn.exe")], cwd=root, creationflags=subprocess.CREATE_NO_WINDOW,
                             env={**os.environ, "PYINSTALLER_RESET_ENVIRONMENT": "1"})
    beneath(root, ".updates/last-update.json").write_text(json.dumps({"version": release.version, "status": "installed", "restartedPid": child.pid}), encoding="utf-8")
