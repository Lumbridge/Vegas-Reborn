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

## Release process

The version is defined in `src/vegas_reborn/version.py`. Keep `pyproject.toml` in
sync. Commit and test the reviewed source before building; dirty sources are
rejected. Run `tools/companion/Package-Companion.ps1`, then publish the ZIP,
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
