# Original online compatibility integration

This source integration starts from companion 0.4.5, commit
`08a8e8fbab280743528b1e33af74cac8567a4452`. It preserves that release's
startup readiness, window/display settings, intro controls, account handling,
updater coordination and UI layout. Changes use the current `vegas_reborn`
namespace; backend services remain in Vegas-Reborn-Server.

## Native module identities

Companion 0.4.5 already shipped a different native8 module for startup/display
work. The combined online adapter therefore uses the fresh identity
**client native9 / `VegasCompanion-9.dll`**. It must not overwrite or relabel the
active `VegasCompanion-8.dll`. The operator's dedicated adapter is
**native4 / `VegasDedicated-4.dll`**, adding the reviewed TEXTSERVER engine-tick
producer to the former dedicated2 source.

The integration base is application 0.4.5; the release owner selected candidate
0.4.6 after the port began. Publication still requires coordinated version
reservation, the exact merged main commit and the repository's normal signed
build/publish workflow. No candidate is installed over the user's active
application as part of this source work.

## Integrated behavior

- The native Player Info entry uses original rank/service-record screens.
- The original Ranked selection is restored in the native match UI. Its
  selection and original URL serialization remain authoritative.
- The copied game's carrier receives the reviewed deterministic repair for
  unbound computed leaderboard columns. The installed original is read only.
- Native leases request `native-base-v1` only after owned-carrier validation;
  every subsequent private credential request retains that opt-in. A missing
  server acknowledgement fails closed.
- The original native statistics SDK sends session registration and host
  outcomes. The shared producer supports dedicated TEXTSERVER and listen hosts,
  preserving the original calculation and authority/session checks.
- The scoped solo-hunt adapter uses the original qualified-player predicate,
  permits one qualifying player only in the original Coop Hunt mode, and
  retains the existing Ready, countdown and authority checks.
- The PEC availability facade delegates its actual item ID to the original
  item/group/bundle availability helper. It retains the original named-rank
  comparison, starter equipment and missing-item behavior; XP is not changed.
- Startup requires the corresponding native module's guarded feature
  installation reports. Hook readback is not a claim that a match, outcome or
  populated leaderboard was verified with the retail client.

The new carrier helper is ordinary source code; no game carrier, executable,
account session, character or capture is included in this repository.
Rank and unlock policy still comes from the reviewed original-game adapters,
rather than an invented reward table.

## Build and updater boundary

`Build-Companion.ps1` produces the fresh versioned adapter names and includes
the compatibility headers in its source-hash record. `tools/build_release.py`
packages client9 alongside the existing native tools, and places dedicated4 at
`runtime-<VERSION>/native/VegasDedicated-4.dll`. Existing 0.4.5 updaters already
allow that versioned runtime subtree; they would reject a new versioned
dedicated DLL under `local/companion/bin` before installing updated verifier
code. The frozen dedicated loader uses `sys._MEIPASS/native`; development uses
the local native build directory. The new updater also permits versioned
dedicated names under the native-bin boundary for future layouts, with the
existing signed-manifest and file-hash checks.
Game copies, accounts, preferences and saves remain outside update ownership.

Normal hosting remains in the game's Create Match flow. Dedicated hosting
remains a separate operator tool with its existing explicit CLI support.

## Review scope

The adapters were reconstructed from original code/data and exported native
serializers in the separate backend research workspace. This integration uses
the reviewed source headers and scoped recipes; it does not replace the modern
companion with a legacy source tree.

No retail games, socket exercises or test suite were launched for this port.
Compilation and source review are recorded separately from runtime acceptance.
