# Vegas 1 player limits and memory limit

Grounded in the Steam `R6Vegas_Game.exe` (SHA-256 `667f659b…`, image base `0x10900000`),
its decrypted runtime sections, the cooked `R6Game.uppc`/`Engine.uppc` script packages and
the shipped map ini files. Not yet tested in a retail match with more than four players.

## Where the original limits come from

- **Map ini files** (`KellerGame/Content/CookedPc/Maps/MP_*/*.ini`, read into
  `R6MissionConfig.m_arGameModeConfigs`). Every adversarial mode ships
  `m_iMaxPlayers=16` and `m_iMaxPlayersListenServer=14`, so a match hosted from the game
  is capped at 14. `GAMEMODE_TERROHUNT` ships `m_iMaxPlayers=4`. Story maps are not changed.
- **`UR6GameManager::GetMaxPlayersAllowed(mode)`** (`0x1116fd10`, vtable slot
  `0x1204b7a0`). In multiplayer, modes 7, 8 and 18 return the `AR6CooperativeGame` class
  default `MaxPlayersAllowed` (offset `0x274`), other modes the `AR6MultiPlayerGame` default.
  The script defaults are 4 and 16. Mode 8 is `AR6CoopTerroristHuntGame` (its mode getter
  `0x111a8700` returns 8); 7 is story co-op.
- **`R6CooperativeGame.CreateTeam`** calls `m_Team.Init(0, MaxPlayersAllowed)`, and
  `R6TeamInfo.AddToTeam` refuses players beyond `m_iMaxSize`. `MaxPlayersAllowed` is not a
  config property, so it cannot be raised through an ini.

## Changes

- The launcher rebuilds each copied map ini from the installed original and changes only
  those exact shipped values to 16 (`MissionPlayerLimits`). Terrorist Hunt also gets
  `m_iMaxPlayersListenServer=16`.
- The native adapter (`native_player_limit.h`) replaces the `GetMaxPlayersAllowed` vtable
  slot so mode 8 returns 16 when the original returns 4. It also hooks the
  `AR6CoopTerroristHuntGame` class constructor (`0x111a8f40`). `StaticConstructObject`
  (`0x10e02b40`) copies the class defaults in `StaticAllocateObject` before it calls the
  class constructor, so after the constructor the hook changes `MaxPlayersAllowed` from 4
  to 16. Story co-op and objective co-op keep 4. Dedicated servers are not changed.
- The launcher sets `IMAGE_FILE_LARGE_ADDRESS_AWARE` in its copy of the executable
  (`LargeAddressAware`). The flag is the only changed byte. The copy's identity checks
  compare against the shipped hash with the flag cleared. The installed game is not changed.
