"""Shared worktree reservations and remote checks for signed releases."""
import json
import os
from pathlib import Path
import subprocess


def reserve_version(common, root, version, commit):
    directory = Path(common) / "vegas-release-reservations"
    directory.mkdir(exist_ok=True)
    path = directory / (version + ".json")
    owner = {"version": version, "sourceCommit": commit, "worktree": str(Path(root).resolve())}
    try:
        descriptor = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    except FileExistsError:
        try:
            existing = json.loads(path.read_text())
        except (OSError, ValueError):
            raise RuntimeError("This version reservation is incomplete; do not overwrite it") from None
        if existing != owner:
            raise RuntimeError("This version is reserved by another worktree or commit; choose a new version") from None
    else:
        with os.fdopen(descriptor, "w") as stream:
            json.dump(owner, stream)


def check_release_source(root, version, repository):
    from vegas_reborn.updater import version_tuple
    subprocess.run(["git", "fetch", "origin", "main"], cwd=root, check=True, capture_output=True)
    def git(*args):
        return subprocess.check_output(["git", *args], cwd=root, text=True).strip()
    commit = git("rev-parse", "HEAD")
    if commit != git("rev-parse", "origin/main"):
        raise RuntimeError("Signed releases must be built and published from the current merged origin/main commit")
    latest = subprocess.check_output(["gh", "release", "view", "--repo", repository,
                                      "--json", "tagName", "--jq", ".tagName"], cwd=root, text=True).strip()
    if version_tuple(version) <= version_tuple(latest.removeprefix("v")):
        raise RuntimeError("The release version must be newer than the latest public release")
    common = Path(git("rev-parse", "--git-common-dir"))
    if not common.is_absolute():
        common = Path(root) / common
    reserve_version(common.resolve(), root, version, commit)
    return commit
