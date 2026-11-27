# Vegas 2 player limits

The Vegas 2 port of the player limits in `NATIVE_PLAYER_LIMITS.md`. Grounded in the Steam
`R6Vegas2_Game.exe` (SHA-256 `e8ab9430…`, image base `0x10900000`, timestamp
`0x484efa91`; private decrypted copy outside Git), the cooked
`KellerGame/CookedPC/R6Game.uppc` and the shipped map ini files. Implementation:
`MissionPlayerLimits.Vegas2Folders`, `GamePreparer.ApplyMissionContent` and
`tools/companion/native_player_limit_vegas2.h`. Not yet tested in a retail match with more
than four players.

## Where the original limits come from (same as Vegas 1)

- **Map ini files.** `KellerGame/CookedPC/MapsBishop/*/*.ini`, `KellerGame/CookedPC/MapsDLC/*/*.ini`
  and copies of the DLC files in `KellerGame/DLC_Casino_01`, `DLC_Murdertown_2` and
  `DLC_Training_02`. They use the same `[GameMode*]` sections as Vegas 1. Every adversarial
  mode ships `m_iMaxPlayers=16` and `m_iMaxPlayersListenServer=14`, `GAMEMODE_TERROHUNT` ships
  `m_iMaxPlayers=4` with no listen-server line, and `GAMEMODE_STORY` ships 2.
  `Config/*ServerOptions.ini` is not a mission file and is not changed.
- **`UR6GameManager::GetMaxPlayersAllowed(mode)`** (`0x10c85070`, `UR6GameManager` vtable
  `0x11d73cd4` + `0x100`, slot `0x11d73dd4`; `execGetMaxPlayersAllowed` `0x110ea050` calls
  it through the vtable). In multiplayer, mode 7 returns the `AR6CoopStoryGame` class default
  `MaxPlayersAllowed` (`+0x28c`), mode 8 the `AR6CooperativeGame` default and other modes the
  `AR6MultiPlayerGame` default. In `R6Game.uppc` those defaults are 4
  (`Default__R6CooperativeGame`) and 16 (`Default__R6MultiPlayerGame`).
- Mode 8 is Terrorist Hunt: `AR6CoopTerroristHuntGame` (vtable `0x11db2d34`) answers
  `GetGameMode` (`+0x750`) with `0x10d40b20` (`mov eax,8; ret`).
- **`CreateTeam`.** The native `execCreateTeam` (`0x10b8b920`) calls vtable `+0x834`, which
  for every co-op class is `0x10d3edd0`. It passes the game's `MaxPlayersAllowed` (`+0x28c`,
  read at `0x10d3ef0e`) to the team's `Init` (`+0x638`) as its size.

## Changes

- The launcher rebuilds each copied mission ini in those five folders from the installed
  original with Vegas 1's rules. Terrorist Hunt goes from 4 to 16 and gets
  `m_iMaxPlayersListenServer=16`. Adversarial listen-server caps go from 14 to 16. Story and
  unexpected values are kept.
- The adapter replaces the `GetMaxPlayersAllowed` slot so mode 8 returns 16 when the
  original returns 4. It also hooks `AR6CoopTerroristHuntGame`'s InternalConstructor
  (`0x10d40bc0`, registered with the class at `0x10d40c32`). This runs after
  `StaticConstructObject` has copied the class defaults, as in Vegas 1. After it runs, a game
  object with the Terrorist Hunt vtable has `MaxPlayersAllowed` changed from 4 to 16. Story
  co-op keeps its values.
- Install checks the image identity, the `GetMaxPlayersAllowed` code and slot, the
  constructor code with its call and vtable store, the mode getter and its slot, and the
  `CreateTeam` slot and size read. A mismatch reports `playerLimitVerified: false`, and the
  launcher asks for an update, as for Vegas 1.
- The Vegas 2 executable already has `IMAGE_FILE_LARGE_ADDRESS_AWARE` (characteristics
  `0x122`), so the Vegas 1 memory-limit change is not needed.

## Not verified

No retail launch or online match was made. `tests/native/vegas2_network_test.cpp` covers
the mode and value filtering and the constructor change against a synthetic image region.
The signatures were checked against the decrypted client.
