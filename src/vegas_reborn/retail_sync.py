"""Companion cloud sync for account-isolated original retail save files.

This auxiliary HTTPS account service does not replace the game's native
Agora/Demonware protocol. Retail PEC and UE SaveGame blobs remain separate.
"""
from __future__ import annotations

import base64
from contextlib import contextmanager
from dataclasses import dataclass
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import threading
import uuid

from .discord_client import AccountSession, DEFAULT_BASE_URL, _base_url
from .retail_profile import MAX_BYTES, patch_xp, read_xp

ROOT = Path(__file__).resolve().parents[2]
MAX_JSON = 48 * 1024 * 1024
SLOTS = {"VegasRetailPEC_v1": "R6_PECPEC",
         "VegasRetailEquipment_v1": "R6_EquipEquipmentTemplate"}
PEC = SLOTS["VegasRetailPEC_v1"]


class RetailSyncError(Exception):
    """Fixed error codes only; never include credentials or response bodies."""


class SyncConflict(RetailSyncError):
    pass


@dataclass(frozen=True)
class SyncResult:
    operation: str
    revision: int
    experience: int
    files_changed: tuple[str, ...] = ()
    local_changed_during_upload: bool = False


class CloudTransport:
    """Bounded fixed-origin HTTPS, with no redirects and a total deadline."""
    def __init__(self, base_url=DEFAULT_BASE_URL):
        try:
            self.host, self.port = _base_url(base_url)
        except Exception:
            raise RetailSyncError("invalid_https_cloud_origin") from None

    def request(self, method, path, *, bearer, body=None, timeout=30):
        if ((method, path) not in (("GET", "/api/v1/account"),
                                  ("GET", "/api/v1/account/profile"),
                                  ("PUT", "/api/v1/account/profile"))
                or not isinstance(bearer, str) or not re.fullmatch(r"[A-Fa-f0-9]{64}", bearer)
                or not 0 < timeout <= 60):
            raise RetailSyncError("invalid_cloud_request")
        try:
            payload = b"" if body is None else json.dumps(body, ensure_ascii=True).encode("ascii")
        except (ValueError, TypeError):
            raise RetailSyncError("invalid_cloud_request") from None
        if len(payload) > MAX_JSON:
            raise RetailSyncError("cloud_request_too_large")
        limit = 16384 if path == "/api/v1/account" else MAX_JSON
        connection = http.client.HTTPSConnection(self.host, self.port, timeout=timeout)
        live = []

        def terminate():
            current = live[0] if live else connection.sock
            if current is not None:
                try:
                    current.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
                current.close()

        timer = threading.Timer(timeout, terminate)
        timer.daemon = True
        timer.start()
        try:
            connection.connect()
            live.append(connection.sock)
            headers = {"Authorization": "Bearer " + bearer, "Accept": "application/json",
                       "Connection": "close"}
            if body is not None:
                headers["Content-Type"] = "application/json"
            connection.request(method, path, body=payload, headers=headers)
            response = connection.getresponse()
            if 300 <= response.status < 400:
                raise RetailSyncError("cloud_redirect_rejected")
            data = response.read(limit + 1)
            if len(data) > limit:
                raise RetailSyncError("cloud_response_too_large")
            return response.status, json.loads(data) if data else None
        except RetailSyncError:
            raise
        except Exception:
            raise RetailSyncError("cloud_request_failed") from None
        finally:
            timer.cancel()
            connection.close()


def retail_game_running():
    """Fail closed if process enumeration is unavailable; no game is launched."""
    if os.name == "nt":
        try:
            result = subprocess.run(["tasklist", "/FI", "IMAGENAME eq R6Vegas_Game.exe", "/FO", "CSV", "/NH"],
                                    capture_output=True, timeout=10, creationflags=subprocess.CREATE_NO_WINDOW)
            if result.returncode:
                raise RetailSyncError("cannot_verify_game_stopped")
            return bool(re.search(rb'(?mi)^"R6Vegas_Game\.exe"', result.stdout))
        except RetailSyncError:
            raise
        except Exception:
            raise RetailSyncError("cannot_verify_game_stopped") from None
    proc = Path("/proc")
    if not proc.is_dir():
        raise RetailSyncError("cannot_verify_game_stopped")
    for path in proc.glob("[0-9]*/cmdline"):
        try:
            with path.open("rb") as source:
                if b"r6vegas_game.exe" in source.read(4096).lower():
                    return True
        except (FileNotFoundError, PermissionError, ProcessLookupError):
            continue
    return False


def _hash(data):
    return hashlib.sha256(data).hexdigest() if data is not None else None


def _same_appearance(first, second, name):
    if first is None or second is None:
        return first is second
    if name != PEC:
        return first == second
    # Both buffers are checksum/version validated before reaching this helper.
    # Only the checksum and raw-XP bytes differ when progression alone changed.
    return first[4:17] == second[4:17] and first[21:] == second[21:]


class RetailProfileSync:
    def __init__(self, owned_root=None, *, base_url=DEFAULT_BASE_URL, transport=None,
                 game_running=retail_game_running):
        workspace = ROOT.resolve()
        local = (workspace / "local").resolve()
        self.owned_root = Path(owned_root or local / "retail-accounts").resolve()
        if not local.is_relative_to(workspace) or not self.owned_root.is_relative_to(local):
            raise RetailSyncError("retail_sync_root_must_be_local")
        self.transport = transport or CloudTransport(base_url)
        self.game_running = game_running

    def _safe(self, path):
        path = Path(path)
        if not path.resolve().is_relative_to(self.owned_root):
            raise RetailSyncError("retail_sync_path_escaped")
        current = self.owned_root
        for part in path.relative_to(self.owned_root).parts:
            current = current / part
            if current.is_symlink():
                raise RetailSyncError("retail_sync_symlink_rejected")
        return path

    def account_directory(self, session):
        self._session(session)
        return self._safe(self.owned_root / session.discord_id)

    def profile_directory(self, session):
        return self._safe(self.account_directory(session) / "profile/Ubisoft/R6Vegas")

    @staticmethod
    def _session(session):
        if (not isinstance(session, AccountSession) or not isinstance(session.discord_id, str)
                or not isinstance(session.token, str) or not re.fullmatch(r"[1-9][0-9]{0,19}", session.discord_id)
                or int(session.discord_id) >= 1 << 64 or not re.fullmatch(r"[A-Fa-f0-9]{64}", session.token)):
            raise RetailSyncError("invalid_account_session")

    def _stopped(self):
        try:
            running = self.game_running()
        except Exception:
            raise RetailSyncError("cannot_verify_game_stopped") from None
        if running:
            raise RetailSyncError("retail_game_must_be_stopped")

    def _request(self, session, method, path, *, body=None):
        try:
            return self.transport.request(method, path, bearer=session.token, body=body, timeout=30)
        except RetailSyncError:
            raise
        except Exception:
            raise RetailSyncError("cloud_request_failed") from None

    def _verify_identity(self, session):
        self._session(session)
        status, response = self._request(session, "GET", "/api/v1/account")
        if status != 200 or not isinstance(response, dict):
            raise RetailSyncError("cloud_identity_unverified")
        identity = response.get("id")
        if type(identity) is int:
            identity = str(identity)
        if identity != session.discord_id:
            raise RetailSyncError("cloud_identity_mismatch")

    @contextmanager
    def _locked(self, session):
        self._stopped()
        directory = self.account_directory(session)
        directory.mkdir(parents=True, exist_ok=True, mode=0o700)
        lock = self._safe(directory / ".sync.lock")
        try:
            descriptor = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        except FileExistsError:
            raise RetailSyncError("retail_sync_already_running") from None
        os.close(descriptor)
        try:
            yield
        finally:
            self._safe(lock).unlink(missing_ok=True)

    def _read(self, path, maximum):
        path = self._safe(path)
        if not path.exists():
            return None
        with path.open("rb") as source:
            data = source.read(maximum + 1)
        if len(data) > maximum:
            raise RetailSyncError("local_profile_too_large")
        return data

    def _local(self, session):
        directory = self.profile_directory(session)
        files = {name: self._read(directory / name, MAX_BYTES) for name in SLOTS.values()}
        if files[PEC] is not None:
            try:
                read_xp(files[PEC])
            except ValueError:
                raise RetailSyncError("local_pec_invalid") from None
        return files

    def _manifest(self, session):
        data = self._read(self.account_directory(session) / "sync.json", 16384)
        if data is None:
            return {"version": 1, "discordId": session.discord_id,
                    "revision": 0, "baselineHashes": {}, "remoteHashes": {}}
        try:
            manifest = json.loads(data)
            if (type(manifest["version"]) is not int or manifest["version"] != 1 or manifest["discordId"] != session.discord_id
                    or type(manifest["revision"]) is not int or manifest["revision"] < 0):
                raise ValueError()
            for field in ("baselineHashes", "remoteHashes"):
                if not isinstance(manifest[field], dict):
                    raise ValueError()
                if any(name not in SLOTS.values() or not re.fullmatch(r"[0-9a-f]{64}", value)
                       for name, value in manifest[field].items()):
                    raise ValueError()
            return manifest
        except Exception:
            raise RetailSyncError("retail_sync_owner_manifest_invalid") from None

    @staticmethod
    def _cloud(response):
        try:
            revision, data = response["revision"], response["data"]
            if (type(revision) is not int or revision < 0 or not isinstance(data, dict)
                    or type(data.get("schemaVersion")) is not int or data["schemaVersion"] != 1 or not isinstance(data.get("files"), dict)
                    or not isinstance(data.get("preferences"), dict) or len(data["files"]) > 256
                    or set(data) - {"schemaVersion", "files", "preferences", "progression"}
                    or any(not isinstance(k, str) or not isinstance(v, str) for k, v in data["files"].items())):
                raise ValueError()
            canonical = data.get("progression", {"schemaVersion": 1, "totalExperience": 0})
            if (not isinstance(canonical, dict) or set(canonical) != {"schemaVersion", "totalExperience"}
                    or type(canonical["schemaVersion"]) is not int or canonical["schemaVersion"] != 1 or type(canonical["totalExperience"]) is not int
                    or not 0 <= canonical["totalExperience"] <= 0xffffffff):
                raise ValueError()
            files = {}
            for slot, name in SLOTS.items():
                if slot not in data["files"]:
                    files[name] = None
                    continue
                encoded = data["files"][slot]
                if len(encoded) > ((MAX_BYTES + 2) // 3) * 4:
                    raise ValueError()
                files[name] = base64.b64decode(encoded, validate=True)
                if len(files[name]) > MAX_BYTES:
                    raise ValueError()
            if files[PEC] is not None:
                read_xp(files[PEC])
            return revision, files, canonical["totalExperience"]
        except Exception:
            raise RetailSyncError("cloud_profile_invalid") from None

    def _fetch(self, session):
        status, response = self._request(session, "GET", "/api/v1/account/profile")
        if status != 200:
            raise RetailSyncError("cloud_profile_read_failed")
        return self._cloud(response)

    def _atomic(self, path, data):
        path = self._safe(path)
        path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        temporary = self._safe(path.parent / (".sync-" + uuid.uuid4().hex))
        descriptor = os.open(temporary, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        try:
            with os.fdopen(descriptor, "wb") as output:
                output.write(data)
                output.flush()
                os.fsync(output.fileno())
            os.replace(temporary, path)
        finally:
            temporary.unlink(missing_ok=True)

    def _commit(self, session, before, changes, manifest):
        self._stopped()
        if self._local(session) != before:
            raise SyncConflict("local_profile_changed_during_sync")
        directory = self.profile_directory(session)
        # Backups are durable and private before any original file is replaced.
        changed = {name: data for name, data in changes.items() if data != before[name]}
        if changed:
            backup = self._safe(self.account_directory(session) / "backups" / uuid.uuid4().hex)
            for name in changed:
                if before[name] is not None:
                    self._atomic(backup / name, before[name])
        replaced = []
        try:
            for name, data in changed.items():
                self._stopped()
                self._atomic(directory / name, data)
                replaced.append(name)
            self._stopped()
            self._atomic(self.account_directory(session) / "sync.json",
                         (json.dumps(manifest, sort_keys=True) + "\n").encode("ascii"))
        except Exception:
            for name in reversed(replaced):
                if before[name] is None:
                    self._safe(directory / name).unlink(missing_ok=True)
                else:
                    self._atomic(directory / name, before[name])
            raise
        return tuple(sorted(changed))

    def pull(self, session, *, replace_local=False):
        self._verify_identity(session)
        with self._locked(session):
            manifest, local = self._manifest(session), self._local(session)
            revision, remote, canonical = self._fetch(session)
            xp = max(canonical, read_xp(local[PEC]) if local[PEC] is not None else 0,
                     read_xp(remote[PEC]) if remote[PEC] is not None else 0)
            changes, baseline = {}, dict(manifest["baselineHashes"])
            for name, incoming in remote.items():
                current = local[name]
                if incoming is None:
                    continue  # No implicit deletion or invention of a character.
                safe = (replace_local or current is None or current == incoming
                        or _hash(current) == manifest["baselineHashes"].get(name))
                if safe:
                    changes[name] = incoming
                elif _same_appearance(current, incoming, name):
                    changes[name] = current
                    safe = True
                elif _hash(incoming) != manifest["remoteHashes"].get(name):
                    raise SyncConflict("retail_profile_conflict")
                if safe:
                    baseline[name] = _hash(changes[name])
            selected = changes.get(PEC, local[PEC])
            if selected is not None:
                changes[PEC] = patch_xp(selected, xp)
                if PEC in baseline and (replace_local or local[PEC] is None or _hash(local[PEC]) == manifest["baselineHashes"].get(PEC)
                                        or _same_appearance(local[PEC], remote[PEC], PEC)):
                    baseline[PEC] = _hash(changes[PEC])
            updated = {"version": 1, "discordId": session.discord_id, "revision": revision,
                       "baselineHashes": baseline,
                       "remoteHashes": {name: _hash(data) for name, data in remote.items() if data is not None}}
            changed = self._commit(session, local, changes, updated)
            return SyncResult("pull", revision, xp, changed)

    def push(self, session, *, replace_cloud=False):
        self._verify_identity(session)
        with self._locked(session):
            manifest, local = self._manifest(session), self._local(session)
            if local[PEC] is None:
                raise RetailSyncError("native_profile_missing")
            revision, remote, canonical = self._fetch(session)
            for name, current in local.items():
                if current is None:
                    continue
                if (not replace_cloud and _hash(remote[name]) != manifest["remoteHashes"].get(name)
                        and not _same_appearance(current, remote[name], name)):
                    raise SyncConflict("cloud_profile_changed")
            if replace_cloud:
                # An explicit user choice may replace the other character.
                # Preserve its complete native bytes before attempting the
                # revision-checked upload; identity and CRC checks still apply.
                backup = self.account_directory(session) / "backups" / ("cloud-" + uuid.uuid4().hex)
                for name, value in remote.items():
                    if value is not None:
                        self._atomic(backup / name, value)
            xp = max(canonical, read_xp(local[PEC]), read_xp(remote[PEC]) if remote[PEC] is not None else 0)
            # A missing local equipment file never deletes an existing remote
            # template. The native PEC itself must have been created in-game.
            uploaded = {name: value if value is not None else remote[name] for name, value in local.items()}
            uploaded[PEC] = patch_xp(local[PEC], xp)
            data = {"schemaVersion": 1, "files": {slot: base64.b64encode(uploaded[name]).decode("ascii")
                    for slot, name in SLOTS.items() if uploaded[name] is not None}, "preferences": {},
                    "progression": {"schemaVersion": 1, "totalExperience": xp}}
            status, response = self._request(session, "PUT", "/api/v1/account/profile",
                                             body={"revision": revision, "data": data})
            if status == 409:
                raise SyncConflict("cloud_revision_conflict")
            if status != 200:
                raise RetailSyncError("cloud_profile_upload_failed")
            acknowledged_revision, acknowledged, acknowledged_xp = self._cloud(response)
            if acknowledged_revision != revision + 1 or acknowledged != uploaded:
                raise RetailSyncError("cloud_profile_acknowledgement_invalid")
            final_xp = max(xp, acknowledged_xp)
            final = dict(local)
            final[PEC] = patch_xp(uploaded[PEC], final_xp)
            current = self._local(session)
            changed_during = current != local
            updated = {"version": 1, "discordId": session.discord_id, "revision": acknowledged_revision,
                       "baselineHashes": {name: _hash(value) for name, value in final.items() if value is not None},
                       "remoteHashes": {name: _hash(value) for name, value in acknowledged.items() if value is not None}}
            # A source edited during HTTP remains untouched. Baseline only the
            # uploaded snapshot so a later sync still sees those local changes.
            changed = self._commit(session, current, {} if changed_during else final, updated)
            return SyncResult("push", acknowledged_revision, final_xp, changed, changed_during)
