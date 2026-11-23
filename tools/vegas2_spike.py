"""Developer launcher for the Rainbow Six Vegas 2 sign-in spike.

Prepares a separate copy of the installed game, routes the original Agora
bootstrap and STUN hosts to an explicit realm with the game-independent
VegasCompanionVegas2.dll adapter, and starts the copy through Steam. Sign in
manually in the game with a test credential from the realm. No game code is
patched. The installed game is read only.

Run it yourself, with the startup-safe private audio route, after building
with: tools/companion/Build-Companion.ps1 -Vegas2 (plus the normal build for
the gate and injector).
"""
import argparse
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src"))
from vegas_reborn.native_launch import run_component, runtime_gate  # noqa: E402

STEAM_APP_ID = "15120"
EXECUTABLE = "Binaries/R6Vegas2_Game.exe"
# Steam build observed on the development machine, 2026-11-23.
CLIENT_SHA256 = "e8ab943062b2e66efad9f3de0e693b35068d3359a9b99fdf390d587cc3284383"
ORIGINAL_SIGNATURE_SHA256 = "bf549ddfc90f626727c9f06405981e8c78170eb47484ba5f20125325c25f7081"
DEFAULT_SOURCE = Path("E:/SteamLibrary/steamapps/common/Rainbow Six Vegas 2")
STEAM = Path("C:/Program Files (x86)/Steam/steam.exe")


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def prepare_copy(source, destination):
    local = (ROOT / "local").resolve()
    if not destination.is_relative_to(local) or source.is_relative_to(destination) or destination.is_relative_to(source):
        raise SystemExit("The game copy must be under local/ and separate from the installation")
    if sha256(source / EXECUTABLE) != CLIENT_SHA256:
        raise SystemExit("This Vegas 2 build is not the one the spike was prepared for")
    marker = destination / ".companion-ready.json"
    if not marker.exists():
        if destination.exists():
            raise SystemExit(f"An unfinished copy exists at {destination}; remove it before retrying")
        print("Copying the game (about 7 GB). The installed game is not modified.", flush=True)

        def ignore(directory, names):
            return [name for name in names if name.lower() in ("session", "memdump.dmp")
                    or name.lower().endswith(".log")]
        shutil.copytree(source, destination, ignore=ignore)
        marker.write_text(json.dumps({"version": 1, "title": "vegas2", "sourceSha256": CLIENT_SHA256}))
    if sha256(destination / EXECUTABLE) != CLIENT_SHA256:
        raise SystemExit("The game copy failed its integrity check")


def install_signature_key(game, key):
    target = game / "KellerGame/Config/PC/signature.pub"
    if key is None:
        if sha256(target) != ORIGINAL_SIGNATURE_SHA256:
            raise SystemExit("The copy has a replaced signature.pub; pass --signature-key or recreate the copy")
        return "original"
    data = Path(key).read_bytes()
    # The original is a 270-byte DER RSA-2048 public key.
    if not 200 <= len(data) <= 600 or data[0] != 0x30:
        raise SystemExit("--signature-key must be a DER-encoded RSA public key")
    target.write_bytes(data)
    return hashlib.sha256(data).hexdigest()


KEYID_SIZE = 24  # SADS 0x113ea5b0 copies 0x18 bytes; the key text is NUL terminated.
KEYID_RC4_KEY = b"Tigu"  # SADS 0x113ea57f: first 4 bytes of "TiguiLaLune".


def rc4(key, data):
    state = list(range(256))
    j = 0
    for i in range(256):
        j = (j + state[i] + key[i % len(key)]) & 0xff
        state[i], state[j] = state[j], state[i]
    i = j = 0
    output = bytearray()
    for byte in data:
        i = (i + 1) & 0xff
        j = (j + state[i]) & 0xff
        state[i], state[j] = state[j], state[i]
        output.append(byte ^ state[(state[i] + state[j]) & 0xff])
    return bytes(output)


def encode_keyid(cd_key):
    """Encode a CD key as the client's [CDKEY] KEYID value.

    Writer SADS 0x113e9ed7: RC4 (key "Tigu") over the 24-byte key buffer, then
    lower-case hex (0x113e9bc0, "%s%2x"). Reader 0x113ea54d reverses it. The
    decrypted text is the key the client signs ticket proofs with.
    """
    plain = cd_key.encode("ascii")
    if len(plain) >= KEYID_SIZE:
        raise ValueError(f"the CD key must be at most {KEYID_SIZE - 1} characters")
    return rc4(KEYID_RC4_KEY, plain.ljust(KEYID_SIZE, b"\0")).hex()


def install_cd_key(game, profile, cd_key):
    # The client stores the key as [CDKEY] KEYID in PlayerProfile.ini (SADS
    # 0x113ea52e). Write both the shipped config file in the copy and the
    # redirected profile location.
    if cd_key is None:
        return
    if not re.fullmatch(rf"[\x21-\x7e]{{1,{KEYID_SIZE - 1}}}", cd_key):
        raise SystemExit(f"--cd-key must be 1-{KEYID_SIZE - 1} printable ASCII characters")
    text = f"[CDKEY]\nKEYID={encode_keyid(cd_key)}\n"
    (game / "KellerGame/Config/PC/PlayerProfile.ini").write_text(text, encoding="ascii")
    target = profile / "My Games/Ubisoft/R6Vegas2"
    target.mkdir(parents=True, exist_ok=True)
    (target / "PlayerProfile.ini").write_text(text, encoding="ascii")


def game_running():
    rows = subprocess.check_output(["tasklist", "/FI", "IMAGENAME eq R6Vegas2_Game.exe", "/FO", "CSV", "/NH"],
                                   creationflags=subprocess.CREATE_NO_WINDOW).decode(errors="replace")
    return "R6Vegas2_Game.exe" in rows


def steam_pid():
    rows = subprocess.check_output(["tasklist", "/FI", "IMAGENAME eq steam.exe", "/FO", "CSV", "/NH"],
                                   creationflags=subprocess.CREATE_NO_WINDOW).decode(errors="replace")
    match = re.search(r'(?mi)^"steam\.exe","([0-9]+)"', rows)
    if not match:
        raise SystemExit("Start Steam and sign in first")
    return int(match[1])


def wait_for(path, seconds, message):
    deadline = time.monotonic() + seconds
    while not path.exists():
        if time.monotonic() > deadline:
            raise SystemExit(message)
        time.sleep(.1)


class TestCredentialServer:
    """Serves one realm test credential on the adapter's credential IPC route.

    Stands in for the companion app's /native-credential endpoint so the
    Discord sign-in hooks can be tested against a local fixture master.
    """

    def __init__(self, path):
        import http.server
        import threading
        entry = json.loads(Path(path).read_text())["signIns"][0]
        self.username, password = entry["username"], entry["password"]
        if not re.fullmatch(r"[A-Za-z0-9]{15}", self.username) or not re.fullmatch(r"[A-Za-z0-9]{1,15}", password):
            raise SystemExit("The test credential does not match the native credential format")
        self.capability = secrets.token_hex(32)
        self.requests = 0
        owner, body = self, f"{self.username}\n{password}\n".encode("ascii")

        class Handler(http.server.BaseHTTPRequestHandler):
            def do_POST(self):
                ok = (self.path == "/native-credential" and
                      self.headers.get("Authorization") == "Bearer " + owner.capability)
                owner.requests += ok
                self.send_response(200 if ok else 403)
                self.send_header("Content-Length", str(len(body) if ok else 0))
                self.end_headers()
                if ok:
                    self.wfile.write(body)

            def log_message(self, *args):
                pass

        self.server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.port = self.server.server_address[1]
        threading.Thread(target=self.server.serve_forever, daemon=True).start()


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE)
    parser.add_argument("--route-ip", default="127.0.0.1", help="Realm IPv4 address")
    parser.add_argument("--route-port", type=int, default=3080, help="Realm TCP port (default 3080)")
    parser.add_argument("--signature-key", type=Path, help="DER public key that replaces signature.pub in the copy")
    parser.add_argument("--cd-key", help="Realm test CD key written to PlayerProfile.ini [CDKEY] KEYID")
    parser.add_argument("--bin", type=Path, default=ROOT / "local/companion/bin")
    parser.add_argument("--work", type=Path, default=ROOT / "local/vegas2")
    parser.add_argument("--test-credential", type=Path,
                        help="Realm fixture signin.json served on the credential IPC route; "
                             "keeps running until the game exits")
    args = parser.parse_args()
    if os.name != "nt":
        raise SystemExit("Windows only")
    ipaddress.IPv4Address(args.route_ip)
    if not 0 < args.route_port < 65536:
        raise SystemExit("Invalid realm port")
    source, work = args.source.resolve(), args.work.resolve()
    game, run = work / "game", work / "run"
    args.bin = args.bin.resolve()
    adapter = args.bin / "VegasCompanionVegas2.dll"
    for name in ("VegasSteamControl.exe", "VegasSteamGate.dll", "VegasInjector.exe", adapter.name):
        if not (args.bin / name).is_file():
            raise SystemExit(f"Missing {args.bin / name}; build the companion first")
    if game_running():
        raise SystemExit("Close the running Vegas 2 game first")

    prepare_copy(source, game)
    key_state = install_signature_key(game, args.signature_key)
    run.mkdir(parents=True, exist_ok=True)
    install_cd_key(game, run / "profile", args.cd_key)
    for name in ("child.json", "compat-status.json"):
        (run / name).unlink(missing_ok=True)
    # The adapter reads the shared route format. Without --test-credential the
    # credential IPC fields are inert placeholders and sign-in is manual.
    credentials = TestCredentialServer(args.test_credential) if args.test_credential else None
    alias = credentials.username if credentials else "v" + secrets.token_hex(7)
    ipc_port = credentials.port if credentials else 1
    capability = credentials.capability if credentials else secrets.token_hex(32)
    (run / "native-route.txt").write_text(
        f"{args.route_ip}\n{args.route_port}\n{ipc_port}\n{capability}\n{alias}\n", encoding="ascii")
    launch = run / "launch.utf16"
    launch.write_bytes((str(source / EXECUTABLE).replace("/", "\\") + "\n" + str(game / "Binaries") +
                        "\nwinsta0\\Default\n" + str(run)).encode("utf-16-le"))

    pid = steam_pid()
    control = args.bin / "VegasSteamControl.exe"
    gate_dll = runtime_gate(args.bin / "VegasSteamGate.dll",
                            Path(os.environ["LOCALAPPDATA"]) / "VegasReborn" / "LaunchCache")
    gate = run_component([control, "--configure", pid, gate_dll, "StartGate", launch])
    try:
        subprocess.Popen([str(STEAM), "-applaunch", STEAM_APP_ID], creationflags=subprocess.CREATE_NO_WINDOW)
        wait_for(run / "child.json", 45, "Steam did not start the game")
        child = json.loads((run / "child.json").read_text())
        run_component([args.bin / "VegasInjector.exe", child["pid"], child["tid"], adapter, run, game, source])
    finally:
        run_component([control, "--stop-gate", pid, gate["module"], gate_dll])
    wait_for(run / "compat-status.json", 90, "The adapter did not report readiness")
    status = json.loads((run / "compat-status.json").read_text())
    print(json.dumps({"adapter": status, "signatureKey": key_state, "realm": f"{args.route_ip}:{args.route_port}",
                      "profile": str(run / "profile")}, indent=2))
    if not status.get("ready"):
        raise SystemExit("The adapter failed to install its hooks")
    if credentials is None:
        print("Game started. Choose Multiplayer > Online and sign in with a realm test credential.")
        return
    print("Game started. Online Login is filled in; the test credential is supplied at login.", flush=True)
    while game_running():
        time.sleep(1)
    print(json.dumps({"credentialRequests": credentials.requests}))


if __name__ == "__main__":
    main()
