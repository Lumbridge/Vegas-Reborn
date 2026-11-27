# Vegas 2 stuck automatic fire sound

The Vegas 2 port of `NATIVE_FIRE_SOUND_LOOP.md`. Grounded in the Steam
`R6Vegas2_Game.exe` (SHA-256 `e8ab9430…`, image base `0x10900000`, timestamp
`0x484efa91`; private decrypted copy outside Git) and the cooked
`KellerGame/CookedPC/Engine.uppc`. Implementation:
`tools/companion/native_fire_sound_loop_vegas2.h`. The cause is established from the code;
the fix has not yet been tried in an online match.

## Cause (same as Vegas 1)

- Each pawn has an `AR6SoundReplicationInfo` (vtable `0x11d7d97c`). Its relevance
  override (`+0x258`, `0x10cc19f0`) always returns true. Its pawn is `+0x264` and its cue
  table `+0x274`.
- `PlayWeaponSound` (vtable `+0x630`, `0x10cc1ae0`; natives `0x10b76c10`, `0x10b76c80`,
  `0x10b76cb0`) plays the cue table's `+0x284` when full-auto fire starts (6), `+0x288` on
  release (10) and `+0x28c` on a forced stop (13). Each cue is played with replication on
  through `AActor::PlaySound` (`0x11352360`). The other layers play only for a locally
  controlled pawn and are not replicated.
- On the host, `PlaySound` sends each cue to every client connection's PlayerController
  through `HearSound` (`0x11352520`), which checks audibility when it is sent and then
  calls `ClientHearSound`. In `Engine.uppc` that function's flags are `0x01020540`: a net
  function without `NetReliable`, as in Vegas 1.
- On a client, `execClientHearSound` (`0x110ec230`) calls the handler `0x11352630`
  (PlayerController vtable `+0x79c`). Start and stop arrive as two separate unreliable
  messages. A lost or culled release leaves the start cue looping.

## Fix (clients only)

- The handler is hooked. When a remote pawn's start cue arrives a track begins, and its
  release or forced-stop cue clears it. A pawn with a Controller (`+0x270`, read by
  `IsLocallyControlled` `0x1116e090`) and an SRI with `Role` (`+0x6e`) `ROLE_Authority` (3)
  are ignored.
- The SRI's per-frame override (vtable `+0x250`, `0x10cc18b0`) is replaced through its
  slot. `AActor::Tick` calls this override for every role. It watches the pawn's
  `FlashCount` (`+0x2b3`). `IncrementFlashCount` (`0x1116fbe0`) and `ClearFlashCount`
  (`0x1116fc30`) force the next net update, and the pawn's replication list (`0x1111ec1c`)
  sends it to every client except the owner.
- The stop decision is Vegas 1's (`NativeFireSoundLoop::Decide`): 0 for 0.75 s, unchanged
  for 2 s, or a dead pawn. A pawn is dead when `Health` (`+0x354`, read by
  `AR6Pawn::IsAlive` `0x10a64ee0`) is at most 0 or when it is torn off (`+0x44` bit
  `0x100`, read by the actor channel at `0x112ffd6d`).
- When no release arrived, the hook calls the SRI's own `PlayWeaponSound(10)`, or `(13)`
  for a dead pawn with a forced-stop cue. Vegas 2's `ForceStopWeaponSound` needs the
  host's loop handle (`+0x29c`), which a client does not have, so it is not used. On a
  client `PlaySound` sends nothing, because its send loop covers only client connections.
  It plays the cue through `eventClientHearSound` (`0x1134f510`) into the same handler, so
  the cue clears the track. A stop that is too far away to be heard is retried every 2 s,
  up to 15 times.
- Install checks the image identity, the handler and its vtable slot, the override, both
  vtable slots, `StopWeaponSound`, the three cue reads and each field read listed above.
  A mismatch reports `fireSoundLoopVerified: false`, and the launcher asks for an update,
  as for Vegas 1.

## Not verified

No retail launch or online match was made. `tests/native/vegas2_network_test.cpp` covers
tracking, filtering and the stop calls against a synthetic image region. The signatures
were checked against the decrypted client.
