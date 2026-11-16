import json
import os
from pathlib import Path
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from vegas_reborn.discord_client import (AccountSession, DiscordClient, DiscordClientError,
                                      HttpTransport, NativeLease, load_session,
                                      save_native_lease, save_session)

ORIGIN = "https://vegas.example.invalid"
TOKEN = "a" * 64
ACCOUNT = {"id": "123456789012345678", "name": "Example"}
LINK = {"code": "ABCD12", "secret": "private-poll-secret", "url": ORIGIN + "/link/ABCD12",
        "expiresIn": 300, "pollSeconds": 3}


class FakeClock:
    def __init__(self):
        self.now = 100.0
    def __call__(self):
        return self.now
    def sleep(self, seconds):
        self.now += seconds


class FakeTransport:
    def __init__(self, responses):
        self.responses = list(responses)
        self.calls = []
    def request(self, method, path, **kwargs):
        self.calls.append((method, path, kwargs))
        return self.responses.pop(0)


class DiscordClientTests(unittest.TestCase):
    def test_claim_metadata_refresh_explicit_claim_and_legacy_fallback(self):
        session = AccountSession(ACCOUNT["id"], ACCOUNT["name"], TOKEN)
        unclaimed = {**ACCOUNT, "nameClaimsEnabled": True, "claimedName": None, "claimRequired": True, "nameChangeAllowed": None}
        claimed = {**unclaimed, "claimedName": "Test_Name", "claimRequired": False, "nameChangeAllowed": "2026-12-16T10:00:00Z"}
        transport = FakeTransport([(200, unclaimed), (200, claimed), (200, ACCOUNT)])
        client = DiscordClient(ORIGIN, transport=transport)
        pending = client.refresh_account(session)
        self.assertTrue(pending.claim_required)
        self.assertTrue(pending.name_claims_enabled)
        result = client.claim_name(pending, " Test_Name ")
        self.assertEqual(result.discord_id, session.discord_id)
        self.assertEqual(result.display_name, session.display_name)
        self.assertEqual(result.claimed_name, "Test_Name")
        self.assertFalse(result.claim_required)
        self.assertEqual(transport.calls[1], ("POST", "/api/v1/account/name", {"body": {"name": "Test_Name"}, "bearer": TOKEN, "timeout": 10}))
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "session.json"
            save_session(path, result)
            self.assertEqual(load_session(path), result)
        self.assertEqual(client.refresh_account(result), session)

    def test_claim_validation_errors_and_identity_confusion_fail_closed(self):
        session = AccountSession(ACCOUNT["id"], ACCOUNT["name"], TOKEN)
        for name in ("ry", "", "bad name", "_invalid", "Ã¥bc"):
            transport = FakeTransport([])
            with self.assertRaisesRegex(DiscordClientError, "invalid_player_name"):
                DiscordClient(ORIGIN, transport=transport).claim_name(session, name)
            self.assertEqual(transport.calls, [])
        for status, code in ((400, "invalid_player_name"), (401, "account_session_expired_or_revoked"),
                             (403, "player_name_change_too_soon"), (409, "player_name_taken")):
            with self.assertRaisesRegex(DiscordClientError, code):
                DiscordClient(ORIGIN, transport=FakeTransport([(status, {"error": "not printed"})])).claim_name(session, "ValidName")
        for response in ({**ACCOUNT, "id": "2"}, {**ACCOUNT, "nameClaimsEnabled": "yes"},
                         {**ACCOUNT, "nameClaimsEnabled": True, "claimedName": None, "claimRequired": False},
                         {**ACCOUNT, "nameClaimsEnabled": True, "claimedName": "ValidName", "claimRequired": False, "nameChangeAllowed": "invalid"}):
            with self.assertRaisesRegex(DiscordClientError, "invalid_account_response"):
                DiscordClient(ORIGIN, transport=FakeTransport([(200, response)])).refresh_account(session)

    def test_dedicated_mint_uses_explicit_route_and_rejects_role_confusion(self):
        session = AccountSession(ACCOUNT["id"], ACCOUNT["name"], TOKEN)
        host = {"username": "h0123456789abcd", "password": "Temporary123AbC", "expiresIn": 90,
                "discordId": ACCOUNT["id"], "displayName": ACCOUNT["name"]}
        transport = FakeTransport([(200, host)])
        lease = DiscordClient(ORIGIN, transport=transport).mint_native(session, dedicated=True)
        self.assertEqual(lease.username, host["username"])
        self.assertEqual(transport.calls, [("POST", "/retail/v1/dedicated-session", {"bearer": TOKEN, "timeout": 10})])
        self.assertNotIn(lease.password, repr(lease))
        for dedicated, response in ((False, host), (True, {**host, "username": "d0123456789abcd"}),
                                     (True, {**host, "discordId": "2"})):
            with self.assertRaisesRegex(DiscordClientError, "invalid_native_credential_response"):
                DiscordClient(ORIGIN, transport=FakeTransport([(200, response)])).mint_native(session, dedicated=dedicated)
        for role in (None, "dedicated", 1):
            transport = FakeTransport([])
            with self.assertRaisesRegex(DiscordClientError, "invalid_native_credential_purpose"):
                DiscordClient(ORIGIN, transport=transport).mint_native(session, dedicated=role)
            self.assertEqual(transport.calls, [])
        with self.assertRaisesRegex(DiscordClientError, "account_session_expired_or_revoked"):
            DiscordClient(ORIGIN, transport=FakeTransport([(401, {})])).mint_native(session, dedicated=True)

    def test_existing_link_poll_and_later_native_mint(self):
        native = {"username": "d0123456789abcd", "password": "Temporary123AbC", "expiresIn": 90,
                  "discordId": ACCOUNT["id"], "displayName": ACCOUNT["name"]}
        transport = FakeTransport([(200, LINK), (202, None),
                                   (200, {"token": TOKEN, "account": ACCOUNT}), (200, native)])
        clock = FakeClock()
        client = DiscordClient(ORIGIN, transport=transport, clock=clock, sleep=clock.sleep)
        link = client.begin_link()
        self.assertEqual(transport.calls[0][1:], ("/api/v1/account/link", {"body": {}, "timeout": 10}))
        session = client.wait_for_link(link)
        self.assertEqual(session.discord_id, ACCOUNT["id"])
        self.assertNotIn(TOKEN, repr(session))
        self.assertNotIn(LINK["secret"], repr(link))
        self.assertEqual(clock.now, 103)
        for _, path, options in transport.calls[1:]:
            self.assertEqual(path, "/api/v1/account/link/ABCD12")
            self.assertEqual(options["body"], {"secret": LINK["secret"]})
            self.assertNotIn(LINK["secret"], path)
        lease = client.mint_native(session)
        self.assertEqual(lease.username, native["username"])
        self.assertNotIn(native["password"], repr(lease))
        _, path, options = transport.calls[-1]
        self.assertEqual(path, "/retail/v1/session")
        self.assertEqual(options["bearer"], TOKEN)
        self.assertNotIn("body", options)

    def test_same_origin_link_required_before_any_browser_handoff(self):
        for url in ("http://vegas.example.invalid/link/ABCD12", "https://elsewhere.invalid/link/ABCD12",
                    ORIGIN + "/link/ABCD12?token=private", ORIGIN + "/link/WRONG"):
            transport = FakeTransport([(200, {**LINK, "url": url})])
            with self.assertRaises(DiscordClientError):
                DiscordClient(ORIGIN, transport=transport).begin_link()
            self.assertEqual(len(transport.calls), 1)
        for origin in ("http://vegas.example.invalid", "https://user:password@vegas.example.invalid",
                       ORIGIN + "/api", ORIGIN + ":0"):
            with self.assertRaises(DiscordClientError):
                DiscordClient(origin, transport=FakeTransport([]))

    def test_pending_polling_expires_after_five_minutes_and_cancels(self):
        clock = FakeClock()
        transport = FakeTransport([(200, {**LINK, "expiresIn": 3600, "pollSeconds": 30})]
                                   + [(202, None)] * 10)
        client = DiscordClient(ORIGIN, transport=transport, clock=clock, sleep=clock.sleep)
        link = client.begin_link()
        self.assertEqual(link.expires_in, 300)
        with self.assertRaisesRegex(DiscordClientError, "account_link_expired"):
            client.wait_for_link(link)
        self.assertEqual(clock.now, 400)
        self.assertEqual(len(transport.calls), 11)
        cancel = threading.Event()
        cancel.set()
        with self.assertRaisesRegex(DiscordClientError, "account_link_cancelled"):
            client.wait_for_link(link, cancel_event=cancel)
        self.assertEqual(len(transport.calls), 11)

    def test_bearer_failure_and_cross_account_native_lease_rejected(self):
        session = AccountSession(ACCOUNT["id"], ACCOUNT["name"], TOKEN)
        transport = FakeTransport([(401, {"error": "not authorized"})])
        with self.assertRaisesRegex(DiscordClientError, "account_session_expired_or_revoked"):
            DiscordClient(ORIGIN, transport=transport).mint_native(session)
        forged = {"username": "d0123456789abcd", "password": "Temporary123AbC", "expiresIn": 90,
                  "discordId": "2", "displayName": "Different"}
        with self.assertRaisesRegex(DiscordClientError, "invalid_native_credential_response"):
            DiscordClient(ORIGIN, transport=FakeTransport([(200, forged)])).mint_native(session)

    def test_private_session_and_lease_files_are_new_and_secret_safe_in_repr(self):
        session = AccountSession(ACCOUNT["id"], ACCOUNT["name"], TOKEN)
        lease = NativeLease("d0123456789abcd", 90, ACCOUNT["id"], ACCOUNT["name"], "Temporary123AbC")
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "session.json"
            save_session(path, session)
            self.assertEqual(load_session(path), session)
            original = path.read_bytes()
            with self.assertRaises(FileExistsError):
                save_session(path, session)
            self.assertEqual(path.read_bytes(), original)
            lease_path = save_native_lease(Path(directory) / "lease.json", lease)
            self.assertEqual(json.loads(lease_path.read_text())["password"], lease.password)
            if os.name != "nt":
                self.assertEqual(path.stat().st_mode & 0o777, 0o600)
                self.assertEqual(lease_path.stat().st_mode & 0o777, 0o600)

    def test_http_transport_never_follows_redirect_or_prints_response(self):
        class Response:
            status = 302
        class Connection:
            sock = None
            def connect(self): pass
            def request(self, *_args, **_kwargs): pass
            def getresponse(self): return Response()
            def close(self): pass
        with patch("vegas_reborn.discord_client.http.client.HTTPSConnection", return_value=Connection()) as constructor:
            with self.assertRaisesRegex(DiscordClientError, "account_redirect_rejected"):
                HttpTransport(ORIGIN).request("POST", "/retail/v1/session", bearer=TOKEN)
            self.assertEqual(constructor.call_count, 1)
