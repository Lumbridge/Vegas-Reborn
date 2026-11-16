#pragma once

// Original PC client UI adapter. Include in the existing x86 companion TU.
// Call Install only after the original plaintext and raw-XP bridge are ready.
// The two UI-thread hooks append a native PLAYER INFO entry and use the
// original Trophy/Service Record panels. No overlay, packet or rank data edit.
#include <windows.h>
#include <cstring>
#include <cstdint>
#include <initializer_list>
#include "MinHook.h"

namespace NativeRank {
static_assert(sizeof(void*)==4,"Original retail client is x86");
static BYTE* Image=nullptr;
static bool Installed=false,Verified=false;
static bool RemotePipelineRecognized=false;
static volatile LONG RowsAdded=0,Opened=0,ErrorCode=0;
static constexpr uintptr_t Preferred=0x10900000;
static constexpr unsigned PopulateRva=0x9d170,SelectionRva=0x9d350;
struct FString {const wchar_t* data;int length;int capacity;};
using PopulateFn=void(__thiscall*)(void*);
using SelectionFn=bool(__thiscall*)(void*,void*);
using AppendFn=void(__thiscall*)(void*,const wchar_t*,int);
using LocalizeFn=FString*(__cdecl*)(FString*,const wchar_t*,const wchar_t*,const wchar_t*,int,int);
using ReleaseFn=void(__thiscall*)(void*);
using ShowFn=void(__thiscall*)(void*,int);
static PopulateFn OriginalPopulate=nullptr;
static SelectionFn OriginalSelection=nullptr;
static AppendFn Append=nullptr;
static LocalizeFn Localize=nullptr;
static ReleaseFn Release=nullptr;
static uint32_t SelectionId=0;

static const char* LastError(){
    switch(InterlockedCompareExchange(&ErrorCode,0,0)){
        case 0:return "none";
        case 1:return "clientIdentityMismatch";
        case 2:return "nativeSignatureMismatch";
        case 3:return "rawXpBridgeMissing";
        case 4:return "hookCreationFailed";
        case 5:return "hookActivationFailed";
        case 6:return "menuUnavailable";
        case 7:return "originalPlayerInfoUnavailable";
        default:return "unknown";
    }
}
static bool Readable(const void* address,size_t bytes){
    MEMORY_BASIC_INFORMATION info{};
    if(!address||!bytes||!VirtualQuery(address,&info,sizeof(info))||info.State!=MEM_COMMIT||
       (info.Protect&(PAGE_NOACCESS|PAGE_GUARD)))return false;
    uintptr_t first=reinterpret_cast<uintptr_t>(address),region=reinterpret_cast<uintptr_t>(info.BaseAddress);
    return first>=region&&first-region<=info.RegionSize&&bytes<=info.RegionSize-(first-region);
}
static BYTE* Pointer(const void* at){return Readable(at,4)?*reinterpret_cast<BYTE* const*>(at):nullptr;}
static bool HasTable(BYTE* object,unsigned rva){return Readable(object,4)&&Pointer(object)==Image+rva;}
static uint32_t NameId(const char* name){
    // Native 115e9180's exact ASCII UI-name CRC, unrelated to PEC checksums.
    uint32_t value=0xffffffffu;
    for(;*name;++name){value^=static_cast<unsigned char>(*name);for(unsigned n=0;n<8;++n)value=(value>>1)^((value&1)?0xedb88320u:0u);}
    return ~value;
}
static BYTE* TrophyFor(BYTE* controller){
    // PC controller1096c510 stores Trophy+e4; generic1096f110 stores it+84.
    // Require the exact original object and original parent on either path.
    for(unsigned offset:{0xe4u,0x84u}){
        BYTE* trophy=Pointer(controller?controller+offset:nullptr);
        if(!HasTable(trophy,0x15bb07c)||!Readable(trophy+0xc,4)||
           !*reinterpret_cast<DWORD*>(trophy+0xc)||Pointer(trophy+4)!=controller)continue;
        BYTE* record=trophy+0x360;
        if(HasTable(record,0x15c3434)&&Readable(record+0xc,4)&&
           *reinterpret_cast<DWORD*>(record+0xc)&&Pointer(record+4)==controller)return trophy;
    }
    return nullptr;
}
static void __fastcall Populate(void* self,void*){
    OriginalPopulate(self);
    BYTE* owner=static_cast<BYTE*>(self);
    if(!HasTable(owner,0x15bb5bc)||!Readable(owner+0xe4,4))return;
    BYTE* list=Pointer(owner+0xe4);
    if(!Readable(list,0xae)){InterlockedExchange(&ErrorCode,6);return;}
    FString label{};
    Localize(&label,L"Trophy",L"TrophyTitle",L"R6Menus",0,0);
    if(label.data&&label.length>1&&label.length<256){
        // Native initializer clears then appends its six original rows. The
        // append routine deep-copies before the original string destructor.
        Append(list,label.data,0);InterlockedIncrement(&RowsAdded);
    }
    Release(&label);
}
static bool __fastcall Selection(void* subobject,void*,void* event){
    BYTE* input=static_cast<BYTE*>(subobject);
    BYTE* owner=input?input-0x20:nullptr;
    if(HasTable(owner,0x15bb5bc)&&Readable(event,8)&&
       *reinterpret_cast<uint32_t*>(static_cast<BYTE*>(event)+4)==SelectionId){
        BYTE* list=Pointer(owner+0xe4);
        if(Readable(list,0xae)&&*reinterpret_cast<short*>(list+0xac)==6){
            BYTE* trophy=TrophyFor(Pointer(owner+4));
            if(trophy){
                // Exact original virtual Show(1) path used for modal panels.
                // Its native callback selects the original PEC/avatar context;
                // original input, dismissal and Service Record row stay native.
                auto show=reinterpret_cast<ShowFn>(Image+0xc19a0);
                show(trophy,1);InterlockedIncrement(&Opened);return true;
            }
            InterlockedExchange(&ErrorCode,7);return true;
        }
    }
    return OriginalSelection(subobject,event);
}
static bool IsJumpTo(BYTE* address,const void* target){
    if(address[0]!=0xe9)return false;
    LONG displacement=0;memcpy(&displacement,address+1,4);
    return address+5+displacement==target;
}
static bool IsCallTo(BYTE* address,const void* target){
    if(address[0]!=0xe8)return false;
    LONG displacement=0;memcpy(&displacement,address+1,4);
    return address+5+displacement==target;
}
static bool Install(BYTE* base){
    if(Installed)return Verified;
    if(!Readable(base,0x1000)){InterlockedExchange(&ErrorCode,1);return false;}
    auto dos=reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if(dos->e_magic!=IMAGE_DOS_SIGNATURE||dos->e_lfanew<=0||dos->e_lfanew>0x800){InterlockedExchange(&ErrorCode,1);return false;}
    auto pe=reinterpret_cast<IMAGE_NT_HEADERS32*>(base+dos->e_lfanew);
    if(pe->Signature!=IMAGE_NT_SIGNATURE||pe->FileHeader.Machine!=IMAGE_FILE_MACHINE_I386||
       pe->FileHeader.TimeDateStamp!=0x47106421||pe->OptionalHeader.ImageBase!=Preferred||
       pe->OptionalHeader.SizeOfImage!=0x1f56000){InterlockedExchange(&ErrorCode,1);return false;}
    Image=base;
    BYTE populate[]{0x64,0xa1,0,0,0,0,0x6a,0xff,0x68,0,0,0,0};
    BYTE selection[]{0x64,0xa1,0,0,0,0,0x6a,0xff,0x68,0,0,0,0};
    DWORD handler=DWORD(reinterpret_cast<uintptr_t>(base+0x1473de0));memcpy(populate+9,&handler,4);
    handler=DWORD(reinterpret_cast<uintptr_t>(base+0x1473e00));memcpy(selection+9,&handler,4);
    const BYTE menuList[]{0x8b,0x8f,0xe4,0,0,0};
    const BYTE originalCases[]{0x83,0xf8,0x05};
    const BYTE showPrefix[]{0x56,0x8b,0xf1,0x83,0x7e,0x0c,0x00};
    const BYTE rawGetter[]{0x8b,0x41,0x10,0xc3};
    if(memcmp(base+PopulateRva,populate,sizeof(populate))||memcmp(base+SelectionRva,selection,sizeof(selection))||
       memcmp(base+0x9d18c,menuList,sizeof(menuList))||memcmp(base+0x9d408,originalCases,sizeof(originalCases))||
       memcmp(base+0xc19a0,showPrefix,sizeof(showPrefix))){InterlockedExchange(&ErrorCode,2);return false;}
    if(memcmp(base+0x566320,rawGetter,sizeof(rawGetter))||!IsJumpTo(base+0x566fe0,base+0x566320)){
        InterlockedExchange(&ErrorCode,3);return false;
    }
    // The existing player-controller path computes the original ordinal and
    // calls its SetPlayerRank script event. Its actor rank is already replicated
    // by the game. Recognize this path; do not create a new master profile field
    // or write another player's rank in this helper.
    RemotePipelineRecognized=IsCallTo(base+0x8d5b27,base+0x562e40)&&
        IsCallTo(base+0x8d5b34,base+0x562ec0)&&IsCallTo(base+0x8d5b3c,base+0x8c6180);
    if(!RemotePipelineRecognized){InterlockedExchange(&ErrorCode,2);return false;}
    Append=reinterpret_cast<AppendFn>(base+0xcf5380);
    Localize=reinterpret_cast<LocalizeFn>(base+0x4f2640);
    Release=reinterpret_cast<ReleaseFn>(base+0x7840);
    SelectionId=NameId("Lst_MenuSelection");
    int first=MH_CreateHook(base+PopulateRva,reinterpret_cast<void*>(Populate),reinterpret_cast<void**>(&OriginalPopulate));
    if(first!=MH_OK){InterlockedExchange(&ErrorCode,4);return false;}
    if(MH_CreateHook(base+SelectionRva,reinterpret_cast<void*>(Selection),reinterpret_cast<void**>(&OriginalSelection))!=MH_OK){
        MH_RemoveHook(base+PopulateRva);InterlockedExchange(&ErrorCode,4);return false;
    }
    if(MH_EnableHook(base+PopulateRva)!=MH_OK||MH_EnableHook(base+SelectionRva)!=MH_OK){
        MH_DisableHook(base+PopulateRva);MH_DisableHook(base+SelectionRva);
        MH_RemoveHook(base+PopulateRva);MH_RemoveHook(base+SelectionRva);InterlockedExchange(&ErrorCode,5);return false;
    }
    Installed=true;
    Verified=IsJumpTo(base+PopulateRva,reinterpret_cast<void*>(Populate))&&IsJumpTo(base+SelectionRva,reinterpret_cast<void*>(Selection));
    if(!Verified){
        MH_DisableHook(base+PopulateRva);MH_DisableHook(base+SelectionRva);
        MH_RemoveHook(base+PopulateRva);MH_RemoveHook(base+SelectionRva);Installed=false;InterlockedExchange(&ErrorCode,5);
    }
    return Verified;
}
} // namespace NativeRank
