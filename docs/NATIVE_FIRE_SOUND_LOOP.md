# Vegas 1 stuck automatic fire sound

Grounded in the Steam `R6Vegas_Game.exe` (SHA-256 `667f659b…`, image base `0x10900000`), its
decrypted runtime sections and the cooked `Engine.uppc`/`R6Game.uppc` packages. The cause is
established from the code; the fix has not yet been tried in an online match.

## Cause

- Each pawn owns an always-relevant `AR6SoundReplicationInfo` (vtable `0x1207b720`, pawn at
  `+0x24c`, cue table at `+0x25c`). `PlayWeaponSound(byte)` (`0x11211f90`) plays the weapon's
  Loops cue (`table+0x26c`) when full-auto fire starts (6), the Ends cue (`+0x270`) on release
  (10) and the ForceStop cue (`+0x274`) on detach or destroy (13), all in sound slot 5.
- On the host, `AActor::PlaySound` (`0x10b326c0`) sends each cue to every other client through
  `APlayerController::HearSound` (`0x10b32880`). That checks `USoundCue::IsAudible` against the
  listener's position at that moment and then calls `ClientHearSound`, whose function flags
  (`0x01020540`) do not include `NetReliable`.
- The start and the stop are two separate unreliable, distance-checked messages, and nothing
  else tells a client that the loop should stop. A lost or culled Ends message (packet loss, a
  busy connection, or the listener crossing the 100 m `MaxAudibleDistance` during a burst)
  leaves the Loops cue playing until the pawn is destroyed. The host plays its own cues
  locally and the shooter plays their own, so only other clients hear it.

## Fix (`native_fire_sound_loop.h`, clients only)

- A hook on the client cue handler (`0x10b32980`, `ClientHearSound(pSound, eSlot,
  pSourceActor, …)`) notes when a remote pawn's Loops cue starts and when its Ends or
  ForceStop cue arrives. Pawns with a Controller (the player's own) and authority-side
  objects are ignored.
- The sound object's Tick (vtable slot `0x1207b920`) watches the pawn's replicated
  `FlashCount` (`+0x297`). `IncrementFlashCount` (`0x10b6a790`) and `ClearFlashCount`
  (`0x10b6a7e0`) set `NetUpdateTime` so every shot and the release replicate promptly.
- Firing has stopped when `FlashCount` has been 0 for 0.75 s, has not changed for 2 s, or the
  pawn is dead (`Health` `+0x338` ≤ 0) or torn off (`+0x48` bit `0x40`). If no Ends cue
  arrived by then, the hook calls the object's own `StopWeaponSound` (`0x112123f0`), or
  `ForceStopWeaponSound` (`0x11212410`) for a dead pawn. On a client these play the cue
  locally in slot 5 and send nothing. A stop the listener is too far away to hear is retried
  every 2 s, up to 15 times.
- Worst case: an extra end sound when the real one arrives more than 0.75 s late, or a burst
  that goes quiet early after a network stall of more than 0.75 s.
