# Rainbow Six Vegas 2 support roadmap

Goal: the companion and backend support the original PC **Rainbow Six Vegas 2**
(Steam app 15120) alongside Rainbow Six Vegas (13540), with shared Discord
accounts.

Decisions (2026-11-23):

1. Vegas 2 spike first, on the existing code, before any rewrite.
2. Companion moves from Python/Tk to C#/.NET. The native C++ hook DLLs stay C++.
3. The backend (native master, credential broker and account service) moves to Rust.
4. Both games' masters share the existing host and use separate ports per title.

## What is known (static analysis only)

Observed in the Steam files. None of it is yet confirmed with the running client.

- Same Ubisoft Agora + Demonware SDK family as Vegas 1, slightly newer
  (adds `bdKeyArchive`, clan tasks, `MmpAuthenticationServer`).
- Bootstrap host `gconnect.ubi.com:3074`. The request adds `&format=xml`
  (Vegas 1 has no format parameter). Candidate gid `afb82e240b4e9b8a70ecdfe1188c9c89`.
- New `KellerGame/Config/PC/signature.pub` (RSA-2048 DER). It is passed with gid
  and `SerialName` into service initialisation. Hypothesis: it verifies a signed
  XML bootstrap response.
- There is no PC `AgoraConfig.lua`. STUN hosts are `stun.{apac,emea,ncsa}.ubi.com`.
- The client exe is wrapped by Steam DRM (`.bind`). The dedicated server
  `RainbowSixVegas2_SADS.exe` is the same build, unprotected, and is the static
  analysis reference.
- `DMSDK.dll` is the Digimask face SDK, not Demonware.
- Saves are under `Documents/My Games/Ubisoft/R6Vegas2/`.

## Phase 1 — Vegas 2 spike (existing Python backend, existing companion)

Exit criterion: the original Vegas 2 client signs in to a local backend.

1. Ground the XML bootstrap response format and `signature.pub` verification in
   the dedicated server binary.
2. Backend: make the master per-title (gid, title id, response format and
   signing key), and add a Vegas 2 bootstrap.
3. Companion: add a Vegas 2 launch path that uses only the generic hooks (file
   isolation, DNS and `connect()` redirect, a `signature.pub` replacement in the
   companion's copy). For the spike, sign in manually with a test credential, so
   no title RVAs are needed yet.
4. Acceptance run with the original client.
   Record which services and operations it calls after sign-in.

## Phase 2 — Rust backend

- A single Rust workspace with crates for the wire layer (framing, bitbuffer,
  3DES, Tiger, certificates, tickets), Demonware services, title definitions
  (Vegas 1, Vegas 2), credential broker, account and Discord service (axum),
  and storage.
- Port the existing Python and .NET tests as the parity baseline. Vegas 1 must
  stay working throughout: run the Rust and Python masters side by side on
  different ports until retail acceptance passes.
- Accounts gain a per-game dimension (slots, leases, statistics, name claims
  shared).
- Website: per-game pages and leaderboards.

## Phase 3 — C#/.NET companion

- A title adapter per game: exe names and hashes, Steam app id, copy
  preparation, trust-key swap, save slots, rank tables, native module, and the
  hooked host list.
- Native: split `compat.cpp` into a shared core (isolation, redirect,
  credential IPC, readiness, status) and per-title modules with their own
  image identity, signature tables and hooks.
- The first .NET release must install through the 0.4.x Python updater: keep
  the signing key and fit the allowlisted layout.

Status (`dotnet/`, not yet released):

- Done: title catalog with per-title fixes, preferences shared with 0.4.x,
  Discord sign-in and name claim, character sync with conflict choice, the
  launch pipeline for both titles, Discord activity, the signed updater
  (including the `--apply-update` handoff from 0.4.14) and the WPF window.
  Toggle fixes are written to `native-features.txt`; `compat.cpp` skips the
  disabled installs and lists them in `disabledFeatures`.
- Done: `tools/build_release.py` builds the .NET layout; the Vegas 2 adapter is
  loaded from `runtime-<version>/native/` when present (`AppPaths.NativeAdapter`).
  Dedicated hosting (`--host`/`--play`) is not part of 0.5.0.
- Vegas 2 through the .NET app routes to `vegas.lumbridge.co.uk:3080`, which
  is not deployed. Use `tools/vegas2_spike.py` against a local master until
  then.

## Phase 4 — Vegas 2 parity

Login hook, PEC/character sync, ranks, matchmaking schema from Vegas 2's
`MatchmakingData`, statistics and leaderboards, dedicated hosting. Each one is
grounded in the binary or in recorded traffic, as for Vegas 1.

Character sync: the character, XP and A.C.E.S. points are `PROFILE3.DAT` (`Profiles/Vegas2/PecSave.cs`;
raw XP at offset 17, matched to the in-game CURRENT XP at 30 and 40) and the loadout is
`R6_EquipEquipmentTemplate`. They sync to the separate Vegas 2 cloud profile
(`/api/v1/account/profile/vegas2`), which has its own revision and XP. XP and each A.C.E.S.
total never go down when two PCs merge. Settings
(`PROFILE1.DAT`), the name (`PROFILE2.DAT`) and `PlayerProfile.ini` stay on the PC.
