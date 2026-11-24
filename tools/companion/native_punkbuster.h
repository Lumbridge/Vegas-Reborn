#pragma once

// Rainbow Six Vegas 1 "ENABLE PUNKBUSTER SERVER" checkbox (Create Match).
//
// The Create Match menu (MP_RSMenus.mgb) binds its widgets by name at RVA
// 0xae780: names from the table at 0x122e75e4 go to menu +0x2e0 onwards, so
// CB_PBServer (7th) is the checkbox at +0x2f8 and its text at +0x314
// (R6Menus.int [ServerOptions] PreGamePunkbuster / InGamePunkbuster). Three
// setup methods fill the menu (slot 6 of three menu vtables):
// - 0xb1f10 and 0xb21c0 show the checkbox: widget slot 9 (+0x24) with 1.
// - 0xb2460 hides B_Link01 and the checkbox: +0x18 bit 0 cleared and widget
//   slot 9 with 0 (0xb25e9).
// After each setup method, the adapter hides the checkbox the same way the
// original 0xb2460 does. No other code shows it again.
//
// Every PunkBuster server start goes through 0x1045090 (PunkBuster command
// 0x75): the checkbox, and three automatic starts gated on the saved
// m_bPunkBusterSv flag (0x65376, 0xaf186 at the menu, 0x4e77a2). The adapter
// replaces that routine with one that does nothing, so the server stays off and
// the menu saves m_bPunkBusterSv=False with the off state. Turning the server
// off (0x10450f0) is unchanged.
#include <windows.h>
#include <cstdint>
#include <cstring>
#include "MinHook.h"

namespace NativePunkBuster {
static_assert(sizeof(void*)==4,"Original retail client is x86");
static constexpr unsigned SetupCount=3;
static constexpr unsigned SetupRvas[SetupCount]{0xb1f10,0xb21c0,0xb2460};
static constexpr BYTE SetupPrologues[SetupCount][14]{
    {0x6a,0xff,0x68,0xc8,0x65,0xd7,0x11,0x64,0xa1,0,0,0,0,0x50},
    {0x6a,0xff,0x68,0x18,0x66,0xd7,0x11,0x64,0xa1,0,0,0,0,0x50},
    {0x6a,0xff,0x68,0x60,0x66,0xd7,0x11,0x64,0xa1,0,0,0,0,0x50}};
// mov edi,0x122e75e4 / lea esi,[ebp+0x2e0]: the widget-name binding.
static constexpr unsigned BindRva=0xae7a2;
static constexpr BYTE BindCode[]{0xbf,0xe4,0x75,0x2e,0x12,0x8d,0xb5,0xe0,0x02,0x00,0x00};
// mov ecx,[esi+0x2f8] / mov edx,[ecx] / push 0 / call [edx+0x24]: the original hide.
static constexpr unsigned HideRva=0xb25e9;
static constexpr BYTE HideCode[]{0x8b,0x8e,0xf8,0x02,0x00,0x00,0x8b,0x11,0x6a,0x00,0xff,0x52,0x24};
static constexpr unsigned StartRva=0x1045090,StartCommandRva=0x10450d9;
static constexpr BYTE StartCode[]{0xa1,0x10,0x9f,0x5a,0x12,0x85,0xc0,0x74,0x4f,0x56,0x8d,0xb0,0xb4,0x03,0x00,0x00};
static constexpr BYTE StartCommand[]{0x6a,0xff,0x6a,0x75};
static constexpr unsigned CheckboxOffset=0x2f8,ShowSlot=0x24/4,FlagsOffset=0x18;
static constexpr unsigned HookCount=SetupCount+1;

static BYTE* Image=nullptr;
static bool Verified=false;
static unsigned Error=0;
using SetupFn=void(__thiscall*)(void*);
using ShowFn=void(__thiscall*)(void*,int);
using StartFn=void(__cdecl*)();
static SetupFn OriginalSetups[SetupCount]{};
static StartFn OriginalStart=nullptr;

static const char* LastError(){
    switch(Error){case 0:return "none";case 1:return "nativeSignatureMismatch";
        case 2:return "hookCreationFailed";case 3:return "hookActivationFailed";default:return "unknown";}
}
static bool Readable(const void* pointer,size_t bytes){
    MEMORY_BASIC_INFORMATION info{};
    if(!pointer||!bytes||!VirtualQuery(pointer,&info,sizeof(info))||info.State!=MEM_COMMIT||
       (info.Protect&(PAGE_GUARD|PAGE_NOACCESS)))return false;
    auto first=reinterpret_cast<uintptr_t>(info.BaseAddress),at=reinterpret_cast<uintptr_t>(pointer);
    return at>=first&&at-first<=info.RegionSize&&bytes<=info.RegionSize-(at-first);
}
static void HideCheckbox(void* menu){
    if(!Readable(static_cast<BYTE*>(menu)+CheckboxOffset,4))return;
    BYTE* checkbox=*reinterpret_cast<BYTE**>(static_cast<BYTE*>(menu)+CheckboxOffset);
    if(!Readable(checkbox,FlagsOffset+1))return;
    void** table=*reinterpret_cast<void***>(checkbox);
    if(!Readable(table+ShowSlot,4))return;
    checkbox[FlagsOffset]&=0xfe;
    reinterpret_cast<ShowFn>(table[ShowSlot])(checkbox,0);
}
template<unsigned Index> static void __fastcall Setup(void* menu,void*){
    OriginalSetups[Index](menu);
    HideCheckbox(menu);
}
static void __cdecl KeepServerOff(){}

static bool Matches(BYTE* base){
    for(unsigned i=0;i<SetupCount;++i)if(memcmp(base+SetupRvas[i],SetupPrologues[i],sizeof(SetupPrologues[i])))return false;
    return !memcmp(base+BindRva,BindCode,sizeof(BindCode))&&!memcmp(base+HideRva,HideCode,sizeof(HideCode))&&
        !memcmp(base+StartRva,StartCode,sizeof(StartCode))&&!memcmp(base+StartCommandRva,StartCommand,sizeof(StartCommand));
}
static bool Install(BYTE* base){
    Image=base;
    if(!Matches(base)){Error=1;return false;}
    void* const targets[HookCount]{base+SetupRvas[0],base+SetupRvas[1],base+SetupRvas[2],base+StartRva};
    void* const detours[HookCount]{reinterpret_cast<void*>(&Setup<0>),reinterpret_cast<void*>(&Setup<1>),
        reinterpret_cast<void*>(&Setup<2>),reinterpret_cast<void*>(&KeepServerOff)};
    void** const originals[HookCount]{reinterpret_cast<void**>(&OriginalSetups[0]),reinterpret_cast<void**>(&OriginalSetups[1]),
        reinterpret_cast<void**>(&OriginalSetups[2]),reinterpret_cast<void**>(&OriginalStart)};
    unsigned created=0;
    for(;created<HookCount;++created)if(MH_CreateHook(targets[created],detours[created],originals[created])!=MH_OK)break;
    if(created<HookCount){while(created)MH_RemoveHook(targets[--created]);Error=2;return false;}
    for(unsigned i=0;i<HookCount;++i){
        if(MH_EnableHook(targets[i])!=MH_OK){
            for(unsigned j=0;j<HookCount;++j){MH_DisableHook(targets[j]);MH_RemoveHook(targets[j]);}
            Error=3;return false;
        }
    }
    Verified=true;
    return true;
}
}
