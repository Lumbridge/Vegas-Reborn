"""Reversible startup-movie settings for the companion-owned game copy."""
import json
from pathlib import Path
import re


MOVIE_FILES = ("MovieLoading.ini", "PC/MovieLoading.ini")


def configure_startup_movies(game, skip):
    from .companion import CompanionError, _private_write
    game = Path(game)
    baseline_path = game / ".companion-movies.json"
    if not skip and not baseline_path.exists():
        return
    baseline = json.loads(baseline_path.read_text()) if baseline_path.exists() else {}
    changes = []
    for relative in MOVIE_FILES:
        path = game / "KellerGame/Config" / relative
        if not path.exists():
            continue
        section, found, lines = "", False, []
        for line in path.read_bytes().decode("utf-8").splitlines(keepends=True):
            stripped = line.strip().lstrip("\ufeff")
            if stripped.startswith("["):
                section = stripped.upper()
            match = re.match(r"\s*Enable\s*=\s*(TRUE|FALSE)\b", line, re.IGNORECASE)
            if section == "[INTRO]" and match:
                found = True
                baseline.setdefault(relative, match.group(1))
                value = "FALSE" if skip else baseline[relative]
                line = line[:match.start(1)] + value + line[match.end(1):]
            lines.append(line)
        if not found:
            raise CompanionError("The copied game's startup movie settings are unsupported")
        changes.append((path, "".join(lines).encode("utf-8")))
    if not changes:
        raise CompanionError("The copied game's startup movie settings are missing")
    # Save original values before changing either platform configuration.
    _private_write(baseline_path, json.dumps(baseline).encode())
    for path, data in changes:
        _private_write(path, data)
    if not skip:
        baseline_path.unlink()
