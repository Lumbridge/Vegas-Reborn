# Vegas 1 Maps Pack download

## Intent

Players want more maps to play online in Rainbow Six Vegas. Ubisoft released a free
Maps Pack in July 2007 (v1.05, ten maps). The supported Steam build does not include
it. The launcher should let a player install it with one button, and every player who
has it should be able to host and join matches on those maps.

The same install also enables extra game modes on the story missions (Terrorist Hunt,
Deathmatch, Team Deathmatch, Attack & Defend), based on the community "Map Extension"
ini changes by Oni.

## Goals

- One **Download Maps Pack** button in a Maps section for Vegas 1, with progress, an
  installed state and **Remove**.
- Installs the ten maps: Kitchen, Roof, Ruined, Trains, Neon, Presidio, Mexico,
  Streets, Killhouse Double, Tower.
- Applies the story-mission mode additions in the same install, so everyone with the
  pack has identical map and mode lists.
- Works online: hosts and joiners with the pack see the same maps and modes.
- The dedicated operator tool can install the pack into the server's game copy.

## Non-goals

- Per-map toggles. Dropped: mismatched content between players breaks joins.
- Automatic download when joining a match that needs the pack.
- Oni's other mods (FOV, sound, weapon edits, quick startup) and his 16-player exe.
- Vegas 2. Its content is different and is not covered here.

## Anti-goals

- Map content or Oni's files committed to this repository (AGENTS.md: never commit
  game binaries/data).
- Any change to the installed game. Everything goes into launcher-owned game copies.
- Installing unverified bytes. Every file must match a signed list before it is used.
- Leaving a half-installed pack that the game would load.

## Facts established

- The Maps Pack installer is a WinRAR SFX around an InstallShield 12 setup
  (`data1.cab`, `data2.cab`, `data1.hdr`). The signed InstallShield binaries check out;
  nothing malicious was found. Product version 1.05.000.
- The pack installs only data:
  `KellerGame\Content\CookedPc\MapsPack\PAK_00_Common` … `PAK_10_Tower` (`.usdx`,
  `.rmpc`, `.shaders`, `.ini`, `.uppc` under `Medias`),
  `KellerGame\Localization\{INT,FRA,DEU,ESP,ITA,POL,RUS}\PAK_*`, and
  `KellerGame\ImageMap\PAK_*.dds`. Uncompressed size is about 775 MB.
- The supported Steam install (client SHA-256 `667f659b…`) has no `MapsPack` folder.
  Its `DefaultEngine.ini` has `+Paths=..\KellerGame\Content`, which is searched
  recursively, so files under `Content\CookedPc\MapsPack` should be found. **Not yet
  confirmed with a retail launch.**
- Oni's "Map Extension" changes 30 of the 59 ini files under
  `KellerGame\Content\CookedPc\Maps`:
  - Story missions (`01_Mexico_04/05/06`, `02_Casino_03/05/06`, `03_Fremont_02/03B`,
    `04_Tower_02`, `05_Dantes_04/06`, `06_Dam_02/04/08`, and the `99_HeliRides_*In*`
    files): adds `[GameModeN]` sections for `GAMEMODE_TERROHUNT`,
    `GAMEMODE_DEATHMATCH`, `GAMEMODE_TEAMDEATHMATCH`, `GAMEMODE_ATTACKANDDEFEND`
    (with `m_szMissionType=R6ObjectiveExtractionItem`), `m_iMissionOrderId=30`,
    `m_iMaxPlayers=16`, `m_iMaxPlayersListenServer=14`. It also raises the existing
    co-op section's `m_iMaxPlayers` from 4 to 16.
  - Multiplayer maps (`MP_*`): only raises one `m_iMaxPlayers` from 4 to 16.
  - Oni's files contain duplicated keys; the launcher writes clean sections.
- Oni's folder contains no maps beyond the official pack. Its "Map Comparisons" files
  are byte-identical to the stock Vegas 2 Estate and Killhouse files.
- The launcher already has: a private game copy (`AppPaths.GameCopy`, prepared by
  `GamePreparer.PrepareAsync`), in-place ini repair against the original
  (`GamePreparer.RepairEngineIni`), a signed-manifest verifier
  (`ReleaseManifest`, pinned `UpdateKey`), and a size-limited, redirect-restricted
  downloader (`ReleaseDownloader`). The dedicated host keeps its own copy
  (`DedicatedHost.PrepareCopyAsync`).

## Approaches considered

1. **One pack for everyone, one button (chosen).** Everyone who installs gets the
   same files and mode lists, so online play is consistent. Costs one 686 MB download
   per player.
2. **Per-map toggles plus fetch on join.** Smaller downloads, but the native adapter
   would need to detect what a match needs and the launcher would have to restart the
   game. Much more work, more failure modes.
3. **Per-map toggles, no sync.** Simplest, but joins fail whenever two players'
   selections differ.

The user picked 1 and asked for the mode changes to be bundled with it rather than
offered as a separate toggle.

## Design

### Content and hosting (backend project, not this repo)

- The backend project publishes the pack as one or more archives of the installed
  files (paths relative to the game root), plus a signed content manifest.
- The content manifest lists every file: relative path, size, SHA-256, and which
  archive holds it. It carries `kind: "content"`, a content id (`vegas1-maps-pack`),
  a content version, and the title it applies to (`vegas1`), so it can never be
  confused with an update manifest.
- **Builder's call (proposed):** sign with the existing release key and verify
  against the pinned `UpdateKey`, with the `kind` field as domain separation. A
  separate content key would also work; it would need its own pinned public key.
- **Host (user's decision): a GitHub release.** It fits the existing redirect
  allowlist in `ReleaseRedirects` (assets up to 2 GB). Constraints:
  - the repository must be public, since the launcher downloads without credentials;
  - not `Lumbridge/Vegas-Reborn`: `ReleaseFeed` reads that repository's
    `releases/latest`, and a content release there could become "latest" and break
    app updates;
  - the backend project's repository if it is public, otherwise a separate public
    content repository. The launcher pins the repository and tag as constants
    (alongside `ReleaseInfo`), e.g. tag `vegas1-maps-pack-v1` with assets
    `content.json`, `content.sig` and the archives.
  - The publishing script for the content release lives in the backend project.

### Launcher (this repo)

- **Core:** a `Content` area (`VegasReborn.Core/Content`) with:
  - manifest verification (reusing `Json` helpers and `Sha256Hex`), with limits sized
    for the pack instead of `ReleaseManifest.MaxDownload`/`MaxUnpacked`;
  - download into a staging folder under `AppPaths.Local`, resumable per archive,
    with cancellation and byte progress;
  - verification of every extracted file against the manifest before anything is
    moved into the game copy;
  - install by moving verified files into the game copy, then writing an install
    record (`content/vegas1-maps-pack.json`: version, file list with hashes) last.
    The game copy is only treated as having the pack once the record exists;
  - remove by deleting exactly the files in the record, then restoring the map ini
    edits, then deleting the record.
- **Mode edits:** extend the existing always-on `sixteen-players` fix
  (`Launch/MissionPlayerLimits.cs`) instead of adding a second step that rewrites the
  same files. It already rebuilds every `CookedPc/Maps` ini from the installed
  original on each prepare and raises Terrorist Hunt to 16 and listen-server caps
  from 14 to 16. With the pack installed, the same pass also appends the sections
  below; without it, output is exactly what it is today. Remove therefore needs no
  separate restore: the next prepare (run straight after remove) rebuilds without
  them.
- **Added sections.** For each file below, append four sections numbered after the
  file's last existing `[GameModeN]`, all with the file's order id:

  ```
  [GameModeN]   m_eGameMode=GAMEMODE_TERROHUNT        m_iMaxPlayers=16  m_iMaxPlayersListenServer=16
  [GameModeN+1] m_eGameMode=GAMEMODE_DEATHMATCH       m_iMaxPlayers=16  m_iMaxPlayersListenServer=16
  [GameModeN+2] m_eGameMode=GAMEMODE_TEAMDEATHMATCH   m_iMaxPlayers=16  m_iMaxPlayersListenServer=16
  [GameModeN+3] m_eGameMode=GAMEMODE_ATTACKANDDEFEND  m_szMissionType=R6ObjectiveExtractionItem
                                                      m_iMaxPlayers=16  m_iMaxPlayersListenServer=16
  each also: m_iMissionOrderId=<order id>
  ```

  | File (under `CookedPc/Maps`) | Order id |
  |---|---|
  | `01_Mexico/01_Mexico_04.ini` | 30 |
  | `01_Mexico/01_Mexico_05.ini` | 31 |
  | `01_Mexico/01_Mexico_06.ini` | 32 |
  | `02_Casino/02_Casino_06.ini` | 35 |
  | `03_Fremont/03_Fremont_02.ini` | 36 |
  | `03_Fremont/03_Fremont_03B.ini` | 37 |
  | `04_Tower/04_Tower_02.ini` | 38 |
  | `05_Dantes/05_Dantes_04.ini` | 30 |
  | `05_Dantes/05_Dantes_06.ini` | 39 |
  | `06_HooverDam/06_Dam_02.ini` | 40 |
  | `06_HooverDam/06_Dam_04.ini` | 30 |
  | `06_HooverDam/06_Dam_08.ini` | 41 |
  | `99_HeliRides/99_HeliRides_01In.ini` | 30 |
  | `99_HeliRides/99_HeliRides_02InNear.ini` | 42 |
  | `99_HeliRides/99_HeliRides_03InNear.ini` | 43 |
  | `99_HeliRides/99_HeliRides_04InNear.ini` | 44 |
  | `99_HeliRides/99_HeliRides_05InNear.ini` | 45 |
  | `99_HeliRides/99_HeliRides_06In.ini` | 46 |

  This is the full set Oni adds, with only these five keys. Decided deviations from
  Oni (designer's calls):
  - listen-server cap 16, not 14, to match the existing fix;
  - story co-op (`GAMEMODE_STORY`) stays at 4: the existing fix leaves it alone and
    Oni notes players past 4 in co-op behave like AI and can fall out of helicopters;
  - Oni's change of `99_HeliRides_02InNear`'s `GAMEMODE_MISSION` order id to 42 is
    not copied (no added mode depends on it);
  - `02_Casino_03` and `02_Casino_05` get nothing (Oni only raised their story cap).
- **Pack map caps:** the pack's own mission inis under `MapsPack/PAK_*` get the same
  raise as stock maps. Their original is the verified downloaded file, so the raise
  is applied at install time and the install record stores the raised hash.
- **Launch gate:** if an install record exists but a listed file is missing or its
  hash changed, the launcher shows "The Maps Pack is damaged. Download it again or
  remove it." and does not launch with partial content.
- **UI:** a Maps section on the Vegas 1 game page:

  ```
  Maps Pack
  Kitchen, Roof, Ruined, Trains, Neon, Presidio, Mexico, Streets,
  Killhouse Double, Tower. Adds Terrorist Hunt, Deathmatch, Team
  Deathmatch and Attack & Defend to story missions.
  Players need the Maps Pack to join matches that use these maps.

  [ Download Maps Pack (686 MB) ]   → progress, Cancel
                                    → Installed  [ Remove ]
  ```

  Plain copy only. Switching to and from this section keeps the window size and
  position; the section scrolls inside the page.
- Download and remove are disabled while the game is running (`GameProcesses`).
- **Content versions (designer's call):** the install record keeps the content
  version. When the published manifest has a newer one, the section shows
  "A newer Maps Pack is available" with **Update**; launching is not blocked. The
  2007 pack is not expected to change, so this exists only to fix a bad publish.
- **Fallback if retail check 1 fails (designer's call):** add
  `+Paths=..\KellerGame\Content\CookedPc\MapsPack` to the copy's engine ini next to
  the existing `RepairEngineIni` edits. Retail check 1 runs before any UI work.

### Dedicated operator tool

- The operator tool gets an install option that installs the same pack into the
  server's game copy, using the same Core code and install record.
- The manifest binds to the title (`vegas1`), not to one executable hash, so it
  applies to both the client copy and the server copy. Each copy keeps its own
  executable check as today.
- The server copy currently never runs `MissionPlayerLimits`. With the pack it must
  run the same pass, so servers offer the same modes as clients.
- Windows `VegasReborn.Server` only. The Linux/Wine image is out of scope.

## Verification

- Unit tests: manifest verification (bad signature, wrong kind, wrong client hash,
  path traversal, oversize), staged install and rollback, remove leaves the copy
  identical to before, ini edits are idempotent and reversible.
- `python -m unittest discover -s tests -v` if any Python transport/updater code is
  touched.
- **Retail checks** (need the startup-safe private-audio route per AGENTS.md):
  1. The supported Steam build lists and loads a pack map.
  2. A story mission appears and runs in each added mode.
  3. Host with the pack, joiner without it: record what happens (clean refusal,
     crash, or works). If it crashes, add a note to the join flow copy.

## Left to the builder

- Archive format and split for hosting (one archive vs per-folder).
- Exact class and file names within the outline above.
- Where the Maps section sits in the existing game page layout.
- Download retry policy, within the existing downloader limits style.
