#pragma once
// Rainbow Six Vegas 2 online XP modifier.
// When the game is online (network mode 4), XP is multiplied by two factors,
// used by the XP award (0x10d432e3, 0x10d43316) and the lobby's
// "XP MODIFIER: %s" line (0x1149b370):
// - 0x10d43150(int players): a player-count table from the R6Game defaults.
// - 0x10d429e0(int teamA, int teamB): 0.9 down to 0.1 for team differences
//   of one to seven, 0.01 beyond.
// The lobby clamps the product to at least 0.01 and shows (product - 1) * 100
// percent only when it is below 1.0. Both factors return 1.0 here, so XP is
// awarded in full and the lobby line stays hidden.
#include <windows.h>
#include <cstring>
#include "MinHook.h"

namespace NativeXpModifierVegas2 {
constexpr unsigned Preferred = 0x10900000;
constexpr unsigned PlayerFactor = 0x10d43150, TeamFactor = 0x10d429e0;
constexpr unsigned char PlayerPrologue[] = {0x83,0x3d,0xf8,0x59,0x37,0x12,0x00,0x75,0x17,0x68,0x34,0x58,0xc9,0x11};
constexpr unsigned char TeamPrologue[] = {0x8b,0x0d,0x3c,0x8a,0x4a,0x12,0x8b,0x01,0x8b,0x90,0x7c,0x02,0x00,0x00};

inline unsigned char* Base = nullptr;
inline void* OriginalPlayer = nullptr;
inline void* OriginalTeam = nullptr;
inline bool Verified = false;
inline const char* Error = "notInstalled";

inline unsigned char* At(unsigned address) { return Base+address-Preferred; }

inline float __cdecl FullForPlayers(int) { return 1.0f; }
inline float __cdecl FullForTeams(int, int) { return 1.0f; }

inline bool CodeReady(unsigned char* base) {
    Base = base;
    return !memcmp(At(PlayerFactor),PlayerPrologue,sizeof(PlayerPrologue)) &&
           !memcmp(At(TeamFactor),TeamPrologue,sizeof(TeamPrologue));
}

inline bool Install(unsigned char* base) {
    if (!CodeReady(base)) { Error = "codeMismatch"; return false; }
    if (MH_CreateHook(At(PlayerFactor),reinterpret_cast<void*>(FullForPlayers),&OriginalPlayer) != MH_OK) {
        Error = "hookCreationFailed"; return false;
    }
    if (MH_CreateHook(At(TeamFactor),reinterpret_cast<void*>(FullForTeams),&OriginalTeam) != MH_OK) {
        MH_RemoveHook(At(PlayerFactor)); Error = "hookCreationFailed"; return false;
    }
    if (MH_EnableHook(At(PlayerFactor)) != MH_OK || MH_EnableHook(At(TeamFactor)) != MH_OK) {
        MH_DisableHook(At(PlayerFactor)); MH_DisableHook(At(TeamFactor));
        MH_RemoveHook(At(PlayerFactor)); MH_RemoveHook(At(TeamFactor));
        Error = "hookEnableFailed"; return false;
    }
    Verified = true;
    Error = "ok";
    return true;
}
}
