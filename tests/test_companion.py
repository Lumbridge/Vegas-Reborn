"""Companion IPC/lifecycle checks; actual retail acceptance is recorded separately."""
import http.client
from pathlib import Path
import sys
import threading
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from vegas_reborn.companion import CredentialIPC
from vegas_reborn.discord_client import AccountSession, NativeLease


class Client:
    def __init__(self):
        self.calls = 0
        self.fail = False

    def mint_native(self, session):
        self.calls += 1
        if self.fail:
            raise RuntimeError("private-token-must-not-leak")
        return NativeLease("d0123456789abcd", 90, session.discord_id, "Tester", "UseOnce123")


class CompanionIPCTests(unittest.TestCase):
    def setUp(self):
        self.client = Client()
        self.messages = []
        self.ipc = CredentialIPC(AccountSession("12345", "Tester", "a" * 64),
                                 client=self.client, notify=self.messages.append)
        self.addCleanup(self.ipc.close)

    def request(self, *, path="/native-credential", capability=None, body=b"", extra=None):
        connection = http.client.HTTPConnection("127.0.0.1", self.ipc.port, timeout=3)
        headers = {"Authorization": "Bearer " + (capability or self.ipc.capability)}
        headers.update(extra or {})
        try:
            connection.request("POST", path, body=body, headers=headers)
            response = connection.getresponse()
            return response.status, response.read(), dict(response.getheaders())
        finally:
            connection.close()

    def test_capability_required_before_mint(self):
        status, body, _ = self.request(capability="b" * 64)
        self.assertEqual(status, 401)
        self.assertEqual(self.client.calls, 0)
        self.assertNotIn(b"UseOnce123", body)

    def test_only_exact_empty_request_is_accepted(self):
        self.assertEqual(self.request(path="/native-credential?account=123")[0], 404)
        self.assertEqual(self.request(body=b"untrusted-account-id")[0], 401)
        self.assertEqual(self.client.calls, 0)

    def test_valid_request_mints_at_game_login(self):
        self.assertEqual(self.client.calls, 0)
        status, body, headers = self.request()
        self.assertEqual(status, 200)
        self.assertEqual(body, b"d0123456789abcd\nUseOnce123\n")
        self.assertEqual(headers["Cache-Control"], "no-store")
        self.assertEqual(self.client.calls, 1)
        self.assertNotIn("UseOnce123", str(self.messages))

    def test_upstream_failure_is_sanitized(self):
        self.client.fail = True
        status, body, _ = self.request()
        self.assertEqual(status, 503)
        self.assertNotIn(b"private-token", body)
        self.assertNotIn("private-token", str(self.messages))

    def test_concurrent_mint_is_rejected(self):
        self.ipc.lock.acquire()
        try:
            self.assertEqual(self.request()[0], 503)
            self.assertEqual(self.client.calls, 0)
        finally:
            self.ipc.lock.release()

    def test_dedicated_ipc_uses_host_role_without_changing_game_request(self):
        roles = []
        class DedicatedClient:
            def mint_native(self, session, *, dedicated=False):
                roles.append(dedicated)
                return NativeLease("h0123456789abcd", 90, session.discord_id, "Tester", "UseOnce123")
        self.ipc.close()
        self.ipc = CredentialIPC(AccountSession("12345", "Tester", "a" * 64),
                                 client=DedicatedClient(), dedicated=True)
        self.addCleanup(self.ipc.close)
        self.assertEqual(self.request()[:2], (200, b"h0123456789abcd\nUseOnce123\n"))
        self.assertEqual(roles, [True])


if __name__ == "__main__":
    unittest.main()
