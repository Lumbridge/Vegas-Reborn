# Retail procedural eye and head timing

The reported PEC eye darting and back-to-wall head bobble have a shared static
candidate in the original `UR6AnimNodeFrankHeadControl`. This change keeps game
rendering uncapped and re-evaluates the original pose every frame, while limiting
the advance of its procedural history and random samples to 60 steps per second.
60 Hz is a chosen compatibility rate, not a recovered original timing constant.
Lower frame rates retain their native one-step-per-evaluation behavior.

## Original evidence

Read-only analysis uses the unpacked original client image SHA256
`888db472adf18852b2db5337e8f5b30b0882c3f5df2586d6eac6409140834a9e`, preferred
base `0x10900000`. The installed executable is never modified. The source image
and Ghidra exports are private, outside Git, under the retail research workspace's
`local/eye-investigation/`. The image's original import inventory identifies
`MSVCR71.dll!rand` at IAT VA `0x125b1e40`.

The class name is at `0x1202dc94`; its main vtable starts at `0x1202de80`.
Evaluation at `0x11106450` receives the secondary interface at main object +0x34.
Its virtual calls dispatch the following main-object methods:

| Method VA | Vtable offset | Observed behavior | Replayed history |
| --- | --- | --- | --- |
| `0x111049c0` | `0x124` | Selects random eye targets; uses frame counter +0x1c4 and per-call probability | +0x144..0x17f and +0x1c4 |
| `0x11107a50` | `0x120` | Advances head PD state without elapsed-time input | +0x1a4..0x1af |
| `0x11107d10` | `0x128` | Samples random pitch/yaw noise and advances fixed-coefficient spring histories | +0x180..0x18f |
| `0x11108620` | `0x12c` | Samples random roll noise and advances its spring history | +0x190..0x197 |
| `0x11105180` | `0x134` | Advances blink frame counter, chooses random blinks and writes eyelid transforms | +0x1c0 |
| `0x111055c0` | `0x138` | Advances eyebrow PD state and writes facial transforms | +0x1c8..0x1cf |

The first method selects a new target using a per-call random test after a
four-frame minimum. Blink decisions also use per-call probabilities and frame
thresholds (20 and 150); their closing/opening stages use counter values 1/2.
The head pitch/yaw and roll methods update their histories from `rand()` and
fixed coefficients, with no elapsed-time argument. More render evaluations can
therefore advance these effects faster. The supplied recording establishes
visible eye movement; it does not measure the original client's FPS. The
back-to-wall symptom is user-reported, not separately recorded in this work.

## Change and guards

Each method retains its pre-step history and random samples. Further evaluations
inside the same QPC-derived 1/60 second interval replay that history and those
samples. They still run the native method against the current target and pose,
including its eye, eyelid and head bone writes. Simply skipping these methods
would omit their transforms on intervening frames.

Only history fields owned by each method are restored. Current targets, base
animation, body poses and unrelated object fields stay live. The original CRT
random import is forwarded normally outside a scoped procedural evaluation;
other threads have no replay context. A recursive lock serializes the small
history table, and entries are bounded to 128 nodes. Original node initialization
at `0x111035f0` clears saved history before pointer reuse. Calls inside a render
interval can follow different conditional branches as targets move; the replay
tape retains additional samples on demand, with capacity above the observed
maximum per method.

Installation checks the original PE machine, timestamp, image size and preferred
base, each hook's instruction bytes, all six native vtable slots and the original
CRT import. It rejects mismatches before creating hooks. Dedicated processes do
not install this adapter. The tested client native11 was rebased alongside 0.4.9 PEC defaults for release
0.4.10 as client native12, with a new versioned DLL filename, and
the companion requires `proceduralAnimationVerified` in its startup report.

## Validation and retail acceptance

- Native adapter compilation passed.
- A separate 32-bit test mapped the pinned unpacked original image at its
  preferred base, then installed all six method detours, the original node
  initializer detour and scoped CRT forwarding. Installation returned `ok` and
  cleanup passed. No original game function was executed. This establishes
  compatibility of the guards and detour installation with those original
  bytes, not a running retail-client result. Private test source/build are
  `local/verify_retail_hooks.cpp` and `local/native-tests/verify_retail_hooks.exe`.
- The native test suite passed, including 60/120/144/240/1000 FPS simulations,
  30 FPS behavior, random replay, live-pose reevaluation, independent eye
  counter restoration, initializer reset and invalid-image rejection.
- The Python suite passed 97 tests with one skip for an unavailable private
  saved-PEC fixture, using the existing dependency-complete build environment.
- An attempted bounded read-only Frida attachment to the previously running
  retail process was refused by Windows (`VirtualAllocEx`, access denied). No
  runtime readings or successful attachment are claimed.
- User-operated isolated retail process 28352 launched from this worktree. Its
  actual startup report returned `ready=true`, `nativeVersion=11`,
  `proceduralAnimationVerified=true`, `proceduralAnimationStatus=ok`. The native
  display report also confirmed its windowed device and viewport hooks.
- The user tested the PEC eye movement and back-to-wall head movement and
  confirmed: “Both movements look fixed.” This is user-confirmed visual
  acceptance in the original client, with rendering left uncapped by this patch.
- Exact rendered FPS, separate blink acceptance, prolonged gameplay, multiplayer
  replication and normal close/save were not independently measured here.

The test companion is prepared in a separate worktree with separate game and
profile directories. No unpublished candidate replaces the normal installed app.
This candidate has not been launched in the retail game.
