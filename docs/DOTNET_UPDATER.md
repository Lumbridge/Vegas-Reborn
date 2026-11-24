# .NET updater and release layout

The .NET companion (`dotnet/`, version 0.5.0 onwards) keeps the 0.4.x update
contract unchanged: the same signed `update.json` (schema 1), the same Ed25519 key,
the same GitHub latest-release feed, the same `plan.json` and the same helper
command line. The implementation is `dotnet/src/VegasReborn.Core/Update/`.

## Why the layout is constrained

GitHub only offers the **latest** stable release. An installed 0.4.14 companion
downloads it, verifies it with its own Python `verify_manifest` and `managed_path`,
stages it, and then starts the **new** staged `VegasReborn.exe`:

```
<root>\.updates\staging\<32 hex>\VegasReborn\VegasReborn.exe
    --apply-update <root>\.updates\staging\<32 hex>\plan.json --parent-pid <pid>
```

(working directory: the payload folder; `PYINSTALLER_RESET_ENVIRONMENT=1` is set).
`plan.json` is `{"schema": 1, "root": ..., "payload": ..., "manifest": <base64>,
"signature": <base64>}`. The parent exits immediately afterwards.

Therefore, for as long as 0.4.x installations may exist, **every** release must:

- pass the 0.4.14 manifest checks: only allowlisted paths, at most 5000 files,
  at most 700 MiB unpacked, archive at most 200 MiB, entries under `VegasReborn/`,
  no case collisions, `VegasReborn.exe` and `version.json` present, `mode` is
  `seed` exactly for the two seed files;
- ship a `VegasReborn.exe` that accepts that exact command line and plan and
  completes the install (the .NET entry point calls `UpdateCommandLine.TryRun`
  before any UI);
- be downloadable within 0.4.14's fixed 180 s per-download deadline. Keep the
  archive small (target under about 60 MB); slow connections otherwise fail and
  the user must retry.

The .NET `ManagedPaths` is the same allowlist (plus rejection of a few extra
Win32 device and invalid names), so a release built for 0.4.14 is exactly a
release the .NET updater accepts.

## Decided release layout (0.5.0)

| Path | Contents |
| --- | --- |
| `VegasReborn.exe` | Framework-dependent single-file bundle (app, Core, BouncyCastle, resources) with `AppHostDotNetSearch=AppRelative` and `AppHostRelativeDotNet=runtime-<version>\dotnet` |
| `README.txt`, `LICENSE-Minhook.txt`, `THIRD-PARTY-NOTICES.txt`, `version.json` | Exactly these root names. .NET, WPF and BouncyCastle notices go into `THIRD-PARTY-NOTICES.txt`; a `LICENSE-DotNet.txt` would be rejected by 0.4.14. `LICENSE-Python.txt` is no longer shipped (an old copy stays on disk). |
| `runtime-<version>/dotnet/` | Private x64 copy of `Microsoft.NETCore.App` and `Microsoft.WindowsDesktop.App` (`host/fxr/<v>/hostfxr.dll`, `shared/...`), the exact version the app was built against |
| `runtime-<version>/native/` | `VegasCompanionVegas2.dll` and any new native module whose name the 0.4.x allowlist does not accept (also `VegasDedicated-N.dll`, as today) |
| `local/companion/bin/` | Only the 0.4.x names: `VegasCompanion-N.dll`, `VegasDedicated.dll`, `VegasInjector.exe`, `VegasSteamGate.dll`, `VegasSteamControl.exe`, `build.json` |
| `local/companion/service.json`, `root-public.der` | `seed` (written only when absent) |
| `runtime-<version>/updater-only` | Marker for updater-only packages, as before |

The Vegas 2 adapter is loaded from `runtime-<version>/native/`
(`AppPaths.NativeAdapter`), falling back to `AppPaths.NativeBin` in development
layouts. 0.5.0 to 0.5.2 do not ship the dedicated modules. Later releases ship
the dedicated server host as `runtime-<version>/VegasServer.exe` (a
framework-dependent single file using `runtime-<version>/dotnet`) and the
Vegas 1 server module as `local/companion/bin/VegasDedicated-4.dll`, a name
0.4.14 accepts.

Because each runtime folder is versioned, the staged helper runs entirely from
its payload (`payload/runtime-<version>/dotnet`) without a system .NET install,
and an update never overwrites the runtime of the running app.

Fallback if app-relative runtime probing fails acceptance: a self-contained
single-file `VegasReborn.exe` with `IncludeNativeLibrariesForSelfExtract=true`
(native files extract to `%TEMP%\.net`). It satisfies the same allowlist with
only the executable at the root. Do not ship a plain self-contained or
framework-dependent folder: `hostfxr.dll`, `VegasReborn.dll` and its
`.deps.json`/`.runtimeconfig.json` would have to sit at the root, which 0.4.14 rejects.

## What the .NET build and package step must produce

`tools/build_release.py` performs these steps (`--trial` builds an unsigned
layout from the working tree for local checks). It must:

1. Build from the exact current `origin/main` commit and use the shared version
   reservation (`tools/release_coordination.py`), as today.
2. `dotnet publish src/VegasReborn.App -c Release -r win-x64 --self-contained false
   -p:PublishSingleFile=true -p:AppHostDotNetSearch=AppRelative
   -p:AppHostRelativeDotNet=runtime-<version>\dotnet`, with
   `dotnet/Directory.Build.props` `<Version>` equal to the release version (the
   updater reads its current version from the assembly).
3. Copy the matching x64 .NET and Windows Desktop runtimes into
   `runtime-<version>/dotnet/`, the native modules into `runtime-<version>/native/`
   and `local/companion/bin/`, the seed files, README, notices and
   `version.json` (`{"version": ..., "repository": "Lumbridge/Vegas-Reborn"}`).
4. Produce the ZIP (`VegasReborn/<path>` entries, deflate), `update.json`
   (`json.dumps(sort_keys=True, separators=(",", ":")) + "\n"`), `update.sig` and
   `build-receipt.json` exactly as `build_release.py` does, signed with
   `local/release/signing-key.pem`.
5. Verify the signed manifest with **both** the 0.4.14 Python `verify_manifest`
   and the .NET `ReleaseManifest.Verify` before publishing through
   `tools/publish_release.py`.

## Migration from 0.4.14

1. 0.4.14 finds 0.5.0, verifies it and extracts it to `.updates/staging/<id>/VegasReborn`.
2. It starts the staged .NET `VegasReborn.exe --apply-update ... --parent-pid ...`
   and exits.
3. The .NET helper (`UpdateCommandLine.TryRun` → `StagedUpdate.Apply`) verifies
   the plan with the pinned key, checks that the parent's image is
   `<root>\VegasReborn.exe` and waits up to 90 s for it to exit, takes the
   `.updates/install.lock` byte-range lock (compatible with the Python helper),
   refuses a downgrade or a running Vegas/Vegas 2 game or server, then installs
   transactionally: backups in `.updates/backups/<id>`, executable last, seed files
   only when absent.
4. It restarts `<root>\VegasReborn.exe` (now .NET) and writes `.updates/last-update.json`.

`local/` (accounts, sessions, preferences, saves, game copies) is never touched.
`runtime-0.4.14/` remains on disk; restoring `VegasReborn.exe` and `version.json`
from `.updates/backups/<id>` returns to 0.4.14 manually.

Acceptance before publishing: in an isolated copy of a published 0.4.14
installation, stage the locally signed candidate with 0.4.14's own
`prepare_update` (using a local downloader) and `launch_helper`, then confirm the
.NET app restarts, data under `local/` is unchanged and a second update from
0.5.0 works. Never install a candidate over the normal app.

## Differences from the Python updater

- Paths are resolved with `GetFinalPathNameByHandle` (the Windows behaviour of
  Python's `Path.resolve`); any reparse point inside a staged payload is rejected.
- Process enumeration uses the process list API instead of `tasklist`.
  `R6Vegas2_Game.exe` and `RainbowSixVegas2_SADS.exe` also block installation.
- TLS: .NET `HttpClient` on Windows uses SChannel with the Windows certificate
  store and chain engine, which is what `truststore` provided. No certificate
  callback is installed and no process-wide setting changes.
- The 0.4.14-specific "existing launch helpers" install check is not ported; a
  .NET updater can never install 0.4.14 (that would be a downgrade).
