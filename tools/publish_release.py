"""Publish an already-built, signed companion release using authenticated gh."""
import json
from pathlib import Path
import subprocess
import sys

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/"src"))
sys.path.insert(0,str(ROOT/"tools"))
from vegas_reborn.version import REPOSITORY
from release_version import VERSION
from vegas_reborn.updater import verify_manifest, digest_file
from release_coordination import check_release_source


def main():
    if len(sys.argv)!=2:
        raise SystemExit("Usage: publish_release.py RELEASE_NOTES_FILE")
    output=ROOT/"local/releases"/VERSION
    release=verify_manifest((output/"update.json").read_bytes(),(output/"update.sig").read_bytes())
    receipt=json.loads((output/"build-receipt.json").read_text())
    current=subprocess.check_output(["git","rev-parse","HEAD"],cwd=ROOT,text=True).strip()
    if receipt["sourceCommit"]!=current or receipt["sourceDirty"] or subprocess.check_output(["git","status","--porcelain"],cwd=ROOT).strip():
        raise RuntimeError("Source must match the reviewed clean release commit")
    if digest_file(output/release.archive)!=release.sha256:
        raise RuntimeError("Release archive failed verification")
    check_release_source(ROOT, VERSION, REPOSITORY)
    artifacts=[str(output/name) for name in (release.archive,"update.json","update.sig","build-receipt.json")]
    subprocess.run(["gh","release","create","v"+VERSION,"--repo",REPOSITORY,"--target",current,
                    "--title","Vegas Reborn Companion "+VERSION,"--notes-file",str(Path(sys.argv[1]).resolve()),*artifacts],check=True,cwd=ROOT)


if __name__=="__main__":main()
