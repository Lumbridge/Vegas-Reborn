# Vegas Reborn Companion

The Windows companion for the **original PC Rainbow Six Vegas**: Steam-authorized
launching, shared account/character synchronization, player names and rank,
borderless display options, local dedicated hosting, native Discord activity,
and signed updates through GitHub Releases.

This is the **companion repository**. The account, native-protocol matchmaking,
database and deployment backend are maintained separately. Hosting here means
running your owned original dedicated game server; it is not the backend service.
No original game files or account data are distributed.

## Download and play

Download the Windows ZIP from [Releases](https://github.com/Lumbridge/Vegas-Reborn/releases/latest),
extract the whole `VegasReborn` folder to a writable location, and run
`VegasReborn.exe`. Python is included. You must own and install the original PC
game and have Steam running. Sign in, select your game folder, and choose Play.
The companion creates its own game copy and keeps character files separate from
the installed game's profiles. Keep it open until the game exits to synchronize.

The companion checks for updates at startup. **Update and restart** downloads a
signed release, verifies it, waits for the companion to exit, installs, and
restarts. Finish playing and stop hosting first. Checks can be disabled in the
app. The updater preserves `local` account sessions, preferences, saves and game
copies. Only specifically listed native helper files within `local/companion/bin`
can be replaced. Existing service configuration and public trust key are seeded
only on a fresh installation.

Update HTTPS uses Windows' native certificate validation, without disabling TLS
checks or changing the machine's trust store. Version 0.3.1 corrects a missing
certificate-chain issue found during the first public-feed test on this PC.

Retail Discord join invites, rendered Alt-Tab acceptance and gameplay acceptance
remain separate work. App and updater tests do not establish retail gameplay.

## Development

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
