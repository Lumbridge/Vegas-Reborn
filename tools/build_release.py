"""Build/sign a companion-only release. Never reads game/account directories."""
import argparse
import hashlib
import importlib.metadata
import json
from pathlib import Path
import shutil
import subprocess
import sys
import zipfile

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src"))
from cryptography.hazmat.primitives import serialization
from vegas_reborn.version import VERSION, REPOSITORY
from vegas_reborn.update_key import PUBLIC_KEY_HEX
from vegas_reborn.updater import ROOT_FILES, SEED_FILES, digest_file, managed_path, verify_manifest
from release_coordination import check_release_source


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--native-directory", type=Path, default=ROOT / "local/companion/bin")
    parser.add_argument("--config-directory", type=Path, default=ROOT / "local/companion")
    parser.add_argument("--signing-key", type=Path, default=ROOT / "local/release/signing-key.pem")
    args = parser.parse_args()
    if subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT).strip():
        raise RuntimeError("Commit the reviewed companion sources before building a public release")
    commit = check_release_source(ROOT, VERSION, REPOSITORY)
    output = ROOT / "local/releases" / VERSION
    if output.exists():
        raise RuntimeError("Preserve the existing release; choose a new version")
    key = serialization.load_pem_private_key(args.signing_key.read_bytes(), password=None)
    public = key.public_key().public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw).hex()
    if public != PUBLIC_KEY_HEX:
        raise RuntimeError("Signing key does not match the app's pinned verification key")
    output.mkdir(parents=True)
    command = [sys.executable, "-m", "PyInstaller", "--noconfirm", "--clean", "--onedir", "--windowed",
               "--name", "VegasReborn", "--contents-directory", "runtime-" + VERSION,
               "--icon", str(ROOT / "src/vegas_reborn/ui/assets/vegas-reborn.ico"),
               "--add-data", str(ROOT / "src/vegas_reborn/ui/assets") + ";vegas_reborn/ui/assets",
               "--paths", str(ROOT / "src"), "--distpath", str(output / "dist"),
               "--workpath", str(ROOT / "local/build" / VERSION), "--specpath", str(output),
               str(ROOT / "tools/vegas_companion.py")]
    subprocess.run(command, check=True, cwd=ROOT)
    app = output / "dist/VegasReborn"
    config = app / "local/companion"
    config.mkdir(parents=True)
    for name in ("service.json", "root-public.der"):
        shutil.copy2(args.config_directory / name, config / name)
    native = config / "bin"
    native.mkdir()
    for name in ("VegasCompanion.dll", "VegasCompanion-8.dll", "VegasDedicated.dll", "VegasInjector.exe", "VegasSteamGate.dll", "VegasSteamControl.exe", "build.json"):
        shutil.copy2(args.native_directory / name, native / name)
    shutil.copy2(ROOT / "third_party/MinHook-LICENSE.txt", app / "LICENSE-Minhook.txt")
    shutil.copy2(Path(sys.base_prefix) / "LICENSE.txt", app / "LICENSE-Python.txt")
    notices = []
    for package in ("cryptography", "cffi", "pyinstaller", "truststore", "Pillow"):
        distribution = importlib.metadata.distribution(package)
        for item in distribution.files or ():
            if ".dist-info/" in str(item).replace("\\", "/") and "LICENSE" in item.name.upper():
                path = distribution.locate_file(item)
                if path.is_file():
                    notices.append(f"{package}: {item.name}\n\n" + path.read_text(encoding="utf-8"))
    (app / "THIRD-PARTY-NOTICES.txt").write_text("\n\n".join(notices), encoding="utf-8")
    (app / "README.txt").write_text((ROOT / "docs/PLAYER-README.txt").read_text(encoding="utf-8"), encoding="utf-8")
    (app / "version.json").write_text(json.dumps({"version": VERSION, "repository": REPOSITORY}) + "\n", encoding="utf-8")
    files = []
    for path in sorted(app.rglob("*")):
        if path.is_file():
            relative = path.relative_to(app).as_posix()
            if not managed_path(relative, VERSION):
                raise RuntimeError("Non-companion file in release: " + relative)
            files.append({"path": relative, "bytes": path.stat().st_size, "sha256": digest_file(path),
                          "mode": "seed" if relative in SEED_FILES else "replace"})
    archive = output / f"VegasReborn-{VERSION}-win64.zip"
    with zipfile.ZipFile(archive, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as bundle:
        for entry in files:
            bundle.write(app / entry["path"], "VegasReborn/" + entry["path"])
    manifest = {"schema": 1, "version": VERSION, "repository": REPOSITORY, "platform": "win64",
                "archive": archive.name, "bytes": archive.stat().st_size, "sha256": digest_file(archive), "files": files}
    if (subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT).strip()
            or subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip() != commit):
        raise RuntimeError("Source changed during the build; this candidate must not be signed or published")
    data = (json.dumps(manifest, sort_keys=True, separators=(",", ":")) + "\n").encode()
    signature = key.sign(data)
    verify_manifest(data, signature)
    (output / "update.json").write_bytes(data)
    (output / "update.sig").write_bytes(signature)
    (output / "build-receipt.json").write_text(json.dumps({"version": VERSION, "sourceCommit": commit,
        "sourceDirty": False, "archiveSha256": manifest["sha256"], "gameFilesIncluded": False,
        "accountFilesIncluded": False, "backendIncluded": False}, indent=2), encoding="utf-8")
    print(json.dumps({"archive": str(archive), "sha256": manifest["sha256"], "sourceCommit": commit}))


if __name__ == "__main__":
    main()
