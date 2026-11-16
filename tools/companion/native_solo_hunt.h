#pragma once

// The original Coop Terrorist Hunt class inherits the two-participant native
// CanLaunchGame gate. Permit one original qualifying participant in mode 8.
// Original lobby state, ready checks, authority, launch and player lists stay native.
#include <windows.h>
#include <cstdint>
#include <cstring>
#include "MinHook.h"

namespace NativeSoloHunt {
static_assert(sizeof(void*)==4,"Original retail images are x86");
static BYTE* Image=nullptr;
static bool Installed=false,Verified=false;
static volatile LONG SoloAllowed=0,ErrorCode=0;
static unsigned PredicateRva=0,ClassRva=0,StateRva=0,ModeRva=0,PlayersRva=0,ReleaseRva=0,ImageSize=0;
struct Players {BYTE** data;int count;int capacity;};
using PredicateFn=int(__thiscall*)(void*);
using PlayersFn=void(__thiscall*)(void*,Players*);
using ReleaseFn=void(__thiscall*)(Players*);
using InfoFn=BYTE*(__thiscall*)(void*);
static PredicateFn Original=nullptr;
static const char* LastError(){
    switch(InterlockedCompareExchange(&ErrorCode,0,0)){
        case 0:return "none";
        case 1:return "imageIdentityMismatch";
        case 2:return "nativeSignatureMismatch";
        case 3:return "hookCreationFailed";
        case 4:return "hookActivationFailed";
        case 5:return "nativePlayersUnavailable";
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
static bool InImage(const void* pointer,size_t bytes){
    auto at=reinterpret_cast<uintptr_t>(pointer),base=reinterpret_cast<uintptr_t>(Image);
    return at>=base&&at-base<=ImageSize&&bytes<=ImageSize-(at-base)&&Readable(pointer,bytes);
}
static BYTE* Pointer(const void* at){return Readable(at,4)?*reinterpret_cast<BYTE* const*>(at):nullptr;}
static bool NativeCode(const void* pointer){
    if(!InImage(pointer,1))return false;
    MEMORY_BASIC_INFORMATION info{};
    return VirtualQuery(pointer,&info,sizeof(info))&&
        (info.Protect&(PAGE_EXECUTE|PAGE_EXECUTE_READ|PAGE_EXECUTE_READWRITE|PAGE_EXECUTE_WRITECOPY));
}
static bool RelativeTo(BYTE* at,BYTE opcode,const void* target){
    if(!Readable(at,5)||at[0]!=opcode)return false;
    LONG relative=0;memcpy(&relative,at+1,4);return at+5+relative==target;
}
static int __fastcall CanLaunch(void* self,void*){
    const int original=Original(self);
    if(original)return original;
    // Exact native class plus its constant mode getter. Story, solo campaign,
    // every PvP class and unknown future subclasses retain the original gate.
    if(!Readable(self,0x300)||Pointer(self)!=Image+ClassRva)return original;
    auto table=Image+ClassRva;
    if(Pointer(table+0x5dc)!=Image+StateRva||Pointer(table+0x5e0)!=Image+ModeRva||
       Pointer(table+0x624)!=Image+PredicateRva||Pointer(table+0x628)!=Image+PlayersRva)return original;
    if(reinterpret_cast<PredicateFn>(Image+ModeRva)(self)!=8)return original;
    const int state=reinterpret_cast<PredicateFn>(Image+StateRva)(self);
    if(state!=2&&state!=4)return original;
    Players players{};
    reinterpret_cast<PlayersFn>(Image+PlayersRva)(self,&players);
    const bool arrayValid=players.count>=0&&players.count<=16&&players.capacity>=players.count&&players.capacity<=128&&
        (players.capacity?Readable(players.data,size_t(players.capacity)*4):players.data==nullptr);
    // Do not pass an untrusted array back to a native destructor. A malformed
    // result is outside the pinned provider contract and keeps the original false.
    if(!arrayValid){InterlockedExchange(&ErrorCode,5);return original;}
    bool valid=true;
    bool qualifying=false;
    if(valid){
        for(int i=0;i<players.count;++i){
            BYTE* participant=players.data[i];
            BYTE* participantTable=Pointer(participant);
            if(!InImage(participantTable,0x490)){valid=false;break;}
            BYTE* getter=Pointer(participantTable+0x48c);
            if(!NativeCode(getter)){valid=false;break;}
            BYTE* info=reinterpret_cast<InfoFn>(getter)(participant);
            if(!info)continue;
            if(!Readable(info+0x2d8,1)){valid=false;break;}
            // This is the SAME original participant qualifier, not a guessed
            // ready flag. Original start evaluator checks bReadyToPlay later.
            if(info[0x2d8]&1)qualifying=true;
        }
    }
    // Dispose of the returned array storage through its original destructor.
    reinterpret_cast<ReleaseFn>(Image+ReleaseRva)(&players);
    if(!valid){InterlockedExchange(&ErrorCode,5);return original;}
    if(qualifying){InterlockedIncrement(&SoloAllowed);return 1;}
    return original;
}
static bool Install(BYTE* base,bool dedicated){
    if(Installed)return Verified;
    if(!Readable(base,0x1000)){InterlockedExchange(&ErrorCode,1);return false;}
    auto dos=reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if(dos->e_magic!=IMAGE_DOS_SIGNATURE||dos->e_lfanew<=0||dos->e_lfanew>0x800){InterlockedExchange(&ErrorCode,1);return false;}
    auto pe=reinterpret_cast<IMAGE_NT_HEADERS32*>(base+dos->e_lfanew);
    ImageSize=dedicated?0x1f53000u:0x1f56000u;
    if(pe->Signature!=IMAGE_NT_SIGNATURE||pe->FileHeader.Machine!=IMAGE_FILE_MACHINE_I386||
       pe->OptionalHeader.ImageBase!=0x10900000u||pe->OptionalHeader.SizeOfImage!=ImageSize||
       pe->FileHeader.TimeDateStamp!=(dedicated?0x473ebf76u:0x47106421u)){
        InterlockedExchange(&ErrorCode,1);return false;
    }
    Image=base;
    PredicateRva=dedicated?0x8a6130u:0x8a6fc0u;
    ClassRva=dedicated?0x1753278u:0x1754678u;
    StateRva=dedicated?0x8a1040u:0x8a1ed0u;
    ModeRva=dedicated?0x8a7870u:0x8a8700u;
    PlayersRva=dedicated?0x8a1180u:0x8a2010u;
    ReleaseRva=dedicated?0x81b30u:0x832f0u;
    BYTE entry[]{0x6a,0xff,0x64,0xa1,0,0,0,0,0x68,0,0,0,0,0x50,0x64,0x89,0x25,0,0,0,0,0x83,0xec,0x0c};
    DWORD handler=DWORD(reinterpret_cast<uintptr_t>(base+(dedicated?0x14e2cd8u:0x14e3998u)));
    memcpy(entry+9,&handler,4);
    const BYTE minimum[]{0x3b,0xdd,0x74,0x0c,0x83,0xff,0x01,0x7e,0x07,0xbe,0x01,0,0,0,0xeb,0x02,0x33,0xf6};
    const BYTE mode[]{0xb8,0x08,0,0,0,0xc3};
    const BYTE helper[]{0x83,0xb9,0xfc,0x02,0,0,0};
    if(!InImage(base+ClassRva,0x62c)||!Readable(base+PredicateRva,0xd0)||
       !Readable(base+ModeRva,sizeof(mode))||!Readable(base+StateRva,sizeof(helper))||
       !Readable(base+PlayersRva,sizeof(helper))||memcmp(base+PredicateRva,entry,sizeof(entry))||
       memcmp(base+PredicateRva+0x98,minimum,sizeof(minimum))||memcmp(base+ModeRva,mode,sizeof(mode))||
       memcmp(base+StateRva,helper,sizeof(helper))||memcmp(base+PlayersRva,helper,sizeof(helper))||
       !RelativeTo(base+PredicateRva+0xb6,0xe8,base+ReleaseRva)||
       Pointer(base+ClassRva+0x5dc)!=base+StateRva||Pointer(base+ClassRva+0x5e0)!=base+ModeRva||
       Pointer(base+ClassRva+0x624)!=base+PredicateRva||Pointer(base+ClassRva+0x628)!=base+PlayersRva){
        InterlockedExchange(&ErrorCode,2);return false;
    }
    if(MH_CreateHook(base+PredicateRva,reinterpret_cast<void*>(CanLaunch),reinterpret_cast<void**>(&Original))!=MH_OK){
        InterlockedExchange(&ErrorCode,3);return false;
    }
    if(MH_EnableHook(base+PredicateRva)!=MH_OK){
        MH_RemoveHook(base+PredicateRva);InterlockedExchange(&ErrorCode,4);return false;
    }
    Verified=RelativeTo(base+PredicateRva,0xe9,reinterpret_cast<void*>(CanLaunch));
    if(!Verified){
        MH_DisableHook(base+PredicateRva);MH_RemoveHook(base+PredicateRva);
        InterlockedExchange(&ErrorCode,4);return false;
    }
    Installed=true;
    return true;
}
} // namespace NativeSoloHunt
