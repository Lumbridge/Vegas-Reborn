"""Companion login through the existing master OAuth/link flow.

Discord passwords and OAuth app secrets never enter this client. The existing
master owns OAuth/PKCE. The retail game still uses its recovered native login;
mint a short-lived native lease only when the game is ready to use it.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timezone
import http.client
import json
import os
from pathlib import Path
import re
import socket
import threading
import time
from urllib.parse import urlsplit


DEFAULT_BASE_URL = "https://vegas.lumbridge.co.uk"


class DiscordClientError(Exception):
    """Messages are fixed codes and never include credentials or responses."""


def _base_url(value):
    parsed = urlsplit(value)
    if (parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password
            or parsed.path not in ("", "/") or parsed.query or parsed.fragment):
        raise DiscordClientError("invalid_https_account_origin")
    try:
        port = parsed.port or 443
    except ValueError:
        raise DiscordClientError("invalid_https_account_origin") from None
    if parsed.port == 0:
        raise DiscordClientError("invalid_https_account_origin")
    return parsed.hostname.lower(), port


def _account(value):
    if not isinstance(value, dict):
        raise DiscordClientError("invalid_account_response")
    identifier = value.get("id")
    if type(identifier) is int:
        identifier = str(identifier)
    name = value.get("name")
    if (not isinstance(identifier, str) or not re.fullmatch(r"[0-9]{1,20}", identifier)
            or not 0 < int(identifier) < (1 << 64)
            or not isinstance(name, str) or not name or len(name) > 100 or "\0" in name):
        raise DiscordClientError("invalid_account_response")
    return str(int(identifier)), name


@dataclass(frozen=True)
class AuthLink:
    code: str
    url: str
    expires_in: int
    poll_seconds: float
    deadline: float
    secret: str = field(repr=False)


@dataclass(frozen=True)
class AccountSession:
    discord_id: str
    display_name: str
    token: str = field(repr=False)
    claimed_name: str | None = None
    name_claims_enabled: bool = False
    claim_required: bool = False
    name_change_allowed: str | None = None

    @property
    def rename_on_cooldown(self):
        if self.claim_required or not self.name_change_allowed:
            return False
        return datetime.fromisoformat(self.name_change_allowed.replace("Z", "+00:00")) > datetime.now(timezone.utc)


def _session_from_account(value, token):
    identifier, name = _account(value)
    enabled = value.get("nameClaimsEnabled", False)
    if type(enabled) is not bool:
        raise DiscordClientError("invalid_account_response")
    if not enabled:
        return AccountSession(identifier, name, token)
    claimed, required, allowed = (value.get(key) for key in ("claimedName", "claimRequired", "nameChangeAllowed"))
    if (type(required) is not bool or required != (claimed is None)
            or (claimed is not None and (not isinstance(claimed, str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{2,15}", claimed)))):
        raise DiscordClientError("invalid_account_response")
    if allowed is not None:
        try:
            if not isinstance(allowed, str) or len(allowed) > 64 or datetime.fromisoformat(allowed.replace("Z", "+00:00")).tzinfo is None:
                raise ValueError()
        except ValueError:
            raise DiscordClientError("invalid_account_response") from None
    return AccountSession(identifier, name, token, claimed, True, required, allowed)


@dataclass(frozen=True)
class NativeLease:
    username: str
    expires_in: int
    discord_id: str
    display_name: str
    password: str = field(repr=False)


class HttpTransport:
    """Fixed HTTPS origin with bounded response size; redirects are not followed."""
    def __init__(self, base_url=DEFAULT_BASE_URL):
        self.host, self.port = _base_url(base_url)

    def request(self, method, path, *, body=None, bearer=None, timeout=10):
        parsed = urlsplit(path)
        if (not path.startswith("/") or path.startswith("//") or parsed.scheme or parsed.netloc
                or parsed.query or parsed.fragment or "\0" in path or not 0 < timeout <= 10):
            raise DiscordClientError("invalid_account_request")
        if bearer is not None and not re.fullmatch(r"[0-9A-Fa-f]{64}", bearer):
            raise DiscordClientError("invalid_account_session")
        data = b"" if body is None else json.dumps(body).encode("utf-8")
        headers = {"Accept": "application/json", "Connection": "close"}
        if body is not None:
            headers["Content-Type"] = "application/json"
        if bearer is not None:
            headers["Authorization"] = "Bearer " + bearer
        connection = http.client.HTTPSConnection(self.host, self.port, timeout=timeout)
        live_socket = []

        def terminate():
            current = live_socket[0] if live_socket else connection.sock
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
            live_socket.append(connection.sock)
            connection.request(method, path, body=data, headers=headers)
            response = connection.getresponse()
            if 300 <= response.status < 400:
                raise DiscordClientError("account_redirect_rejected")
            payload = response.read(16385)
            if len(payload) > 16384:
                raise DiscordClientError("account_response_too_large")
            result = json.loads(payload) if payload else None
            return response.status, result
        except DiscordClientError:
            raise
        except Exception:
            raise DiscordClientError("account_request_failed") from None
        finally:
            timer.cancel()
            connection.close()


class DiscordClient:
    def __init__(self, base_url=DEFAULT_BASE_URL, *, transport=None,
                 clock=time.monotonic, sleep=time.sleep):
        self.origin = _base_url(base_url)
        self.transport = transport or HttpTransport(base_url)
        self.clock, self.sleep = clock, sleep

    def begin_link(self):
        started = self.clock()
        status, response = self.transport.request("POST", "/api/v1/account/link", body={}, timeout=10)
        if status != 200 or not isinstance(response, dict):
            raise DiscordClientError("account_link_start_failed")
        code, secret, url = (response.get(name) for name in ("code", "secret", "url"))
        expires, interval = response.get("expiresIn"), response.get("pollSeconds")
        if (not isinstance(code, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,128}", code)
                or not isinstance(secret, str) or not 1 <= len(secret) <= 1024 or "\0" in secret
                or not isinstance(url, str) or type(expires) is not int or expires <= 0
                or type(interval) not in (int, float) or not 0 < interval <= 30):
            raise DiscordClientError("invalid_account_link_response")
        target = urlsplit(url)
        try:
            target_origin = (target.hostname.lower(), target.port or 443) if target.hostname else None
        except ValueError:
            target_origin = None
        if (target.scheme != "https" or target_origin != self.origin or target.username or target.password
                or target.path != "/link/" + code or target.query or target.fragment):
            raise DiscordClientError("account_link_origin_mismatch")
        ttl = min(expires, 300)
        return AuthLink(code, url, ttl, float(interval), started + ttl, secret)

    def wait_for_link(self, link, *, cancel_event=None):
        while True:
            if cancel_event is not None and cancel_event.is_set():
                raise DiscordClientError("account_link_cancelled")
            remaining = link.deadline - self.clock()
            if remaining <= 0:
                raise DiscordClientError("account_link_expired")
            status, response = self.transport.request(
                "POST", "/api/v1/account/link/" + link.code,
                body={"secret": link.secret}, timeout=min(10, remaining))
            if cancel_event is not None and cancel_event.is_set():
                raise DiscordClientError("account_link_cancelled")
            if self.clock() >= link.deadline:
                raise DiscordClientError("account_link_expired")
            if status == 200:
                if not isinstance(response, dict) or not isinstance(response.get("token"), str) or not re.fullmatch(r"[0-9A-Fa-f]{64}", response["token"]):
                    raise DiscordClientError("invalid_account_session")
                return _session_from_account(response.get("account"), response["token"])
            if status != 202:
                raise DiscordClientError("account_link_poll_failed")
            delay = min(link.poll_seconds, max(0, link.deadline - self.clock()))
            if cancel_event is not None:
                if cancel_event.wait(delay):
                    raise DiscordClientError("account_link_cancelled")
            else:
                self.sleep(delay)

    def refresh_account(self, session):
        status, response = self.transport.request("GET", "/api/v1/account", bearer=session.token, timeout=10)
        if status == 401:
            raise DiscordClientError("account_session_expired_or_revoked")
        if status != 200:
            raise DiscordClientError("account_refresh_failed")
        updated = _session_from_account(response, session.token)
        if updated.discord_id != session.discord_id:
            raise DiscordClientError("invalid_account_response")
        return updated

    def claim_name(self, session, name):
        if not isinstance(name, str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{2,15}", name.strip()):
            raise DiscordClientError("invalid_player_name")
        status, response = self.transport.request("POST", "/api/v1/account/name", body={"name": name.strip()}, bearer=session.token, timeout=10)
        if status in (400, 401, 403, 409):
            raise DiscordClientError({400: "invalid_player_name", 401: "account_session_expired_or_revoked",
                                      403: "player_name_change_too_soon", 409: "player_name_taken"}[status])
        if status != 200:
            raise DiscordClientError("player_name_claim_failed")
        updated = _session_from_account(response, session.token)
        if updated.discord_id != session.discord_id or not updated.name_claims_enabled or updated.claim_required:
            raise DiscordClientError("invalid_account_response")
        return updated

    def mint_native(self, session, *, dedicated=False, statistics_codec=""):
        if type(dedicated) is not bool:
            raise DiscordClientError("invalid_native_credential_purpose")
        if statistics_codec not in ("", "native-base-v1"):
            raise DiscordClientError("invalid_statistics_codec")
        path = "/retail/v1/dedicated-session" if dedicated else "/retail/v1/session"
        options = {"body": {"statisticsCodec": statistics_codec}} if statistics_codec else {}
        status, response = self.transport.request("POST", path, bearer=session.token, timeout=10, **options)
        if status == 401:
            raise DiscordClientError("account_session_expired_or_revoked")
        if status != 200 or not isinstance(response, dict):
            raise DiscordClientError("native_credential_mint_failed")
        if statistics_codec and response.get("statisticsCodec") != statistics_codec:
            raise DiscordClientError("statistics_codec_not_acknowledged")
        identifier, name = _account({"id": response.get("discordId"), "name": response.get("displayName")})
        username, password, expires = (response.get(key) for key in ("username", "password", "expiresIn"))
        if (identifier != session.discord_id or not isinstance(username, str)
                or not re.fullmatch(("h" if dedicated else "d") + r"[0-9a-f]{14}", username) or not isinstance(password, str)
                or not re.fullmatch(r"[A-Za-z0-9]{1,15}", password)
                or type(expires) is not int or not 1 <= expires <= 90):
            raise DiscordClientError("invalid_native_credential_response")
        return NativeLease(username, expires, identifier, name, password)


def _write_private(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, "w", encoding="utf-8") as output:
        json.dump(value, output, ensure_ascii=True)
        output.write("\n")
    return path


def save_session(path, session):
    return _write_private(path, {"version": 1, "account": {"id": session.discord_id, "name": session.display_name,
                                 "claimedName": session.claimed_name, "nameClaimsEnabled": session.name_claims_enabled,
                                 "claimRequired": session.claim_required, "nameChangeAllowed": session.name_change_allowed},
                                 "token": session.token, "savedAt": datetime.now(timezone.utc).isoformat()})


def load_session(path):
    path = Path(path)
    if os.name != "nt" and path.stat().st_mode & 0o077:
        raise DiscordClientError("account_session_file_not_private")
    if path.stat().st_size > 16384:
        raise DiscordClientError("invalid_account_session_file")
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(value, dict) or value.get("version") != 1 or not isinstance(value.get("token"), str) or not re.fullmatch(r"[0-9A-Fa-f]{64}", value["token"]):
            raise DiscordClientError("invalid_account_session_file")
        return _session_from_account(value.get("account"), value["token"])
    except DiscordClientError:
        raise
    except Exception:
        raise DiscordClientError("invalid_account_session_file") from None


def save_native_lease(path, lease):
    return _write_private(path, {"version": 1, "username": lease.username, "password": lease.password,
                                 "expiresIn": lease.expires_in, "discordId": lease.discord_id,
                                 "displayName": lease.display_name,
                                 "savedAt": datetime.now(timezone.utc).isoformat()})
