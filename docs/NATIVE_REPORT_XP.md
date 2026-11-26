# Multiplayer post-game earned XP

The client adapter adds the local player's actual saved PEC XP award to the
original co-op and competitive post-game reports. Co-op's successful report
keeps the original mission score and displays `score / earned XP` in its total
row, labelled `SCORE / XP`. Its failure description retains the original message
and adds earned XP on a following line. Competitive results label their final
award `XP EARNED` and show the number alone.
The scope includes competitive modes1–6, co-op story7 and co-op Hunt8, using
the original game-mode getter. This does not add a reward or change its amount.

## Recovered source contract

Read-only analysis of the existing reconstructed Steam client image, SHA256
`888db472adf18852b2db5337e8f5b30b0882c3f5df2586d6eac6409140834a9e`,
preferred base `10900000`:

- Player-controller end-round handler `111d6df0` supplies the local PEC handle
  and original statistics provider to controller `10e63b90` (`thiscall`, RET8).
  That function resets its handle's40-byte context, runs the original evaluator
  and award transaction synchronously, and returns a success byte.
- Transaction `10e6bc40` retains native maximum-rank and idle checks.
  Application `10e6b930` reads raw profile XP with `10e66320`, adds context+4
  and writes through `10e66310`. The adapter observes raw profile+10 before
  and after the enclosing controller transaction; it never calls the setter.
- The controller's contexts are at+10, count+14. Native `10e63c00` resolves
  `contexts + handle*28`. The original local profile resolver is `10e656c0`
  on controller+8's service. Each captured result is bound to that context and
  handle, and invalidated before another transaction starts.
- Co-op report `10981940` belongs to original vtable `11eb5d48`. Loader
  `10980070` binds the sixth label at+f8 and final mission-score value at+128.
  It deliberately does **not** bind a sixth middle value at+110. The adapter
  uses the bound total value, preserving the native numeric score before the
  XP suffix. Failure uses the existing description at+d4.
- Competitive report `10985520` belongs to original vtable `11eb6c44`; its
  final label/value are+154/+13c. Native `10986230` invokes it while showing
  results. Original text setter `1095bf30` updates the existing text and its
  redraw flags. `1095bc40` identifies the original string layout used to read
  co-op score/description before appending the requested text.

The adapter requires the supported client identity, executable startup targets
and exact native function signatures. Client native13 has a fresh DLL name;
the companion requires `reportXpVerified`. Dedicated modules remain native4.
Native reports are restricted to their original vtables, multiplayer modes and
an existing original LAN/Online session. Awards are also bound to that session;
offline solo reports retain their original text.
Repeated refreshes replace the XP suffix. Failed or missing observations show
`XP unavailable` (competitive: `N/A`); a successful unchanged profile shows `0 XP`. Declining or
wrapped XP is treated as unavailable. There is no account, file or socket write.

## Field widths (fix for `score /` with no XP)

The first release showed only `score /` in the co-op total row. The PC menu
layout `KellerGame/Content/MenusPC/MP_RSMenus.mgb` (SHA256
`6c889a78fcec5feca05aaf43fe68be065ae7ccbfaad6814d99c47f1ded041f8b`, big-endian;
each text element stores x0, x1, y0, y1 as 16-bit values 0x24 bytes before its
count-prefixed UTF-16BE placeholder, then an alignment word: 0 left, 1 centre,
2 right) defines the After Action Report rows:

| Element | Rectangle (x, y) | Align | Placeholder |
| --- | --- | --- | --- |
| `YOUR TOTAL SCORE` label (+f8) | 32-178, 320-340 | left | `YOUR TOTAL SCORE` |
| `L_ScoreValue` (+128) | 345-420, 320-340 | right | `= 8,000` |
| Other rows' points cells | 345-420 | right | `1,000`, `-2,500` |
| Competitive final value (+13c) | 218-294, 101-119 | right | `9999` |
| Co-op failure description (+d4) | 166-472, 226-334 | centre | `YOUR TEAM WAS ELIMINATED...` |

`L_ScoreValue` is a 75 px, one-line points cell sized for seven characters, so
`1234 / 250 XP` did not fit and everything after `1234 /` was lost. Loader
`10980070` binds names from `122e662c`/`122e6600`/`122e6614`; the score row has
no middle element, leaving x 179-344 empty. When the fix is enabled, the
companion rewrites its game copy's `MP_RSMenus.mgb` from the pinned original
with that element's left edge at x 184 (`ReportXpLayout`). The right edge,
height and alignment are unchanged, so the game's right-aligned score stays in
place. A disabled fix or an unpinned original restores the installed file. The
co-op label is shortened to `SCORE / XP` so it stays inside its 146 px label,
and the competitive value, in a 76 px cell, is now the number alone, or `N/A`
when unavailable. The multi-line failure description already had room.

## Validation and remaining acceptance

The Windows client adapter compiles. The mapped x86 native test exercises the
award wrapper, consecutive matches, zero awards, profile isolation, failed and
missing profiles, unsigned wrap, replaced contexts, report callbacks for all
eight multiplayer mode enums, co-op success/failure, repeated rendering and
preservation of the native mission score. Existing native checks also pass.
The Python suite passes108 tests with one skip, including the new native-module
readiness requirement. These are synthetic checks, not retail interoperability.

The ignored research exports are in the separate backend workspace under
`local/analysis/report-xp/`. Game files and captures are excluded from Git.
Rendered layout, natural match completion and host/remote-player reports still
require a live retail check. The current retail harness states that private
audio is unverified and requires normal-audio authorization. No automated
retail process was launched, and live acceptance remains open. Signed release publication and installation
are recorded separately from these implementation checks.
