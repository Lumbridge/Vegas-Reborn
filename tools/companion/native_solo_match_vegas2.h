#pragma once
// Rainbow Six Vegas 2 multiplayer matches with one player.
// Each versus game class answers CanLaunchGame (vtable +0x7a8) with a native
// predicate that requires the lobby state (vtable +0x74c) to be 2 or 4 and
// counts participants (vtable +0x7ac) whose info object (participant vtable
// +0x504) has bit 0 of +0x2f4 set:
// - 0x10ba18f0 AR6MultiPlayerGame: more than one participant.
// - 0x10ba2080 AR6DeathmatchGame: more than one participant.
// - 0x10ba2960 team modes (Team Deathmatch, Assassination, Conquest,
//   Retrieval, Attack & Defend, Team Leader): one per team.
// Deathmatch and the team modes also require four participants (and even
// teams) when flag 0x100 of +0x348 is set and global 0x12386ad4 is zero;
// that rule is kept. The co-op classes (0x10d3e5f0, 0x10d3fa70) already
// accept one participant and are not hooked.
// The hook keeps the original answer when it is true, and otherwise accepts
// one qualifying participant in the same lobby states.
#include <windows.h>
#include <cstring>
#include "MinHook.h"

namespace NativeSoloMatchVegas2 {
constexpr unsigned Preferred = 0x10900000;
constexpr unsigned Count = 3;
constexpr unsigned Predicates[Count] = {0x10ba18f0, 0x10ba2080, 0x10ba2960};
constexpr unsigned char Prologues[Count][14] = {
    {0x6a,0xff,0x64,0xa1,0,0,0,0,0x68,0x58,0xbc,0xb4,0x11,0x50},
    {0x6a,0xff,0x64,0xa1,0,0,0,0,0x68,0xd8,0xbc,0xb4,0x11,0x50},
    {0x6a,0xff,0x64,0xa1,0,0,0,0,0x68,0x88,0xbd,0xb4,0x11,0x50},
};
// The minimum comparisons: cmp edi,1 / cmp ebp,1 / cmp ecx,1.
constexpr unsigned Minimums[Count] = {0x10ba1992, 0x10ba2127, 0x10ba2a3f};
constexpr unsigned char MinimumBytes[Count][3] = {{0x83,0xff,0x01},{0x83,0xfd,0x01},{0x83,0xf9,0x01}};
constexpr unsigned ReleaseAddress = 0x10b93ae0, RankedGlobal = 0x12386ad4;
constexpr unsigned StateSlot = 0x74c, PlayersSlot = 0x7ac, InfoSlot = 0x504;
constexpr unsigned QualifyOffset = 0x2f4, FlagsOffset = 0x348, RankedFlag = 0x100;

struct Players { BYTE** data; int count; int capacity; };
using Predicate = int(__thiscall*)(void*);
using PlayersFn = void(__thiscall*)(void*,Players*);
using ReleaseFn = void(__thiscall*)(Players*);
using InfoFn = BYTE*(__thiscall*)(void*);

inline unsigned char* Base = nullptr;
inline Predicate Originals[Count]{};
inline bool Verified = false;
inline const char* Error = "notInstalled";
inline volatile LONG SoloAllowed = 0;

inline unsigned char* At(unsigned address) { return Base+address-Preferred; }
inline void* Slot(void* object, unsigned offset) { return (*reinterpret_cast<void***>(object))[offset/4]; }

inline bool Readable(const void* pointer, size_t bytes) {
    MEMORY_BASIC_INFORMATION info{};
    if (!pointer || !VirtualQuery(pointer,&info,sizeof(info)) || info.State != MEM_COMMIT ||
        (info.Protect & (PAGE_GUARD|PAGE_NOACCESS))) return false;
    auto at = reinterpret_cast<ULONG_PTR>(pointer), first = reinterpret_cast<ULONG_PTR>(info.BaseAddress);
    return at-first+bytes <= info.RegionSize;
}

inline bool Ranked(BYTE* game) {
    return (*reinterpret_cast<DWORD*>(game+FlagsOffset) & RankedFlag) && *reinterpret_cast<DWORD*>(At(RankedGlobal)) == 0;
}

inline int SoloAnswer(void* self, int original) {
    if (original || !Readable(self,FlagsOffset+4) || Ranked(static_cast<BYTE*>(self))) return original;
    const int state = reinterpret_cast<Predicate>(Slot(self,StateSlot))(self);
    if (state != 2 && state != 4) return original;
    Players players{};
    reinterpret_cast<PlayersFn>(Slot(self,PlayersSlot))(self,&players);
    bool qualifying = false;
    // An array outside the engine's bounds is not read or released here.
    if (players.count < 0 || players.count > 64 || players.capacity < players.count ||
        (players.count && !Readable(players.data,size_t(players.count)*4))) return original;
    for (int i = 0; i < players.count && !qualifying; ++i) {
        BYTE* participant = players.data[i];
        if (!Readable(participant,4)) continue;
        BYTE* info = reinterpret_cast<InfoFn>(Slot(participant,InfoSlot))(participant);
        qualifying = info && Readable(info+QualifyOffset,1) && (info[QualifyOffset] & 1);
    }
    reinterpret_cast<ReleaseFn>(At(ReleaseAddress))(&players);
    if (!qualifying) return original;
    InterlockedIncrement(&SoloAllowed);
    return 1;
}

template <unsigned Index>
int __fastcall CanLaunch(void* self, void*) { return SoloAnswer(self,Originals[Index](self)); }

inline void* const Detours[Count] = {
    reinterpret_cast<void*>(&CanLaunch<0>), reinterpret_cast<void*>(&CanLaunch<1>), reinterpret_cast<void*>(&CanLaunch<2>)};

inline bool CodeReady(unsigned char* base) {
    Base = base;
    for (unsigned i = 0; i < Count; ++i) {
        if (memcmp(At(Predicates[i]),Prologues[i],sizeof(Prologues[i])) ||
            memcmp(At(Minimums[i]),MinimumBytes[i],sizeof(MinimumBytes[i]))) return false;
    }
    return true;
}

inline void Remove(unsigned count) {
    for (unsigned i = 0; i < count; ++i) { MH_DisableHook(At(Predicates[i])); MH_RemoveHook(At(Predicates[i])); }
}

inline bool Install(unsigned char* base) {
    if (!CodeReady(base)) { Error = "codeMismatch"; return false; }
    for (unsigned i = 0; i < Count; ++i) {
        if (MH_CreateHook(At(Predicates[i]),Detours[i],reinterpret_cast<void**>(&Originals[i])) != MH_OK) {
            Remove(i); Error = "hookCreationFailed"; return false;
        }
        if (MH_EnableHook(At(Predicates[i])) != MH_OK) {
            Remove(i+1); Error = "hookEnableFailed"; return false;
        }
    }
    Verified = true;
    Error = "ok";
    return true;
}
}
