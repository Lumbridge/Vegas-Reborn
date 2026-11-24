# Vegas 1 Maps Pack Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Download Maps Pack button for Vegas 1 that installs the official ten-map pack from a signed GitHub release into the launcher's (and the dedicated server's) game copy, and adds versus modes to 18 story-mission files.

**Architecture:** The verified pack lives once in a launcher-owned content store (`local/content/vegas1-maps-pack/`). Each game-copy prepare links (hard link, copy as fallback) the store's files into the copy, or removes them when the pack is gone, then the existing `MissionPlayerLimits` pass rebuilds the map inis with the extra modes. The store, not the copy, holds the install record, so the button works before a copy exists, and client and server copies share one download.

**Tech Stack:** .NET (C#), WPF app, xUnit tests, BouncyCastle Ed25519 (already used by `ReleaseManifest`), `System.IO.Compression` zip.

**Spec:** `docs/superpowers/specs/2026-11-24-vegas1-maps-pack-design.md`

## Global Constraints

- Never commit game binaries/data. Test fixtures are synthetic text; no pack files, no Oni files.
- The installed original game is read-only; all writes go to launcher-owned copies or `AppPaths.Local`.
- Plain, functional UI copy. No quips, slogans or marketing language.
- Switching pages must preserve window size and position; content scrolls within the page.
- Content release host: a public GitHub repository that is not `Lumbridge/Vegas-Reborn`.
- Manifest signed with the existing release key, verified against `UpdateKey.PublicKeyHex`, with `"kind": "content"`.
- Content id `vegas1-maps-pack`, title `vegas1`.
- Added mode sections: keys `m_eGameMode`, `m_szMissionType` (A&D only, `R6ObjectiveExtractionItem`), `m_iMissionOrderId`, `m_iMaxPlayers=16`, `m_iMaxPlayersListenServer=16`. Story co-op stays at 4.
- Run `dotnet test dotnet/VegasReborn.slnx` after every task. Run `python -m unittest discover -s tests -v` only if Python transport/updater code is touched (it is not, in this plan).
- Retail launches require the established startup-safe private-audio route for that test.
- Branch `feature/maps-pack`; finish by merging into `main` (no PR).

## Review Focus

1. **Pack removed while a game copy still has its files** → next prepare deletes exactly the linked files and restores stock map inis; nothing else in the copy changes. (Task 6 test `RemovedPackIsUnlinkedOnNextPrepare`.)
2. **User edits or antivirus deletes a pack file in the store** → launch is refused with "The Maps Pack is damaged. Download it again or remove it."; no partial content is linked. (Task 6 test `DamagedStoreRefusesToLink`.)
3. **Download interrupted (cancel, network drop, app closed)** → store untouched, staging cleaned on next attempt, no record written, status stays Not installed. (Task 5 test `CancelledInstallLeavesNoRecord`.)
4. **Slow connection** → a single archive may take far longer than 3 minutes; content downloads must not hit the update downloader's 180 s deadline. (Task 3 test `ContentDeadlineIsLongerThanUpdateDeadline`.)
5. **Malicious or broken release (path traversal in zip, file not in manifest, wrong hash, wrong kind/title)** → install fails before anything reaches the store. (Task 2 and Task 5 tests.)

---

### Task 1: Retail spike — does the supported build load the pack?

Manual, no product code. Decides whether Task 6 needs the `+Paths` fallback.

**Files:** none committed. Record the outcome in the spec's "Facts established" section.

- [ ] **Step 1:** Extract the user's Maps Pack (`C:\Users\ryans\Desktop\Rainbow-Six-Vegas-One-Extra-Maps.exe`) into a scratch folder outside the repo with `7z x`, then extract `data1.cab`/`data2.cab` with an InstallShield cab tool (e.g. `unshield` or `i6comp`). Do not run `setup.exe`.
- [ ] **Step 2:** Copy only `KellerGame\Content\CookedPc\MapsPack\`, `KellerGame\Localization\*\PAK_*` and `KellerGame\ImageMap\PAK_*.dds` into the launcher's Vegas 1 game copy (`<root>\local\companion\game`), never the installed game.
- [ ] **Step 3:** Launch through the launcher with the private-audio route. Expected: pack maps (e.g. Kitchen, Tower) appear in Create Match and one loads.
- [ ] **Step 4:** If they do not appear, add `+Paths=..\KellerGame\Content\CookedPc\MapsPack` under the existing `+Paths=` lines in the copy's `KellerGame/Config/PC/DefaultEngine.ini` and retry. Record which variant works.
- [ ] **Step 5:** Remove the copied files from the game copy (or delete the copy so it is rebuilt). Commit the spec update:

```bash
git add docs/superpowers/specs/2026-11-24-vegas1-maps-pack-design.md
git commit -m "Record whether the Steam build loads the Maps Pack"
```

---

### Task 2: Mode extension and the rebuild pass

**Files:**
- Create: `dotnet/src/VegasReborn.Core/Launch/MapModeExtension.cs`
- Modify: `dotnet/src/VegasReborn.Core/Launch/MissionPlayerLimits.cs`
- Test: `dotnet/tests/VegasReborn.Core.Tests/Launch/MapModeExtensionTests.cs`, `dotnet/tests/VegasReborn.Core.Tests/Launch/Vegas1MatchFixesTests.cs`

**Interfaces:**
- Produces:
  - `internal static class MapModeExtension` with `public static IReadOnlyDictionary<string, int> OrderIds` (key: forward-slash path relative to `MissionPlayerLimits.MapsFolder`, e.g. `"01_Mexico/01_Mexico_04.ini"`; value: order id) and `internal static string Extend(string text, int orderId)`.
  - `MissionPlayerLimits.Apply(string copy, string source, bool extendModes)` replaces `Apply(string copy, string source)`.

- [ ] **Step 1: Write failing tests** in `MapModeExtensionTests.cs`:

```csharp
[Fact] public void OrderIdsMatchTheSpecTable()
{
    Assert.Equal(18, MapModeExtension.OrderIds.Count);
    Assert.Equal(30, MapModeExtension.OrderIds["01_Mexico/01_Mexico_04.ini"]);
    Assert.Equal(35, MapModeExtension.OrderIds["02_Casino/02_Casino_06.ini"]);
    Assert.Equal(42, MapModeExtension.OrderIds["99_HeliRides/99_HeliRides_02InNear.ini"]);
    Assert.Equal(46, MapModeExtension.OrderIds["99_HeliRides/99_HeliRides_06In.ini"]);
    Assert.False(MapModeExtension.OrderIds.ContainsKey("02_Casino/02_Casino_03.ini"));
}

// Story file shaped like 01_Mexico_04.ini (CRLF), with trailing [PS3]/[Xenon] sections.
[Fact] public void ExtendNumbersAfterTheLastGameModeAndKeepsOtherSections()
{
    var text = "[R6Game.R6MissionConfig]\r\nm_iId=65\r\n\r\n[GameMode0]\r\nm_eGameMode=GAMEMODE_STORY\r\nm_iMaxPlayers=4\r\nm_iMissionOrderId=2\r\n\r\n"
             + "[GameMode1]\r\nm_eGameMode=GAMEMODE_MISSION\r\nm_iMissionOrderId=20\r\n\r\n[PS3]\r\nm_bCanBeLaunch = TRUE\r\n";
    var extended = MapModeExtension.Extend(text, 30);
    Assert.Contains("[GameMode2]\r\nm_eGameMode=GAMEMODE_TERROHUNT\r\nm_iMissionOrderId=30\r\nm_iMaxPlayers=16\r\nm_iMaxPlayersListenServer=16\r\n", extended);
    Assert.Contains("[GameMode5]\r\nm_eGameMode=GAMEMODE_ATTACKANDDEFEND\r\nm_szMissionType=R6ObjectiveExtractionItem\r\nm_iMissionOrderId=30\r\nm_iMaxPlayers=16\r\nm_iMaxPlayersListenServer=16\r\n", extended);
    Assert.Contains("m_eGameMode=GAMEMODE_STORY\r\nm_iMaxPlayers=4\r\n", extended);
    Assert.Contains("[PS3]\r\nm_bCanBeLaunch = TRUE", extended);
    Assert.True(extended.IndexOf("[GameMode5]") < extended.IndexOf("[PS3]"));
}

[Fact] public void ExtendIsDeterministic() // Extend(text, 30) == Extend(text, 30); Apply always extends the original, never its own output
```

In `Vegas1MatchFixesTests.cs` add:

```csharp
[Fact] public void ApplyExtendsListedStoryFilesOnlyWhenAsked()
// install + game copy contain 01_Mexico/01_Mexico_04.ini (story shape) and MP_Casino_01 (Shipped).
// Apply(copy, install, extendModes: true)  -> Mexico file == MapModeExtension.Extend(original, 30); MP file == Raise(Shipped).
// Apply(copy, install, extendModes: false) -> Mexico file == original exactly (sections stripped); MP file == Raise(Shipped).

[Fact] public void ApplyRestoresAnUnraisableFileToTheOriginal()
// copy has a modified 01_Mexico_01.ini, original has no limits to raise -> after Apply, copy == original.
```

Update the existing `ApplyRebuildsCopiedFilesFromTheInstalledOriginal` call sites to pass `extendModes: false`.

- [ ] **Step 2:** Run `dotnet test --filter "FullyQualifiedName~MapModeExtension|FullyQualifiedName~Vegas1MatchFixes"`. Expected: build failure (types/overload missing).
- [ ] **Step 3: Implement.** `OrderIds` holds exactly the 18 rows of the spec table. `Extend` finds the highest `N` among `[GameModeN]` headers, inserts the four sections (order TERROHUNT, DEATHMATCH, TEAMDEATHMATCH, ATTACKANDDEFEND; keys in the order shown in the test) after the last line of the last `[GameModeN]` section, separated by a blank line, using the file's newline style. In `MissionPlayerLimits.Apply`: for every original ini, `expected = Raise(text) ?? text`, then if `extendModes` and the path relative to `MapsFolder` is in `OrderIds`, `expected = Raise(Extend(text, id)) ?? Extend(text, id)`; write when the copy's text differs. Update the doc comment (files are now always rebuilt from the original).
- [ ] **Step 4:** Update the call in `GamePreparer.PrepareAsync` to `MissionPlayerLimits.Apply(destination, source, extendModes: false)` for now (Task 6 wires the real value). Run the full test suite. Expected: PASS.
- [ ] **Step 5:** Commit: `git commit -m "Add story-mission versus modes to the mission limits pass"`.

---

### Task 3: Content manifest and long downloads

**Files:**
- Create: `dotnet/src/VegasReborn.Core/Content/ContentManifest.cs`, `dotnet/src/VegasReborn.Core/Content/ContentInfo.cs`, `dotnet/src/VegasReborn.Core/Content/ContentException.cs`
- Modify: `dotnet/src/VegasReborn.Core/Update/ReleaseManifest.cs` (make `VerifySignature` `internal`), `dotnet/src/VegasReborn.Core/Update/ReleaseDownloader.cs`
- Test: `dotnet/tests/VegasReborn.Core.Tests/Content/ContentManifestTests.cs`, `dotnet/tests/VegasReborn.Core.Tests/Update/DownloadTests.cs`

**Interfaces:**
- Produces:
  - `public static class ContentInfo { public const string Repository = "<public content repo>"; public const string Id = "vegas1-maps-pack"; public const string Tag = "vegas1-maps-pack-v1"; public static string BaseUrl => $"https://github.com/{Repository}/releases/download/{Tag}/"; }`. The repository value is confirmed with the user before release (see Task 9); tests do not depend on it.
  - `public sealed record ContentArchive(string Name, long Bytes, string Sha256);`
  - `public sealed record ContentFile(string Path, long Bytes, string Sha256, string Archive);`
  - `public sealed record ContentRelease(string Id, int Version, string Title, IReadOnlyList<ContentArchive> Archives, IReadOnlyList<ContentFile> Files);`
  - `public static class ContentManifest { public const int MaxManifestBytes = 4 * 1024 * 1024; public const long MaxArchiveBytes = 1536L * 1024 * 1024; public const long MaxUnpacked = 2L * 1024 * 1024 * 1024; public const int MaxFiles = 5000; public static ContentRelease Verify(byte[] data, byte[] signature); internal static ContentRelease Verify(byte[] data, byte[] signature, string publicKeyHex); }`
  - `public sealed class ContentException(string message) : Exception(message);`
  - `public ReleaseDownloader(TimeSpan downloadDeadline)` public constructor; `internal ReleaseDownloader(HttpMessageHandler handler, string? version = null, TimeSpan? downloadDeadline = null)`; `public static readonly TimeSpan ContentDeadline = TimeSpan.FromMinutes(30)` on `ReleaseDownloader`.

Manifest JSON (schema 1), as the backend publishes it:

```json
{ "schema": 1, "kind": "content", "id": "vegas1-maps-pack", "version": 1, "title": "vegas1",
  "archives": [ { "name": "PAK_01_Kitchen.zip", "bytes": 123, "sha256": "<64 hex>" } ],
  "files": [ { "path": "KellerGame/Content/CookedPc/MapsPack/PAK_01_Kitchen/PAK_01_Kitchen.usdx",
               "bytes": 456, "sha256": "<64 hex>", "archive": "PAK_01_Kitchen.zip" } ] }
```

- [ ] **Step 1: Write failing tests** using `UpdateFixture.Sign` and `UpdateFixture.PublicKeyHex`:
  - `VerifiesAWellFormedManifest` — returns 1 archive, 1 file, version 1.
  - `RejectsBadSignature`, `RejectsUpdateKind` (`"kind": "update"`), `RejectsOtherTitle` (`"vegas2"`), `RejectsOtherId`.
  - `RejectsUnsafePaths` — theory over `"../x"`, `"C:/x"`, `"/x"`, `"KellerGame/Binaries/x.dll"`, `"KellerGame/Config/DefaultEngine.ini"`, `"KellerGame\\Content\\x"`.
  - `RejectsFilesOutsideTheAllowedFolders` — allowed prefixes only: `KellerGame/Content/CookedPc/MapsPack/`, `KellerGame/Localization/<LANG>/PAK_` and `KellerGame/ImageMap/PAK_`, and extensions `.usdx .rmpc .shaders .ini .uppc .int .fra .deu .esp .ita .pol .rus .dds`.
  - `RejectsUnknownArchiveDuplicatePathsAndOversize` — file names an archive not listed; duplicate path (case-insensitive); total > `MaxUnpacked`; archive > `MaxArchiveBytes`.
  - Every rejection throws `ContentException("The Maps Pack download list is unsupported or unsafe")`, except bad signature: `ContentException("The Maps Pack download list could not be verified")`.
  - In `DownloadTests`: `ContentDeadlineIsLongerThanUpdateDeadline` — a fake handler streaming slowly past a short injected update deadline succeeds when constructed with a longer `downloadDeadline`.
- [ ] **Step 2:** Run the new tests. Expected: build failure.
- [ ] **Step 3: Implement** `ContentManifest.Verify` mirroring `ReleaseManifest.Verify` (reuse `Json` helpers, `ReleaseManifest.Sha256Hex()` and `ReleaseManifest.VerifySignature`). Path check: forward slashes only, no `..`, no rooted path, allowed prefix and extension as above. Add the deadline parameter to `ReleaseDownloader`, defaulting to the existing 180 s.
- [ ] **Step 4:** Run the full suite. Expected: PASS.
- [ ] **Step 5:** Commit: `git commit -m "Verify signed content manifests and allow long content downloads"`.

---

### Task 4: AppPaths and the install record

**Files:**
- Modify: `dotnet/src/VegasReborn.Core/AppPaths.cs`
- Create: `dotnet/src/VegasReborn.Core/Content/MapsPackRecord.cs`
- Test: `dotnet/tests/VegasReborn.Core.Tests/Content/MapsPackRecordTests.cs`

**Interfaces:**
- Produces:
  - `AppPaths.MapsPack => Path.Combine(Local, "content", "vegas1-maps-pack")`; files live in `MapsPack/files/<game-relative path>`, staging in `MapsPack/staging`, record at `MapsPack/record.json`.
  - `public sealed record MapsPackRecord(int Version, IReadOnlyList<ContentFile> Files)` with `static MapsPackRecord? Read(string path)` (null when missing or malformed) and `void Write(string path)` (via `PrivateFile.WriteText`).
  - `public enum MapsPackState { NotInstalled, Installed, Damaged }` (same file) and the static method `MapsPackRecord.Check(AppPaths paths, out MapsPackRecord? record) -> MapsPackState`: `Damaged` when any recorded file is missing or its size or SHA-256 differs.

- [ ] **Step 1:** Tests: `RoundTripsARecord`, `MalformedRecordReadsAsNull`, `MissingFileIsDamaged`, `ChangedHashIsDamaged`, `NoRecordIsNotInstalled` (a `files/` folder without a record still counts as not installed).
- [ ] **Step 2:** Run; expected build failure.
- [ ] **Step 3:** Implement. `Check` hashes with `PrivateFile.Sha256`.
- [ ] **Step 4:** Run suite; PASS.
- [ ] **Step 5:** Commit: `git commit -m "Record the installed Maps Pack"`.

---

### Task 5: Download, verify, install and remove

**Files:**
- Create: `dotnet/src/VegasReborn.Core/Content/MapsPackInstaller.cs`
- Test: `dotnet/tests/VegasReborn.Core.Tests/Content/MapsPackInstallerTests.cs` (fake `IReleaseDownloader` serving bytes from a dictionary keyed by URL; manifests signed with `UpdateFixture`)

**Interfaces:**
- Consumes: `ContentManifest`, `ContentInfo`, `MapsPackRecord`, `AppPaths.MapsPack`, `IReleaseDownloader`.
- Produces:
  - `public sealed record MapsPackProgress(long Bytes, long TotalBytes);`
  - `public sealed class MapsPackInstaller(AppPaths paths, IReleaseDownloader downloader, string? publicKeyHex = null, string? baseUrl = null)` (the optional values are for tests; production uses `UpdateKey.PublicKeyHex` and `ContentInfo.BaseUrl`), with:
    - `Task<ContentRelease> FetchAsync(CancellationToken ct)`: downloads `content.json` and `content.sig` (64 bytes), returns the verified release.
    - `Task InstallAsync(IProgress<MapsPackProgress>? progress, CancellationToken ct)`.
    - `void Remove()`.
    - `Task<bool> UpdateAvailableAsync(CancellationToken ct)`: true when a record exists and the published version is greater.

Install algorithm (exact order):
1. Fetch and verify the manifest.
2. Delete any existing `staging/`, then create it.
3. For each archive: `DownloadFileAsync(baseUrl + name, archive.Bytes, staging/name)`, then check size and SHA-256 against the manifest.
4. Extract each zip into `staging/files/`. Reject an entry not listed for that archive, a duplicate, or a path escaping the folder (use `SafePaths.Beneath`). Check each extracted file's size and SHA-256. Reject a listed file that is missing.
5. Delete the old record, then the old `files/`. Move `staging/files` to `files`. Write the new record. Delete `staging/`.
6. Map `UpdateException` and IO errors to `ContentException` with plain text ("The Maps Pack download failed. Check your connection and try again." / "The Maps Pack could not be installed: <reason>").

Remove: delete the record first, then `files/` and `staging/`.

- [ ] **Step 1: Failing tests:** `InstallsVerifiedFilesAndWritesRecordLast`, `ArchiveHashMismatchInstallsNothing`, `ZipEntryNotInManifestInstallsNothing`, `TraversalEntryInstallsNothing`, `MissingListedFileInstallsNothing`, `CancelledInstallLeavesNoRecord` (cancel during archive 2 of 2; state is `NotInstalled`; the next install succeeds), `ReinstallReplacesOldFiles`, `RemoveDeletesRecordThenFiles`, `UpdateAvailableWhenPublishedVersionIsNewer`. For every failure, `MapsPackRecord.Check` returns `NotInstalled` and no `files/` exists.
- [ ] **Step 2:** Run; expected build failure.
- [ ] **Step 3:** Implement as specified.
- [ ] **Step 4:** Run suite; PASS.
- [ ] **Step 5:** Commit: `git commit -m "Download and install the Maps Pack into the content store"`.

---

### Task 6: Link the pack into game copies

**Files:**
- Create: `dotnet/src/VegasReborn.Core/Content/MapsPackLink.cs`
- Modify: `dotnet/src/VegasReborn.Core/Launch/GamePreparer.cs` (Vegas 1 branch, before `MissionPlayerLimits`), `dotnet/src/VegasReborn.Core/Dedicated/DedicatedHost.cs` (after `PrepareCopyAsync`, Vegas 1 only)
- Test: `dotnet/tests/VegasReborn.Core.Tests/Content/MapsPackLinkTests.cs`, plus one case in `dotnet/tests/VegasReborn.Core.Tests/Dedicated/DedicatedTests.cs`

**Interfaces:**
- Consumes: `MapsPackRecord.Check`, `MissionPlayerLimits.Apply(copy, source, extendModes)`.
- Produces: `internal static class MapsPackLink { public const string LinkedList = ".vegasreborn-mapspack.json"; public static bool Apply(AppPaths paths, string copy); }`. It returns true when the pack is installed and linked, and throws `LaunchException("The Maps Pack is damaged. Download it again or remove it.")` when the state is `Damaged`.

Behaviour:
- **Installed:** for each record file, if the copy's file is absent or its hash differs, create a hard link (`CreateHardLink` P/Invoke in `Update/NativeMethods.cs`, or the .NET API if available) to `MapsPack/files/<path>`, falling back to `File.Copy` across volumes. Remove any path listed in the copy's `LinkedList` that is no longer in the record. Write `LinkedList` (paths only). Then call `MissionPlayerLimits.Apply(copy, paths.MapsPack + "/files", extendModes: false)` so the pack's own inis get the 16-player raise from their store originals. `PrivateFile.Write` replaces by move, so the store file is never modified through the link.
- **Not installed:** delete every path in the copy's `LinkedList` (only those, using `SafePaths.Beneath`), remove now-empty `MapsPack` folders, delete `LinkedList`, return false.
- **GamePreparer:** `var pack = title.Key == TitleCatalog.Vegas1.Key && MapsPackLink.Apply(paths, destination);` then `MissionPlayerLimits.Apply(destination, source, extendModes: pack)`.
- **DedicatedHost:** for Vegas 1, `var pack = MapsPackLink.Apply(_paths, game);` then `if (pack) MissionPlayerLimits.Apply(game, source, extendModes: true);`. Map `LaunchException` to `DedicatedException` with the same text.
- **Fallback:** only if Task 1 recorded that `+Paths` is needed, `Apply` also ensures `+Paths=..\KellerGame\Content\CookedPc\MapsPack` is present in the copy's `KellerGame/Config/PC/DefaultEngine.ini` while installed, and absent otherwise.

- [ ] **Step 1: Failing tests:** `InstalledPackIsLinkedAndInisRaised`, `RelinkIsANoOpWhenUnchanged` (no writes; compare last-write times), `RemovedPackIsUnlinkedOnNextPrepare` (afterwards the copy tree equals its pre-install tree byte for byte), `DamagedStoreRefusesToLink`, `LinkedListNeverDeletesOutsideTheCopy` (a hand-edited list with `../x` throws or skips without deleting), `DedicatedCopyGetsPackAndModes`.
- [ ] **Step 2:** Run; expected failure.
- [ ] **Step 3:** Implement and wire.
- [ ] **Step 4:** Run suite; PASS.
- [ ] **Step 5:** Commit: `git commit -m "Link the Maps Pack into client and server game copies"`.

---

### Task 7: Dedicated operator CLI

**Files:**
- Modify: `dotnet/src/VegasReborn.Server/Program.cs`
- Test: `dotnet/tests/VegasReborn.Core.Tests/Dedicated/DedicatedTests.cs` (if Program parsing is testable there; otherwise a parse test alongside the existing CLI tests)

**Interfaces:**
- Consumes: `MapsPackInstaller`, `MapsPackRecord.Check`.
- Produces: commands `maps-pack install`, `maps-pack remove`, `maps-pack status` (Vegas 1 only; any other `--game` is a usage error, "The Maps Pack is only for Rainbow Six Vegas"). Add them to the usage text under the existing commands as `maps-pack install|remove|status   Manage the Vegas 1 Maps Pack`.

- [ ] **Step 1:** Failing tests: parse `maps-pack install`; reject `maps-pack` without a verb; reject `--game vegas2`.
- [ ] **Step 2:** Run; fail.
- [ ] **Step 3:** Implement; `install` prints percentage lines, `status` prints `Not installed`, `Installed (version N)` or `Damaged`. Refuse install and remove while the server for this root holds its host lock ("Stop the server first").
- [ ] **Step 4:** Run suite; PASS.
- [ ] **Step 5:** Commit: `git commit -m "Manage the Maps Pack from the dedicated server tool"`.

---

### Task 8: Maps section in the launcher

**Files:**
- Create: `dotnet/src/VegasReborn.App/ViewModels/MapsPackViewModel.cs`
- Modify: `dotnet/src/VegasReborn.App/ViewModels/GameViewModels.cs` (expose `MapsPack` on the Vegas 1 `GameViewModel`, null otherwise), `dotnet/src/VegasReborn.App/MainWindow.xaml` (section after "Game folder", before the fix sections, inside the existing page `ScrollViewer`)

**Interfaces:**
- Consumes: `MapsPackInstaller`, `MapsPackRecord.Check`, `GameProcesses.AnyRunning()`, `MainViewModel.IsBusy`.
- Produces: `MapsPackViewModel` with `State` (`MapsPackState`), `IsDownloading`, `Percent`, `StatusText`, `UpdateAvailable`, and the commands `DownloadCommand`, `CancelCommand`, `RemoveCommand`, `UpdateCommand`. Downloading counts toward `owner.IsBusy`, so Play is disabled during it.

Exact copy:
- Heading: `Maps Pack`
- Lead: `Kitchen, Roof, Ruined, Trains, Neon, Presidio, Mexico, Streets, Killhouse Double, Tower. Adds Terrorist Hunt, Deathmatch, Team Deathmatch and Attack & Defend to story missions.`
- Second line (muted): `Players need the Maps Pack to join matches that use these maps.`
- Button (not installed): `Download Maps Pack (686 MB)`. Compute the size from the manifest when fetched; fall back to this text.
- While downloading: progress bar plus `Downloading… {percent}%` and a `Cancel` button.
- Installed: `Installed` plus a `Remove` button. With a newer version: `A newer Maps Pack is available` plus an `Update` button.
- Damaged: `The Maps Pack is damaged. Download it again or remove it.` with `Download again` and `Remove`.
- Game running: buttons disabled, with tooltip `Close the game first.`
- Errors: show the `ContentException` message in `StatusText`.

- [ ] **Step 1:** Build and run the app (`run` skill or `dotnet run --project dotnet/src/VegasReborn.App`) with an isolated root (never the user's normal install). Confirm the section appears only on the Vegas 1 page. Switching pages keeps window size and position, and the page scrolls.
- [ ] **Step 2:** Point `MapsPackInstaller` at a local test release (a test-only base URL) or exercise it through the view model with the fake downloader. Check each state's copy against the list above.
- [ ] **Step 3:** Run the full suite; PASS.
- [ ] **Step 4:** Commit: `git commit -m "Add the Maps Pack section to the Vegas 1 page"`.

---

### Task 9: Release content, retail checks, docs

**Files:**
- Modify: `dotnet/src/VegasReborn.Core/Content/ContentInfo.cs` (the confirmed repository), `docs/PLAYER-README.txt` (one short Maps Pack paragraph), `docs/HOSTING.md` (`maps-pack` commands)

- [ ] **Step 1:** Ask the user for the public content repository name and confirm it is not `Lumbridge/Vegas-Reborn`. Set `ContentInfo.Repository`.
- [ ] **Step 2:** In the backend project (not this repo), the user or a backend session publishes release `vegas1-maps-pack-v1`: zips per `MapsPack/PAK_*` folder plus one `PAK_Common.zip` for Localization and ImageMap files, then `content.json` signed with the release key into `content.sig`. Paths, prefixes and schema are as in Task 3.
- [ ] **Step 3:** Retail checks with the private-audio route, using an isolated acceptance install (never the user's normal app): (1) Download from the button; pack maps appear and load. (2) Each added mode appears on a listed story mission, and one runs. (3) Host with the pack while a second client without it joins; record the result in the spec. (4) Remove, relaunch, and confirm stock maps only.
- [ ] **Step 4:** Update the docs, run the full suite, commit: `git commit -m "Document the Maps Pack"`.
- [ ] **Step 5:** Merge `feature/maps-pack` into `main` (no PR), per project practice. Releasing the launcher follows `tools/build_release.py` / `tools/publish_release.py` separately.
