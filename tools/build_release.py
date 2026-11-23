"""Build/sign a companion-only .NET release. Never reads game/account directories.

Layout and constraints: docs/DOTNET_UPDATER.md. The release must pass the 0.4.x
Python updater's manifest checks, so it is verified with both the Python and the
.NET verifiers before it is written.
"""
import argparse
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import zipfile

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src"))
sys.path.insert(0, str(ROOT / "tools"))
from cryptography.hazmat.primitives import serialization
from vegas_reborn.version import REPOSITORY
from vegas_reborn.update_key import PUBLIC_KEY_HEX
from vegas_reborn.updater import SEED_FILES, digest_file, managed_path, verify_manifest
from release_coordination import check_release_source
from release_version import VERSION

DOTNET_ROOT = Path(r"C:\Program Files\dotnet")
FRAMEWORKS = ("Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App")
# Names the 0.4.x updater accepts under local/companion/bin.
BIN_NAMES = ("VegasCompanion.dll", "VegasCompanion-13.dll", "VegasInjector.exe", "VegasSteamGate.dll",
             "VegasSteamControl.exe", "build.json")
# Native modules whose names 0.4.x does not accept there; loaded from runtime-<version>/native.
RUNTIME_NATIVE_NAMES = ("VegasCompanionVegas2.dll",)
TARGET_ARCHIVE = 60 * 1024 * 1024


def git(*args):
    return subprocess.check_output(["git", *args], cwd=ROOT, text=True).strip()


def runtime_version(publish):
    """The exact framework version the published app runs on (latest installed patch)."""
    config = json.loads((publish / "VegasReborn.runtimeconfig.json").read_text(encoding="utf-8")) \
        if (publish / "VegasReborn.runtimeconfig.json").exists() else None
    major = "10.0"
    if config:
        frameworks = config["runtimeOptions"].get("frameworks") or [config["runtimeOptions"]["framework"]]
        major = ".".join(frameworks[0]["version"].split(".")[:2])
    def installed(name):
        return {p.name for p in (DOTNET_ROOT / "shared" / name).iterdir() if re.fullmatch(re.escape(major) + r"\.\d+", p.name)}
    common = installed(FRAMEWORKS[0]) & installed(FRAMEWORKS[1])
    common &= {p.name for p in (DOTNET_ROOT / "host/fxr").iterdir()}
    if not common:
        raise RuntimeError("No matching x64 .NET and Windows Desktop runtime is installed")
    return max(common, key=lambda v: tuple(int(x) for x in v.split(".")))


def build_native(work):
    native = work / "native"
    if native.exists():
        shutil.rmtree(native)
    relative = native.relative_to(ROOT).as_posix()
    script = ROOT / "tools/companion/Build-Companion.ps1"
    for flag in ([], ["-Vegas2"]):
        subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script),
                        "-OutputDirectory", relative, *flag], check=True, cwd=ROOT)
    return native


def publish_app(work, version):
    publish = work / "publish"
    if publish.exists():
        shutil.rmtree(publish)
    subprocess.run(["dotnet", "publish", str(ROOT / "dotnet/src/VegasReborn.App/VegasReborn.App.csproj"),
                    "-c", "Release", "-r", "win-x64", "--self-contained", "false",
                    "-p:PublishSingleFile=true", "-p:AppHostDotNetSearch=AppRelative",
                    "-p:AppHostRelativeDotNet=runtime-" + version + "\\dotnet",
                    "-p:DebugType=none", "-p:Version=" + version,
                    "--artifacts-path", str(work / "artifacts"), "-o", str(publish)], check=True, cwd=ROOT / "dotnet")
    return publish


def notices(runtime):
    parts = []
    bouncy = Path.home() / ".nuget/packages/bouncycastle.cryptography/2.7.0/LICENSE.md"
    for title, path in ((".NET " + runtime + " (Microsoft.NETCore.App, Microsoft.WindowsDesktop.App)", DOTNET_ROOT / "LICENSE.txt"),
                        (".NET third-party notices", DOTNET_ROOT / "ThirdPartyNotices.txt"),
                        ("BouncyCastle.Cryptography 2.7.0", bouncy)):
        parts.append(title + "\n\n" + path.read_text(encoding="utf-8", errors="replace"))
    return "\n\n".join(parts)


def assemble(app, publish, native, runtime, version, args):
    published = sorted(p.name for p in publish.iterdir())
    if published != ["VegasReborn.exe"]:
        raise RuntimeError("Unexpected published files: " + ", ".join(published))
    app.mkdir(parents=True)
    shutil.copy2(publish / "VegasReborn.exe", app / "VegasReborn.exe")
    runtime_root = app / ("runtime-" + version)
    dotnet = runtime_root / "dotnet"
    shutil.copytree(DOTNET_ROOT / "host/fxr" / runtime, dotnet / "host/fxr" / runtime)
    for name in FRAMEWORKS:
        shutil.copytree(DOTNET_ROOT / "shared" / name / runtime, dotnet / "shared" / name / runtime)
    shutil.copy2(DOTNET_ROOT / "LICENSE.txt", dotnet / "LICENSE.txt")
    (runtime_root / "native").mkdir()
    for name in RUNTIME_NATIVE_NAMES:
        shutil.copy2(native / name, runtime_root / "native" / name)
    config = app / "local/companion"
    (config / "bin").mkdir(parents=True)
    for name in ("service.json", "root-public.der"):
        shutil.copy2(args.config_directory / name, config / name)
    for name in BIN_NAMES:
        shutil.copy2(native / name, config / "bin" / name)
    shutil.copy2(ROOT / "third_party/MinHook-LICENSE.txt", app / "LICENSE-Minhook.txt")
    (app / "THIRD-PARTY-NOTICES.txt").write_text(notices(runtime), encoding="utf-8")
    shutil.copy2(ROOT / "docs/PLAYER-README.txt", app / "README.txt")
    (app / "version.json").write_text(json.dumps({"version": version, "repository": REPOSITORY}) + "\n", encoding="utf-8")


def package(app, output, version):
    files = []
    for path in sorted(app.rglob("*")):
        if path.is_file():
            relative = path.relative_to(app).as_posix()
            if not managed_path(relative, version):
                raise RuntimeError("Non-companion file in release: " + relative)
            files.append({"path": relative, "bytes": path.stat().st_size, "sha256": digest_file(path),
                          "mode": "seed" if relative in SEED_FILES else "replace"})
    archive = output / f"VegasReborn-{version}-win64.zip"
    with zipfile.ZipFile(archive, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as bundle:
        for entry in files:
            bundle.write(app / entry["path"], "VegasReborn/" + entry["path"])
    return files, archive


def verify_dotnet(output):
    result = subprocess.run(["dotnet", "run", "--file", str(ROOT / "tools/verify_release.cs"), "--",
                             str(output / "update.json"), str(output / "update.sig")],
                            cwd=ROOT / "tools", capture_output=True, text=True)
    if result.returncode != 0 or result.stdout.strip().splitlines()[-1:] != ["verified " + output.name]:
        raise RuntimeError(".NET manifest verification failed:\n" + result.stdout + result.stderr)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--config-directory", type=Path, default=ROOT / "local/companion")
    parser.add_argument("--signing-key", type=Path, default=ROOT / "local/release/signing-key.pem")
    parser.add_argument("--trial", action="store_true",
                        help="Build an unsigned layout under local/build/trial-<version> from the working tree")
    args = parser.parse_args()
    work = ROOT / "local/build" / (("trial-" if args.trial else "") + VERSION)
    if args.trial:
        commit = git("rev-parse", "HEAD")
        if work.exists():
            shutil.rmtree(work)
    else:
        if git("status", "--porcelain"):
            raise RuntimeError("Commit the reviewed companion sources before building a public release")
        commit = check_release_source(ROOT, VERSION, REPOSITORY)
    output = ROOT / "local/releases" / VERSION if not args.trial else work / "release"
    if output.exists():
        raise RuntimeError("Preserve the existing release; choose a new version")
    key = None
    if not args.trial:
        key = serialization.load_pem_private_key(args.signing_key.read_bytes(), password=None)
        public = key.public_key().public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw).hex()
        if public != PUBLIC_KEY_HEX:
            raise RuntimeError("Signing key does not match the app's pinned verification key")
    work.mkdir(parents=True, exist_ok=True)
    native = build_native(work)
    publish = publish_app(work, VERSION)
    runtime = runtime_version(publish)
    output.mkdir(parents=True)
    app = work / "app/VegasReborn"
    if app.exists():
        shutil.rmtree(app)
    assemble(app, publish, native, runtime, VERSION, args)
    files, archive = package(app, output, VERSION)
    size = archive.stat().st_size
    if size > TARGET_ARCHIVE:
        print(f"warning: archive is {size / 1048576:.1f} MiB, above the 60 MB download target", file=sys.stderr)
    manifest = {"schema": 1, "version": VERSION, "repository": REPOSITORY, "platform": "win64",
                "archive": archive.name, "bytes": size, "sha256": digest_file(archive), "files": files}
    if args.trial:
        print(json.dumps({"layout": str(app), "archive": str(archive), "bytes": size, "files": len(files),
                          "runtime": runtime}))
        return
    if git("status", "--porcelain") or git("rev-parse", "HEAD") != commit:
        raise RuntimeError("Source changed during the build; this candidate must not be signed or published")
    data = (json.dumps(manifest, sort_keys=True, separators=(",", ":")) + "\n").encode()
    signature = key.sign(data)
    verify_manifest(data, signature)
    (output / "update.json").write_bytes(data)
    (output / "update.sig").write_bytes(signature)
    verify_dotnet(output)
    (output / "build-receipt.json").write_text(json.dumps({"version": VERSION, "sourceCommit": commit,
        "updaterOnly": False, "sourceDirty": False, "archiveSha256": manifest["sha256"], "dotnetRuntime": runtime,
        "gameFilesIncluded": False, "accountFilesIncluded": False, "backendIncluded": False}, indent=2), encoding="utf-8")
    print(json.dumps({"archive": str(archive), "sha256": manifest["sha256"], "bytes": size, "sourceCommit": commit}))


if __name__ == "__main__":
    main()
