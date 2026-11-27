#pragma once

// Rainbow Six Vegas 2 sixteen-player Terrorist Hunt for listen-server hosts.
// Same limits as Vegas 1 (docs/NATIVE_PLAYER_LIMITS.md); Vegas 2 addresses and layout are
// read from the unpacked R6Vegas2_Game.exe (docs/NATIVE_PLAYER_LIMITS_VEGAS2.md):
//  - UR6GameManager::GetMaxPlayersAllowed(mode) 0x10c85070 (UR6GameManager vtable
//    0x11d73cd4 +0x100, called through it by execGetMaxPlayersAllowed 0x110ea050).
//    In multiplayer, mode 7 returns the AR6CoopStoryGame class default MaxPlayersAllowed
//    (+0x28c), mode 8 the AR6CooperativeGame default (4 in R6Game.uppc) and other modes
//    the AR6MultiPlayerGame default (16).
//  - Mode 8 is Terrorist Hunt: AR6CoopTerroristHuntGame (vtable 0x11db2d34) answers
//    GetGameMode (+0x750) with 0x10d40b20: mov eax,8; ret.
//  - AR6CooperativeGame's CreateTeam (vtable +0x834, 0x10d3edd0) passes the game's
//    MaxPlayersAllowed (+0x28c) to the team's Init as its size.
// AR6CoopTerroristHuntGame's InternalConstructor (0x10d40bc0, registered with the
// class at 0x10d40c32) runs after StaticConstructObject has copied the class defaults,
// so the inherited 4 is raised to 16 there. Story co-op keeps the original values.
// The per-map menu and session caps come from the copy's mission ini files.
#include <windows.h>
#include <cstring>
#include "MinHook.h"

namespace NativePlayerLimitVegas2 {
static_assert(sizeof(void*)==4,"Original retail images are x86");
constexpr unsigned Preferred=0x10900000;
constexpr int OriginalCoop=4,Raised=16,TerroristHunt=8;
constexpr unsigned MaxPlayersAddress=0x10c85070,ManagerSlot=0x11d73dd4;
constexpr unsigned ConstructorAddress=0x10d40bc0,ConstructAddress=0x10faf2d0;
constexpr unsigned HuntVtable=0x11db2d34,HuntModeAddress=0x10d40b20,ModeSlot=0x750;
constexpr unsigned CreateTeamAddress=0x10d3edd0,CreateTeamSlot=0x834,CreateTeamRead=0x10d3ef0e;
constexpr unsigned MaxPlayersAllowed=0x28c;

inline unsigned char* Base=nullptr;
inline bool Verified=false;
inline const char* Error="notInstalled";
inline volatile LONG Raises=0;
using MaxFn=int(__thiscall*)(void*,unsigned);
using ConstructFn=void(__cdecl*)(void*);
inline MaxFn OriginalMax=nullptr;
inline ConstructFn OriginalConstruct=nullptr;

inline unsigned char* At(unsigned address){return Base+(address-Preferred);}
inline unsigned Relocated(unsigned address){return unsigned(reinterpret_cast<uintptr_t>(At(address)));}
inline bool Readable(const void* pointer,size_t bytes){
    MEMORY_BASIC_INFORMATION info{};
    if(!pointer||!bytes||!VirtualQuery(pointer,&info,sizeof(info))||info.State!=MEM_COMMIT||
       (info.Protect&(PAGE_GUARD|PAGE_NOACCESS)))return false;
    auto at=reinterpret_cast<uintptr_t>(pointer),first=reinterpret_cast<uintptr_t>(info.BaseAddress);
    return at>=first&&at-first<=info.RegionSize&&bytes<=info.RegionSize-(at-first);
}
inline BYTE* Pointer(const void* at){return Readable(at,4)?*reinterpret_cast<BYTE* const*>(at):nullptr;}
inline bool Matches(const void* at,const void* code,size_t size){return Readable(at,size)&&!memcmp(at,code,size);}

inline int __fastcall MaxPlayers(void* manager,void*,unsigned mode){
    const int original=OriginalMax(manager,mode);
    // Only the exact original coop default for Terrorist Hunt is raised.
    return BYTE(mode)==TerroristHunt&&original==OriginalCoop?Raised:original;
}
inline void __cdecl Construct(void* object){
    OriginalConstruct(object);
    auto game=static_cast<BYTE*>(object);
    if(!game||!Readable(game,MaxPlayersAllowed+4)||Pointer(game)!=At(HuntVtable))return;
    auto value=reinterpret_cast<volatile LONG*>(game+MaxPlayersAllowed);
    if(InterlockedCompareExchange(value,Raised,OriginalCoop)==OriginalCoop)InterlockedIncrement(&Raises);
}

inline bool ImageMatches(unsigned char* base){
    if(!Readable(base,0x1000))return false;
    auto dos=reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if(dos->e_magic!=IMAGE_DOS_SIGNATURE||dos->e_lfanew<=0||dos->e_lfanew>0x800)return false;
    auto pe=reinterpret_cast<IMAGE_NT_HEADERS32*>(base+dos->e_lfanew);
    return pe->Signature==IMAGE_NT_SIGNATURE&&pe->FileHeader.Machine==IMAGE_FILE_MACHINE_I386&&
        pe->FileHeader.TimeDateStamp==0x484efa91u&&pe->OptionalHeader.SizeOfImage==0x1edb000u&&
        pe->OptionalHeader.ImageBase==Preferred;
}
inline bool RelativeTo(const BYTE* at,BYTE opcode,const void* target){
    if(!Readable(at,5)||at[0]!=opcode)return false;
    LONG relative=0;memcpy(&relative,at+1,4);return at+5+relative==target;
}
inline bool WriteSlot(BYTE* slot,const void* value){
    DWORD protection=0;
    if(!VirtualProtect(slot,4,PAGE_READWRITE,&protection))return false;
    InterlockedExchange(reinterpret_cast<volatile LONG*>(slot),LONG(reinterpret_cast<uintptr_t>(value)));
    DWORD ignored=0;
    return VirtualProtect(slot,4,protection,&ignored)!=FALSE;
}

// GetMaxPlayersAllowed: IsMultiplayer (vtable +0x138); mode 7 and mode 8 branches.
constexpr unsigned char MaxEntry[]{0x8b,0x01,0x8b,0x90,0x38,0x01,0,0,0xff,0xd2,0x85,0xc0,0x0f,0x84,0xab,0,0,0,
    0x0f,0xb6,0x44,0x24,0x04,0x83,0xe8,0x07,0x74,0x70,0x83,0xe8,0x01,0x74,0x3a};
// Default object read and return: MOV EAX,[EAX+0x28c]; RET 4.
constexpr unsigned char MaxRead[]{0x8b,0x80,0x8c,0x02,0,0,0xc2,0x04,0};
// InternalConstructor: PUSH ESI; MOV ESI,[ESP+8]; TEST ESI,ESI; JZ; MOV ECX,ESI; CALL constructor.
constexpr unsigned char ConstructorEntry[]{0x56,0x8b,0x74,0x24,0x08,0x85,0xf6,0x74,0x0d,0x8b,0xce};
constexpr unsigned char HuntMode[]{0xb8,0x08,0,0,0,0xc3};
// CreateTeam: MOV EAX,[EBX+0x28c] (team size).
constexpr unsigned char TeamSize[]{0x8b,0x83,0x8c,0x02,0,0};

inline bool Install(unsigned char* base){
    if(Verified)return true;
    if(!ImageMatches(base)){Error="unsupportedImage";return false;}
    Base=base;
    BYTE* slot=At(ManagerSlot);
    unsigned char vtableStore[6]{0xc7,0x06};
    const unsigned vtable=Relocated(HuntVtable);
    memcpy(vtableStore+2,&vtable,4);
    if(!Matches(At(MaxPlayersAddress),MaxEntry,sizeof(MaxEntry))||
       !Matches(At(MaxPlayersAddress+0x52),MaxRead,sizeof(MaxRead))||Pointer(slot)!=At(MaxPlayersAddress)||
       !Matches(At(ConstructorAddress),ConstructorEntry,sizeof(ConstructorEntry))||
       !RelativeTo(At(ConstructorAddress+11),0xe8,At(ConstructAddress))||
       !Matches(At(ConstructorAddress+16),vtableStore,sizeof(vtableStore))||
       !Matches(At(HuntModeAddress),HuntMode,sizeof(HuntMode))||
       Pointer(At(HuntVtable+ModeSlot))!=At(HuntModeAddress)||
       Pointer(At(HuntVtable+CreateTeamSlot))!=At(CreateTeamAddress)||
       !Matches(At(CreateTeamRead),TeamSize,sizeof(TeamSize))){
        Error="codeMismatch";return false;
    }
    if(MH_CreateHook(At(ConstructorAddress),reinterpret_cast<void*>(Construct),reinterpret_cast<void**>(&OriginalConstruct))!=MH_OK){
        Error="hookCreationFailed";return false;
    }
    if(MH_EnableHook(At(ConstructorAddress))!=MH_OK){
        MH_RemoveHook(At(ConstructorAddress));
        Error="hookActivationFailed";return false;
    }
    // The manager reaches GetMaxPlayersAllowed only through this vtable slot.
    OriginalMax=reinterpret_cast<MaxFn>(At(MaxPlayersAddress));
    if(!WriteSlot(slot,reinterpret_cast<void*>(MaxPlayers))||Pointer(slot)!=reinterpret_cast<BYTE*>(MaxPlayers)){
        WriteSlot(slot,At(MaxPlayersAddress));
        MH_DisableHook(At(ConstructorAddress));MH_RemoveHook(At(ConstructorAddress));
        Error="nativeTableWriteFailed";return false;
    }
    Verified=true;Error="ok";
    return true;
}
} // namespace NativePlayerLimitVegas2
