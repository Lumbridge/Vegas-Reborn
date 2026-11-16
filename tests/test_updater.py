"""Signed, synthetic update fixtures; no game, accounts or GitHub writes."""
import hashlib
import json
import os
from pathlib import Path
import stat
import struct
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
from cryptography.hazmat.primitives import serialization
from vegas_reborn import updater as u


class UpdateTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        # Hosted Windows runners may provide an 8.3 alias in %TEMP%.
        self.root = Path(self.temporary.name).resolve()
        self.key = Ed25519PrivateKey.generate()
        self.public = self.key.public_key().public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw).hex()
        self.contents = {"VegasReborn.exe": b"new companion", "runtime-0.3.1/python310.dll": b"new runtime",
                         "README.txt": b"new readme", "version.json": b'{"version":"0.3.1"}',
                         "local/companion/service.json": b"public defaults"}

    def fixture(self, contents=None, *, zip_hook=None):
        contents = contents or self.contents
        archive = self.root / ("bundle-" + str(len(list(self.root.glob("*.zip")))) + ".zip")
        with zipfile.ZipFile(archive, "w") as bundle:
            for name, data in contents.items():
                bundle.writestr("VegasReborn/" + name, data)
            if zip_hook:
                zip_hook(bundle)
        value = {"schema": 1, "version": "0.3.1", "repository": u.REPOSITORY, "platform": "win64",
                 "archive": "VegasReborn-0.3.1-win64.zip", "bytes": archive.stat().st_size,
                 "sha256": u.digest_file(archive), "files": [
                     {"path": path, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest(),
                      "mode": "seed" if path in u.SEED_FILES else "replace"} for path, data in contents.items()]}
        manifest = json.dumps(value).encode()
        signature = self.key.sign(manifest)
        release = u.verify_manifest(manifest, signature, public_key=self.public)
        return archive, release

    def payload(self):
        archive, release = self.fixture()
        payload = self.root / "payload"
        payload.mkdir()
        u.extract_archive(archive, payload, release)
        return payload, release

    def install_root(self):
        root = self.root / "installed"
        (root / "local/companion/accounts/test/profile").mkdir(parents=True)
        (root / "local/companion/game").mkdir()
        (root / "VegasReborn.exe").write_bytes(b"old companion")
        (root / "version.json").write_bytes(b'{"version":"0.3.0"}')
        (root / "README.txt").write_bytes(b"old readme")
        sentinels = {"local/companion/accounts/test/profile/R6_PECPEC": b"character fixture",
                     "local/companion/account-session.json": b"synthetic session",
                     "local/companion/preferences.json": b"user preferences",
                     "local/companion/game/owned-asset": b"synthetic game sentinel",
                     "local/companion/service.json": b"existing config"}
        for name, data in sentinels.items():
            (root / name).write_bytes(data)
        return root, sentinels

    def test_signature_tampering_and_wrong_key_rejected(self):
        archive, release = self.fixture()
        for data, signature, public in ((release.manifest+b" ", release.signature, self.public),
                                         (release.manifest, bytes(64), self.public),
                                         (release.manifest, release.signature, "00"*32)):
            with self.assertRaises(u.UpdateError):
                u.verify_manifest(data, signature, public_key=public)

    def test_archive_hash_and_extra_entries_rejected(self):
        archive, release = self.fixture()
        archive.write_bytes(archive.read_bytes()+b"tampered")
        with self.assertRaises(u.UpdateError):
            u.extract_archive(archive, self.root/"unpack", release)
        archive, release = self.fixture(zip_hook=lambda bundle: bundle.writestr("VegasReborn/../outside", b"bad"))
        with self.assertRaises(u.UpdateError):
            u.extract_archive(archive, self.root/"unpack2", release)
        self.assertFalse((self.root/"outside").exists())

    def test_local_data_and_windows_path_attacks_rejected_even_when_signed(self):
        paths = ["../outside", "/absolute", "runtime-0.3.1/../outside", "runtime-0.3.1/a:b", "runtime-0.3.1/CON.txt",
                 "runtime-0.3.1/trailing.", "runtime-0.3.1\\escape", "local/companion/account-session.json",
                 "local/companion/accounts/test/profile/R6_PECPEC", "local/companion/game/asset", "server.py"]
        for path in paths:
            with self.subTest(path=path), self.assertRaises(u.UpdateError):
                self.fixture({**self.contents, path: b"bad"})

    def test_duplicate_windows_case_paths_rejected(self):
        with self.assertRaises(u.UpdateError):
            self.fixture({**self.contents, "runtime-0.3.1/PYTHON310.DLL": b"shadow"})

    def test_symlink_archive_entry_rejected(self):
        # Replace a declared entry with a symlink, while keeping the manifest
        # hashes/size signed. Extraction must reject its type before writing.
        contents = dict(self.contents)
        contents.pop("README.txt")
        def link(bundle):
            info = zipfile.ZipInfo("VegasReborn/README.txt")
            info.create_system = 3
            info.external_attr = (stat.S_IFLNK | 0o777) << 16
            bundle.writestr(info, b"target")
        archive, release = self.fixture(contents, zip_hook=link)
        with self.assertRaises(u.UpdateError):
            u.extract_archive(archive, self.root/"symlinks", release)

    def test_install_preserves_accounts_settings_game_and_existing_config(self):
        payload, release = self.payload()
        root, sentinels = self.install_root()
        u.install_payload(root, payload, release, active=lambda: False)
        self.assertEqual((root/"VegasReborn.exe").read_bytes(), b"new companion")
        self.assertEqual(json.loads((root/"version.json").read_text())["version"], "0.3.1")
        for name, data in sentinels.items():
            self.assertEqual((root/name).read_bytes(), data)
        self.assertTrue(list((root/".updates/backups").rglob("VegasReborn.exe")))

    def test_active_game_blocks_all_install_changes(self):
        payload, release = self.payload()
        root, sentinels = self.install_root()
        with self.assertRaisesRegex(u.UpdateError, "Close the game"):
            u.install_payload(root, payload, release, active=lambda: True)
        self.assertEqual((root/"VegasReborn.exe").read_bytes(), b"old companion")
        self.assertFalse((root/"runtime-0.3.1").exists())

    def test_failed_replace_rolls_back_managed_files(self):
        payload, release = self.payload()
        root, sentinels = self.install_root()
        replace = os.replace
        def fail_exe(source, target):
            if Path(target) == root/"VegasReborn.exe":
                raise PermissionError("synthetic sharing violation")
            return replace(source, target)
        with patch.object(u.os, "replace", side_effect=fail_exe), self.assertRaises(u.UpdateError):
            u.install_payload(root, payload, release, active=lambda: False)
        self.assertEqual((root/"README.txt").read_bytes(), b"old readme")
        self.assertEqual((root/"VegasReborn.exe").read_bytes(), b"old companion")
        self.assertEqual(json.loads((root/"version.json").read_text())["version"], "0.3.0")
        for name, data in sentinels.items():
            self.assertEqual((root/name).read_bytes(), data)

    def test_payload_tampering_extra_file_and_downgrade_rejected(self):
        payload, release = self.payload()
        root, sentinels = self.install_root()
        extra=payload/"runtime-0.3.1/unsigned.dll"
        extra.write_bytes(b"unsigned")
        with self.assertRaises(u.UpdateError):
            u.install_payload(root, payload, release, active=lambda: False)
        extra.unlink()
        (root/"version.json").write_text('{"version":"0.3.2"}')
        with self.assertRaisesRegex(u.UpdateError, "not newer"):
            u.install_payload(root, payload, release, active=lambda: False)

    def test_prepare_rechecks_signed_plan_and_does_not_touch_installation(self):
        archive, release = self.fixture()
        root, sentinels = self.install_root()
        def download(url, *, limit, destination):
            self.assertEqual(url, release.base_url+release.archive)
            Path(destination).write_bytes(archive.read_bytes())
        plan = u.prepare_update(root, release, downloader=download)
        verify = u.verify_manifest
        with patch.object(u, "verify_manifest", side_effect=lambda data, signature: verify(data, signature, public_key=self.public)):
            actual, payload, staged = u.load_plan(plan)
            self.assertEqual(actual, root)
            self.assertEqual(staged.version, "0.3.1")
            (payload/"VegasReborn.exe").write_bytes(b"tampered")
            with self.assertRaises(u.UpdateError):
                u.load_plan(plan)
        for name, data in sentinels.items():
            self.assertEqual((root/name).read_bytes(), data)

    def test_redirect_outside_github_rejected(self):
        handler = u.ReleaseRedirects()
        request = u.urllib.request.Request("https://github.com/Lumbridge/Vegas-Reborn/releases/download/v0.3.1/update.json")
        for url in ("http://github.com/unsafe", "https://example.com/payload", "https://user:pass@github.com/payload"):
            with self.subTest(url=url), self.assertRaises(u.UpdateError):
                handler.redirect_request(request, None, 302, "Found", {}, url)


if __name__ == "__main__":
    unittest.main()
