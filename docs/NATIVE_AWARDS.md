# Vegas 1 player awards

PLAYER INFO shows RIBBONS, MEDALS, BADGES, TROPHIES and ACHIEVEMENTS as
"0 OF 0" because the PC game never creates its award list and its unlock step
does nothing. The Vegas 1 adapter creates the game's own 36 awards for the
local profile, lets the game's own checks unlock them, and saves them in the
account directory.
Implementation: `tools/companion/native_awards.h`.

## Original evidence (client VAs, base 0x10900000)

### Award manager

- Singleton at [0x123fcfe0], created by the getter 0x10d18710 (size 0x24,
  vtable 0x11f6b460). +0x18 is an array of per-profile award TArrays
  (12 bytes: data, count, max), +0x1c the list count (4).
- CreateAll 0x10d19320 (this=manager, list) clears the list (0x10d18830) and
  appends factory 0x10de57d0(index, list, manager) for index 0..0x23.
  ResetForList 0x10d193b0 bounds-checks the list and jumps to CreateAll.
- Award base (ctor 0x10d24810): +4 manager, +8 unlocked, +0xc list. The 36
  vtables are 0x11f6b478..0x11f6b9f0 (0x28 apart).
- Serialize 0x10d193d0 (this=manager, archive, list) returns 0 for an
  out-of-range list. It serializes the version dword [0x122ecea8] (3), and when
  loading (archive+0x14 != 0) calls CreateAll and returns 0 on a version
  mismatch. Then, for each award, 0x10d24940 serializes award+8 (4 bytes) and
  calls the award's vtable +0x24. Those 12 serializers are a no-op
  (0x10d248b0), ByteOrderSerialize of 4 bytes at +0x10 or 8 bytes at +0x18, or
  one archive Serialize of 1 byte at +0x10 (0x10debbe0). ByteOrderSerialize
  0x10921260 reads only archive+0x40 and calls archive vtable +4.
- Unlock 0x10d18900 (this=manager, controller, award) maps the controller
  through [[0x123c1a28]+0x88]+0x40, bounds-checks, requires [0x1240e72c] == 0
  and award+8 == 0, then calls manager vtable +8. On PC that entry
  (0x11f6b468) is the stub 0x10de4540 (`mov eax,1; ret 4`), so nothing is ever
  unlocked. Callers: 0x1096eabc, 0x111a26cb, 0x111c9114, 0x111d7058. Progress
  updates go through 0x10d18a80, 0x10d18b10, 0x10d18890 and 0x10d18960; every
  award path first calls the getter.

### Why the list is empty on PC

- The profile container [0x123fea0c] (ctor 0x10d20c90) writes -1 to its
  profile index +0x1c8, and its only setter 0x10d20740 has no callers. Its
  award block passes that index to ResetForList (0x10d20a50) and Serialize
  (0x10d22ad0).
- More importantly, the container's block serializers 0x10d21bd0 (options),
  0x10d22ad0 (awards) and 0x10d22b00 (PEC) and its version check 0x10d20be0
  have no callers or references in the PC build. The PC profile file
  `R6_Profile%s` (name builder 0x10d030d0, type 4, format at 0x11f5b834) is
  saved by 0x11175f60 and loaded by 0x11175f90. Both serialize only the options
  object (0x11175cc0). `R6_ProfileOPTIONS` therefore holds no award section,
  and no PC save file ever has.
- Setting +0x1c8 would not create, load or save awards on PC. It would also
  change the options block (0x10d21bd0 reads options manager Get(index) instead
  of the player's options) and the PEC block (0x10d20b70, 0x10d22b00 pass the
  index to the PEC facade). The adapter leaves the container alone.

## Adapter

- Getter 0x10d18710 (MinHook): after the original returns the manager, the
  adapter checks the manager vtable and list bounds, then takes the local
  profile index [[0x123c1a28]+0x88]+0x4c (the index the original profile save
  0x11175f60 and load 0x11175f95 use). Once per manager, if that list is empty,
  it loads the saved file through the original Serialize, or creates the list
  through the original ResetForList. A list the original already created is
  adopted unchanged.
- Manager vtable +8 (0x11f6b468, data pointer): replaced with a routine that
  checks the award belongs to this manager, sets award+8 = 1, and saves. It
  returns 1 like the stub. The original Unlock checks still run first. Vegas 2
  also logs and shows a notification; no Vegas 1 notification path was
  identified, so none is shown.
- Archive: a 0x80-byte object with archive+0x14 (loading) and archive+0x40
  (byte swapping, 0) and a vtable whose slot 1 copies bytes into or out of a
  bounded buffer (1024 bytes). The original Serialize writes and reads through
  it unchanged. An offline run of the original Serialize (save mode) on the
  decrypted image produced the expected bytes through this archive.
- Progress fields (+0x10, +0x18) change without an unlock. When the getter
  runs, at most every 2 seconds, the adapter serializes the list and writes it
  only if the bytes changed.

## Persistence

- File: `<account output>\player-awards-v1.dat`, the same persistent,
  account-scoped directory that holds `multiplayer-appearance-v1-*.dat`.
  Layout: `VRAW`, format 1, list index, 2 zero bytes, payload length (u32),
  payload (the original Serialize bytes), CRC-32 of the payload.
- Writes go to a temporary file and replace the target with
  `MOVEFILE_WRITE_THROUGH`.
- A load never reuses data that was not written by this adapter. A file with
  a bad header, length or checksum, a payload the original rejects (version),
  or a payload that is shorter or longer than the original reads, is moved to
  `player-awards-v1.dat.rejected` and fresh awards are created through
  ResetForList. A short read zero-fills and fails, and the partly read list is
  replaced. A file that exists but cannot be read creates fresh awards and
  blocks saving for that session, so the file is not overwritten.
- The file is not in the game's profile folder and is not a `SaveSlots` entry,
  so it is not cloud-synced. Syncing it needs the backend to accept a new slot.

## Guards and receipt

`NativeAwards::Install(base, Output)` runs after the existing client setup.
It requires the client image identity (TimeDateStamp 0x47106421, ImageBase
0x10900000, SizeOfImage 0x1f56000) and exact bytes for the getter, Unlock,
CreateAll, ResetForList, Serialize, the version store/compare/serialize
helpers, the per-award serialize helper, ByteOrderSerialize and its thunk, the
manager vtable entries and the commit stub, plus every award vtable's +0x24
entry and each of the 12 serializers. The getter and Serialize take part in
the executable-page preflight. The receipt reports `nativeAwardsVerified` and
`nativeAwardsStatus`; the launcher requires `nativeAwardsVerified`.
Verification describes guarded installation only.

## Not verified in game

Synthetic native tests (`tests/native/awards_test.cpp`) cover creation, unlock,
progress saving, loading and rejection. The guards were checked against the
decrypted image, and the original Serialize was run in save mode. Still
unverified: the Player Info counts after creation (including whether the
ACHIEVEMENTS row reads these lists), which in-game events
unlock which awards, whether progress fields persist across matches as
intended, and the load path through the original CreateAll in the running
game.
