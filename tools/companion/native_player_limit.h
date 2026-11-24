#pragma once

// Sixteen-player Coop Terrorist Hunt for listen-server hosts.
//
// The original caps Coop Terrorist Hunt at four players in two places:
//  - UR6GameManager::GetMaxPlayersAllowed(mode) returns the AR6CooperativeGame
//    class default MaxPlayersAllowed (4) for the coop modes 7, 8 and 18;
//  - R6CooperativeGame.CreateTeam passes the game's MaxPlayersAllowed (4) to
//    R6TeamInfo.Init, whose AddToTeam refuses players beyond m_iMaxSize.
// Mode 8 is Coop Terrorist Hunt (AR6CoopTerroristHuntGame's constant mode getter).
// Story coop (7) and objective (18) keep the original values. The per-map menu and
// session caps come from the copy's mission ini files (GamePreparer).
#include <windows.h>
#include <cstdint>
#include <cstring>
#include "MinHook.h"

namespace NativePlayerLimit {
static_assert(sizeof(void*)==4,"Original retail images are x86");
static constexpr int OriginalCoop=4,Raised=16,TerroristHunt=8;
static constexpr unsigned MaxPlayersRva=0x86fd10;     // UR6GameManager::GetMaxPlayersAllowed
static constexpr unsigned ManagerSlotRva=0x174b7a0;   // its UR6GameManager vtable slot (+0x100)
static constexpr unsigned ConstructorRva=0x8a8f40;    // AR6CoopTerroristHuntGame InternalConstructor
static constexpr unsigned HuntVtableRva=0x1754678;    // AR6CoopTerroristHuntGame vtable
static constexpr unsigned HuntModeRva=0x8a8700;       // its GetGameMode: mov eax,8; ret
static constexpr unsigned MaxPlayersAllowed=0x274;    // AGameInfo::MaxPlayersAllowed
static BYTE* Image=nullptr;
static bool Installed=false,Verified=false;
static volatile LONG ErrorCode=0,Raises=0;
using MaxFn=int(__thiscall*)(void*,unsigned);
using ConstructFn=void(__cdecl*)(void*);
static MaxFn OriginalMax=nullptr;
static ConstructFn OriginalConstruct=nullptr;
static const char* LastError(){
    switch(InterlockedCompareExchange(&ErrorCode,0,0)){
        case 0:return "none";
        case 1:return "imageIdentityMismatch";
        case 2:return "nativeSignatureMismatch";
        case 3:return "hookCreationFailed";
        case 4:return "hookActivationFailed";
        case 5:return "nativeTableWriteFailed";
        default:return "unknown";
    }
}
static bool Readable(const void* pointer,size_t bytes){
    MEMORY_BASIC_INFORMATION info{};
    if(!pointer||!bytes||!VirtualQuery(pointer,&info,sizeof(info))||info.State!=MEM_COMMIT||
       (info.Protect&(PAGE_GUARD|PAGE_NOACCESS)))return false;
    auto at=reinterpret_cast<uintptr_t>(pointer),first=reinterpret_cast<uintptr_t>(info.BaseAddress);
    return at>=first&&at-first<=info.RegionSize&&bytes<=info.RegionSize-(at-first);
}
static BYTE* Pointer(const void* at){return Readable(at,4)?*reinterpret_cast<BYTE* const*>(at):nullptr;}
static int __fastcall MaxPlayers(void* manager,void*,unsigned mode){
    const int original=OriginalMax(manager,mode);
    // Only the exact original coop default for Terrorist Hunt is raised.
    return BYTE(mode)==TerroristHunt&&original==OriginalCoop?Raised:original;
}
static void __cdecl Construct(void* object){
    OriginalConstruct(object);
    // Script properties are copied from the class defaults before the native
    // class constructor runs (UObject::StaticConstructObject), so this value is
    // the inherited R6CooperativeGame default. Change only that exact value.
    auto game=static_cast<BYTE*>(object);
    if(!game||!Readable(game,MaxPlayersAllowed+4)||Pointer(game)!=Image+HuntVtableRva)return;
    auto value=reinterpret_cast<volatile LONG*>(game+MaxPlayersAllowed);
    if(InterlockedCompareExchange(value,Raised,OriginalCoop)==OriginalCoop)InterlockedIncrement(&Raises);
}
static bool RelativeTo(const BYTE* at,BYTE opcode,const void* target){
    if(!Readable(at,5)||at[0]!=opcode)return false;
    LONG relative=0;memcpy(&relative,at+1,4);return at+5+relative==target;
}
static bool WriteSlot(BYTE* slot,const void* value){
    DWORD protection=0;
    if(!VirtualProtect(slot,4,PAGE_READWRITE,&protection))return false;
    InterlockedExchange(reinterpret_cast<volatile LONG*>(slot),LONG(reinterpret_cast<uintptr_t>(value)));
    DWORD ignored=0;
    return VirtualProtect(slot,4,protection,&ignored)!=FALSE;
}
static bool Install(BYTE* base){
    if(Installed)return Verified;
    if(!Readable(base,0x1000)){InterlockedExchange(&ErrorCode,1);return false;}
    auto dos=reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if(dos->e_magic!=IMAGE_DOS_SIGNATURE||dos->e_lfanew<=0||dos->e_lfanew>0x800){InterlockedExchange(&ErrorCode,1);return false;}
    auto pe=reinterpret_cast<IMAGE_NT_HEADERS32*>(base+dos->e_lfanew);
    if(pe->Signature!=IMAGE_NT_SIGNATURE||pe->FileHeader.Machine!=IMAGE_FILE_MACHINE_I386||
       pe->OptionalHeader.ImageBase!=0x10900000u||pe->OptionalHeader.SizeOfImage!=0x1f56000u||
       pe->FileHeader.TimeDateStamp!=0x47106421u){
        InterlockedExchange(&ErrorCode,1);return false;
    }
    Image=base;
    // GetMaxPlayersAllowed: IsMultiplayer; mode 7..8 or 18 selects the coop class.
    const BYTE maxEntry[]{0x8b,0x01,0xff,0x90,0x28,0x01,0,0,0x85,0xc0,0x74,0x72,0x8a,0x44,0x24,0x04,
                          0x3c,0x07,0x72,0x30,0x3c,0x08,0x76,0x04,0x3c,0x12,0x75,0x28};
    // Default object read and return: MOV EAX,[EAX+0x274]; RET 4.
    const BYTE maxRead[]{0x8b,0x80,0x74,0x02,0,0,0xc2,0x04,0};
    // InternalConstructor: MOV ECX,[ESP+4]; TEST ECX,ECX; JZ; JMP constructor.
    const BYTE construct[]{0x8b,0x4c,0x24,0x04,0x85,0xc9,0x74,0x05};
    const BYTE huntMode[]{0xb8,0x08,0,0,0,0xc3};
    BYTE* slot=base+ManagerSlotRva;
    if(!Readable(base+MaxPlayersRva,0x90)||memcmp(base+MaxPlayersRva,maxEntry,sizeof(maxEntry))||
       memcmp(base+MaxPlayersRva+0x75,maxRead,sizeof(maxRead))||Pointer(slot)!=base+MaxPlayersRva||
       !Readable(base+ConstructorRva,14)||memcmp(base+ConstructorRva,construct,sizeof(construct))||
       !RelativeTo(base+ConstructorRva+8,0xe9,base+0x8a86e0)||
       !Readable(base+HuntModeRva,sizeof(huntMode))||memcmp(base+HuntModeRva,huntMode,sizeof(huntMode))||
       Pointer(base+HuntVtableRva+0x5e0)!=base+HuntModeRva){
        InterlockedExchange(&ErrorCode,2);return false;
    }
    if(MH_CreateHook(base+ConstructorRva,reinterpret_cast<void*>(Construct),reinterpret_cast<void**>(&OriginalConstruct))!=MH_OK){
        InterlockedExchange(&ErrorCode,3);return false;
    }
    if(MH_EnableHook(base+ConstructorRva)!=MH_OK||!RelativeTo(base+ConstructorRva,0xe9,reinterpret_cast<void*>(Construct))){
        MH_DisableHook(base+ConstructorRva);MH_RemoveHook(base+ConstructorRva);
        InterlockedExchange(&ErrorCode,4);return false;
    }
    // The manager reaches GetMaxPlayersAllowed only through this vtable slot.
    OriginalMax=reinterpret_cast<MaxFn>(base+MaxPlayersRva);
    if(!WriteSlot(slot,reinterpret_cast<void*>(MaxPlayers))||Pointer(slot)!=reinterpret_cast<BYTE*>(MaxPlayers)){
        WriteSlot(slot,base+MaxPlayersRva);
        MH_DisableHook(base+ConstructorRva);MH_RemoveHook(base+ConstructorRva);
        InterlockedExchange(&ErrorCode,5);return false;
    }
    Verified=true;Installed=true;
    return true;
}
} // namespace NativePlayerLimit
