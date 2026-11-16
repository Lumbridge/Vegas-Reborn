# Development and releases

For installation and playing instructions, see the [README](../README.md).

Requires Python 3.10+, Windows, Visual Studio C++ build tools and Windows SDK.

```powershell
python -m venv .venv
.venv/Scripts/python.exe -m pip install -r requirements-build.txt
.venv/Scripts/python.exe -m unittest discover -s tests -v
tools/companion/Build-Companion.ps1
tools/companion/Build-Companion.ps1 -Dedicated
tools/companion/Test-Native.ps1
```

MinHook is fetched at its pinned commit into ignored `local/dependencies`.
Public launch configuration (`service.json` and `root-public.der`) belongs in
`local/companion`, obtainable from a published companion ZIP. Do not use a real
retail key in a configuration or commit a user's account session.

```powershell
.venv/Scripts/python.exe tools/vegas_companion.py
```

## Desktop presentation

`entrypoint.py` handles CLI/update routing, `application.py` coordinates account,
launch and synchronization operations, and `ui/` owns the pages, theme and
graphics. Background workers communicate with the UI through queued events.
The normal navigation has Overview, Account, Display & Game, Connections and
Updates. Dedicated hosting is a separate operator tool (`tools/host_server.py`);
the legacy explicit `--host` path remains available.

UI text is functional: no slogans or quips. Page changes preserve window size
and position. Content scrolls inside the fixed content area. Button styles must
set normal, hover, pressed and disabled background colors explicitly so theme
defaults cannot show through rounded corners.

`tools/render_ui_preview.py` renders its own synthetic Tk window without capturing
the desktop or game. Use `--compact` for a separate compact-size preview run;
the tool never resizes the window while switching pages. Output goes to ignored
`local/design-preview`. Original generated artwork and its prompt/provenance are
in `src/vegas_reborn/ui/assets`, and icons are drawn by `ui/graphics.py`.

## Release process

Windowed mode explicitly maintains the original framed Win32 style on the owned
game window. Frame repair preserves the current client area and window position;
later maintenance leaves ordinary user moves and resizes alone. It also demotes
topmost windows without activating them. Synthetic Win32 checks cover a real
caption hit-test, client dimensions, repeated maintenance, movement and recovery
after a borderless style reset. Retail rendering, title-bar dragging during play
and Alt-Tab still require a separate original-client acceptance run with the
required startup-safe private audio route.

The version is defined in `src/vegas_reborn/version.py`. Keep `pyproject.toml` in
sync. Merge and test all intended changes, then build from the exact current
`origin/main` commit; dirty and unmerged sources are rejected. Build and publish
tools reserve the version atomically in the common Git directory, shared by all
linked worktrees. A reservation belongs to one worktree and commit; use a new
version if a different candidate is needed. Never remove another reservation.
Only install published releases in the user's normal app. Keep candidate builds
in isolated acceptance directories. Run `tools/companion/Package-Companion.ps1`, then publish the ZIP,
`update.json`, `update.sig` and `build-receipt.json` through `tools/publish_release.py`.
The release feed is the repository's latest stable release. Preview releases are
not offered by the updater.

The Ed25519 private release key is local-only under
`local/release/signing-key.pem`; **back it up securely**. Only its public key is
compiled into the application. The signature authenticates the archive hash,
file hashes, sizes, platform, repository and version. The updater rejects path
traversal, Windows device names, case collisions, symlinks, unlisted files and
downgrades. Installation is transactional, with previous managed files under
`.updates/backups`. A failed file replacement restores the previous app.

New signing keys require an intentional trust migration in an already-trusted
release; replacing a GitHub manifest alone cannot rotate the key.
