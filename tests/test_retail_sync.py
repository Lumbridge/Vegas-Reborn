"""Synthetic codec and fake-account-service checks, not retail interoperability."""
import base64
import copy
import json
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src"))
from vegas_reborn.discord_client import AccountSession
from vegas_reborn.retail_profile import checksum, patch_xp, read_xp
from vegas_reborn.retail_sync import (CloudTransport, MAX_JSON, PEC, SLOTS,
                                    RetailProfileSync, RetailSyncError, SyncConflict)

EQUIP = SLOTS["VegasRetailEquipment_v1"]
SESSION = AccountSession("123456789", "Local Tester", "a" * 64)


def fixture(xp=10, appearance=b"synthetic appearance"):
    raw = struct.pack("<IBI", 0x19, 1, xp) + appearance
    payload = struct.pack("<II", 6, len(raw)) + raw
    return struct.pack("<I", checksum(payload)) + payload


class FakeCloud:
    def __init__(self, files=None, xp=0):
        self.identity = SESSION.discord_id
        self.profile = {"revision": 0, "data": {"schemaVersion": 1,
            "files": {slot: base64.b64encode(value).decode("ascii") for slot, value in (files or {}).items()},
            "preferences": {"remakeSetting": True},
            "progression": {"schemaVersion": 1, "totalExperience": xp}}}
        self.calls = []
        self.conflict = False
        self.ack_xp = 0
        self.on_put = lambda: None

    def request(self, method, path, *, bearer, body=None, timeout=30):
        assert bearer == SESSION.token
        self.calls.append((method, path, copy.deepcopy(body)))
        if path == "/api/v1/account":
            return 200, {"id": self.identity, "name": "Tester"}
        if method == "GET":
            return 200, copy.deepcopy(self.profile)
        if self.conflict or body["revision"] != self.profile["revision"]:
            return 409, {"error": "revision_conflict"}
        self.on_put()
        data = self.profile["data"]
        preserved = {k: v for k, v in data["files"].items() if k not in SLOTS}
        preserved.update(body["data"]["files"])
        data["files"] = preserved
        data["progression"]["totalExperience"] = max(data["progression"]["totalExperience"],
            body["data"]["progression"]["totalExperience"], self.ack_xp)
        self.profile["revision"] += 1
        return 200, copy.deepcopy(self.profile)


class RetailSyncTests(unittest.TestCase):
    def setUp(self):
        (ROOT / "local").mkdir(exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="sync-unit-", dir=ROOT / "local")
        self.addCleanup(self.temp.cleanup)
        self.cloud = FakeCloud()
        self.sync = RetailProfileSync(self.temp.name, transport=self.cloud, game_running=lambda: False)

    def write(self, name, data):
        path = self.sync.profile_directory(SESSION) / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        return path

    def remote(self, name, data):
        slot = next(slot for slot, filename in SLOTS.items() if filename == name)
        self.cloud.profile["data"]["files"][slot] = base64.b64encode(data).decode("ascii")

    def test_identity_before_any_local_write(self):
        self.cloud.identity = "987654321"
        with self.assertRaisesRegex(RetailSyncError, "cloud_identity_mismatch"):
            self.sync.pull(SESSION)
        self.assertEqual(list(Path(self.temp.name).iterdir()), [])
        self.assertEqual(len(self.cloud.calls), 1)

    def test_pull_native_blob_and_canonical_max_without_touching_remake(self):
        native = fixture(100)
        self.remote(PEC, native)
        self.remote(EQUIP, b"opaque equipment")
        self.cloud.profile["data"]["files"]["VegasPEC_v1"] = "UE-opaque-never-decoded"
        self.cloud.profile["data"]["progression"]["totalExperience"] = 500
        result = self.sync.pull(SESSION)
        saved = (self.sync.profile_directory(SESSION) / PEC).read_bytes()
        self.assertEqual(read_xp(saved), 500)
        self.assertEqual(saved[4:17], native[4:17])
        self.assertEqual(saved[21:], native[21:])
        self.assertEqual(result.files_changed, tuple(sorted((PEC, EQUIP))))
        self.assertEqual((self.sync.profile_directory(SESSION) / EQUIP).read_bytes(), b"opaque equipment")
        self.assertEqual(self.cloud.profile["data"]["files"]["VegasPEC_v1"], "UE-opaque-never-decoded")
        self.assertTrue(all(call[0] == "GET" for call in self.cloud.calls))

    def test_no_fabricated_native_character(self):
        self.cloud.profile["data"]["progression"]["totalExperience"] = 500
        self.assertEqual(self.sync.pull(SESSION).experience, 500)
        self.assertFalse((self.sync.profile_directory(SESSION) / PEC).exists())
        with self.assertRaisesRegex(RetailSyncError, "native_profile_missing"):
            self.sync.push(SESSION)

    def test_crc_invalid_local_and_remote_preserve_existing(self):
        path = self.write(PEC, fixture())
        original = path.read_bytes()
        self.remote(PEC, original[:-1] + bytes([original[-1] ^ 1]))
        with self.assertRaisesRegex(RetailSyncError, "cloud_profile_invalid"):
            self.sync.pull(SESSION)
        self.assertEqual(path.read_bytes(), original)
        path.write_bytes(b"invalid local")
        with self.assertRaisesRegex(RetailSyncError, "local_pec_invalid"):
            self.sync.push(SESSION)
        self.assertFalse(any(call[0] == "PUT" for call in self.cloud.calls))

    def test_unsynced_appearance_conflict_keeps_both(self):
        self.remote(PEC, fixture())
        self.sync.pull(SESSION)
        local = fixture(30, b"local unsynced")
        path = self.write(PEC, local)
        remote = fixture(40, b"other machine")
        self.remote(PEC, remote)
        with self.assertRaises(SyncConflict):
            self.sync.pull(SESSION)
        with self.assertRaises(SyncConflict):
            self.sync.push(SESSION)
        self.assertEqual(path.read_bytes(), local)
        self.assertFalse(any(call[0] == "PUT" for call in self.cloud.calls))

    def test_safe_remote_appearance_restore_and_private_backup(self):
        initial = fixture()
        self.remote(PEC, initial)
        self.sync.pull(SESSION)
        replacement = fixture(15, b"saved on other machine")
        self.remote(PEC, replacement)
        self.sync.pull(SESSION)
        self.assertEqual((self.sync.profile_directory(SESSION) / PEC).read_bytes(), replacement)
        backups = list(self.sync.account_directory(SESSION).glob("backups/*/" + PEC))
        self.assertEqual(len(backups), 1)
        self.assertEqual(backups[0].read_bytes(), initial)

    def test_pull_keeps_unsynced_local_when_remote_unchanged(self):
        self.remote(PEC, fixture())
        self.sync.pull(SESSION)
        local = fixture(900, b"new local customization")
        path = self.write(PEC, local)
        self.cloud.profile["data"]["progression"]["totalExperience"] = 1000
        self.sync.pull(SESSION)
        self.assertEqual(path.read_bytes(), patch_xp(local, 1000))
        self.sync.push(SESSION)
        remote = base64.b64decode(self.cloud.profile["data"]["files"]["VegasRetailPEC_v1"])
        self.assertEqual(remote, patch_xp(local, 1000))

    def test_push_preserves_other_family_preferences_and_remote_equipment(self):
        path = self.write(PEC, fixture(600))
        self.remote(EQUIP, b"remote equipment preserved")
        self.cloud.profile["data"]["files"]["VegasPEC_v1"] = "UE receipt ledger bytes"
        result = self.sync.push(SESSION)
        data = self.cloud.profile["data"]
        self.assertEqual(result.experience, 600)
        self.assertEqual(data["files"]["VegasPEC_v1"], "UE receipt ledger bytes")
        self.assertEqual(data["preferences"], {"remakeSetting": True})
        self.assertEqual(base64.b64decode(data["files"]["VegasRetailEquipment_v1"]), b"remote equipment preserved")
        self.assertFalse((path.parent / EQUIP).exists())
        sent = self.cloud.calls[-1][2]
        self.assertEqual(sent["revision"], 0)
        self.assertEqual(set(sent["data"]["files"]), set(SLOTS))
        manifest = (self.sync.account_directory(SESSION) / "sync.json").read_text()
        self.assertNotIn(SESSION.token, manifest)

    def test_acknowledged_higher_xp_updates_only_xp_and_crc(self):
        original = fixture(50)
        path = self.write(PEC, original)
        self.cloud.ack_xp = 700
        self.assertEqual(self.sync.push(SESSION).experience, 700)
        self.assertEqual(path.read_bytes(), patch_xp(original, 700))

    def test_source_changed_during_put_not_overwritten(self):
        path = self.write(PEC, fixture(50))
        edited = fixture(80, b"edited during HTTP")
        self.cloud.on_put = lambda: path.write_bytes(edited)
        self.cloud.ack_xp = 700
        result = self.sync.push(SESSION)
        self.assertTrue(result.local_changed_during_upload)
        self.assertEqual(path.read_bytes(), edited)

    def test_revision_conflict_not_retried_or_forced(self):
        path = self.write(PEC, fixture(50))
        self.cloud.conflict = True
        with self.assertRaisesRegex(SyncConflict, "cloud_revision_conflict"):
            self.sync.push(SESSION)
        self.assertEqual(read_xp(path.read_bytes()), 50)
        self.assertEqual(sum(call[0] == "PUT" for call in self.cloud.calls), 1)
        self.assertFalse((self.sync.account_directory(SESSION) / "sync.json").exists())

    def test_game_started_during_http_blocks_local_save_changes(self):
        original = fixture(50)
        path = self.write(PEC, original)
        running = [False]
        self.sync.game_running = lambda: running[0]
        self.cloud.on_put = lambda: running.__setitem__(0, True)
        self.cloud.ack_xp = 700
        with self.assertRaisesRegex(RetailSyncError, "retail_game_must_be_stopped"):
            self.sync.push(SESSION)
        self.assertEqual(path.read_bytes(), original)
        self.assertFalse((self.sync.account_directory(SESSION) / "sync.json").exists())

    def test_uint32_max_and_bad_schema_types(self):
        self.remote(PEC, fixture(0xffffffff))
        self.assertEqual(self.sync.pull(SESSION).experience, 0xffffffff)
        self.assertEqual(self.sync.push(SESSION).experience, 0xffffffff)
        self.cloud.profile["data"]["progression"]["schemaVersion"] = True
        with self.assertRaisesRegex(RetailSyncError, "cloud_profile_invalid"):
            self.sync.pull(SESSION)

    def test_process_guard_and_path_limits(self):
        sync = RetailProfileSync(self.temp.name, transport=self.cloud, game_running=lambda: True)
        with self.assertRaisesRegex(RetailSyncError, "retail_game_must_be_stopped"):
            sync.pull(SESSION)
        self.assertEqual(list(Path(self.temp.name).iterdir()), [])
        with self.assertRaisesRegex(RetailSyncError, "retail_sync_root_must_be_local"):
            RetailProfileSync(ROOT / "src", transport=self.cloud)

    def test_strict_canonical_and_owner_validation(self):
        for value in (-1, 1 << 32, True, 1.5, "100"):
            self.cloud.profile["data"]["progression"]["totalExperience"] = value
            with self.assertRaisesRegex(RetailSyncError, "cloud_profile_invalid"):
                self.sync.pull(SESSION)
        self.cloud.profile["data"]["progression"]["totalExperience"] = 0
        self.sync.pull(SESSION)
        manifest = self.sync.account_directory(SESSION) / "sync.json"
        data = json.loads(manifest.read_text())
        data["discordId"] = "another account"
        manifest.write_text(json.dumps(data))
        with self.assertRaisesRegex(RetailSyncError, "retail_sync_owner_manifest_invalid"):
            self.sync.push(SESSION)


    def test_explicit_cloud_choice_preserves_local_backup_and_higher_xp(self):
        local = fixture(900, b"local appearance")
        remote = fixture(500, b"cloud appearance")
        self.write(PEC, local)
        self.remote(PEC, remote)
        with self.assertRaises(SyncConflict):
            self.sync.pull(SESSION)
        self.sync.pull(SESSION, replace_local=True)
        result = (self.sync.profile_directory(SESSION) / PEC).read_bytes()
        self.assertEqual(result, patch_xp(remote, 900))
        backups = list((self.sync.account_directory(SESSION) / "backups").rglob(PEC))
        self.assertTrue(any(path.read_bytes() == local for path in backups))

    def test_explicit_local_choice_preserves_cloud_backup_and_revision_check(self):
        local = fixture(500, b"local appearance")
        remote = fixture(1100, b"cloud appearance")
        path = self.write(PEC, local)
        self.remote(PEC, remote)
        with self.assertRaises(SyncConflict):
            self.sync.push(SESSION)
        self.cloud.conflict = True
        with self.assertRaisesRegex(SyncConflict, "cloud_revision_conflict"):
            self.sync.push(SESSION, replace_cloud=True)
        self.assertEqual(path.read_bytes(), local)
        self.cloud.conflict = False
        self.sync.push(SESSION, replace_cloud=True)
        self.assertEqual(path.read_bytes(), patch_xp(local, 1100))
        self.assertEqual(base64.b64decode(self.cloud.profile["data"]["files"]["VegasRetailPEC_v1"]), patch_xp(local, 1100))
        backups = list((self.sync.account_directory(SESSION) / "backups").rglob(PEC))
        self.assertTrue(any(path.read_bytes() == remote for path in backups))


class TransportTests(unittest.TestCase):
    def test_redirects_and_oversized_response_rejected_without_followup(self):
        class Response:
            status = 302
            def read(self, limit):
                return b"x" * limit
        class Connection:
            sock = None
            def __init__(self, *args, **kwargs): pass
            def connect(self): pass
            def request(self, *args, **kwargs): pass
            def getresponse(self): return Response()
            def close(self): pass
        with patch("vegas_reborn.retail_sync.http.client.HTTPSConnection", Connection):
            transport = CloudTransport()
            with self.assertRaisesRegex(RetailSyncError, "cloud_redirect_rejected"):
                transport.request("GET", "/api/v1/account/profile", bearer=SESSION.token)
            Response.status = 200
            with self.assertRaisesRegex(RetailSyncError, "cloud_response_too_large"):
                transport.request("GET", "/api/v1/account", bearer=SESSION.token)
        with self.assertRaisesRegex(RetailSyncError, "invalid_https_cloud_origin"):
            CloudTransport("http://example.test")
        with self.assertRaisesRegex(RetailSyncError, "invalid_cloud_request"):
            CloudTransport().request("GET", "https://attacker.test", bearer=SESSION.token)


if __name__ == "__main__":
    unittest.main()
