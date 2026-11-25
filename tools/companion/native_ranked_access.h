#pragma once

// Restore pointers to existing original PC option/filter objects. No synthetic
// widgets, enum conversion, selection changes, or matchmaking protocol edits.
#include <windows.h>
#include <cstdint>
#include <cstring>
#include "MinHook.h"

namespace NativeRankedAccess {
static_assert(sizeof(void*)==4,"Original retail client is x86");
static BYTE* Image=nullptr;
static bool Installed=false,Verified=false;
static volatile LONG CreateRowsAdded=0,FilterRowsAdded=0,ErrorCode=0;
static constexpr uintptr_t Preferred=0x10900000;
static constexpr unsigned CreateRva=0x940380,FiltersRva=0x952960;
struct Vector { BYTE** data; int count; int capacity; };
using CreateFn=void(__thiscall*)(void*);
using FiltersFn=void(__thiscall*)(void*,int);
using AddFn=int(__thiscall*)(Vector*,int,int);
using ValueFn=int(__thiscall*)(void*);
static CreateFn OriginalCreate=nullptr;
static FiltersFn OriginalFilters=nullptr;
static AddFn Add=nullptr;
// The filters init is also where the PunkBuster filter is removed. MinHook
// allows one hook per target, so while this hook owns the init, the PunkBuster
// module registers its post-init step here instead of hooking it again.
using AfterFiltersFn=void(*)(void*,int);
static AfterFiltersFn volatile AfterFilters=nullptr;

static const char* LastError(){
    switch(InterlockedCompareExchange(&ErrorCode,0,0)){
        case 0:return "none";
        case 1:return "clientIdentityMismatch";
        case 2:return "nativeSignatureMismatch";
        case 3:return "hookCreationFailed";
        case 4:return "hookActivationFailed";
        case 5:return "nativeOptionsUnavailable";
        case 6:return "nativeArrayAppendFailed";
        default:return "unknown";
    }
}
static bool Accessible(const void* address,size_t bytes,bool write=false){
    MEMORY_BASIC_INFORMATION info{};
    if(!address||!bytes||!VirtualQuery(address,&info,sizeof(info))||
       info.State!=MEM_COMMIT||(info.Protect&(PAGE_NOACCESS|PAGE_GUARD)))return false;
    if(write&&!(info.Protect&(PAGE_READWRITE|PAGE_WRITECOPY|PAGE_EXECUTE_READWRITE|PAGE_EXECUTE_WRITECOPY)))return false;
    uintptr_t first=reinterpret_cast<uintptr_t>(address),region=reinterpret_cast<uintptr_t>(info.BaseAddress);
    return first>=region&&first-region<=info.RegionSize&&bytes<=info.RegionSize-(first-region);
}
static BYTE* Pointer(const void* at){return Accessible(at,4)?*reinterpret_cast<BYTE* const*>(at):nullptr;}
static bool HasTable(BYTE* object,unsigned rva){return Accessible(object,4)&&Pointer(object)==Image+rva;}
static bool ValidVector(Vector* v,int limit,bool write=false){
    if(!Accessible(v,sizeof(*v),write)||v->count<0||v->capacity<v->count||v->capacity>limit)return false;
    if(!v->capacity)return v->count==0;
    return Accessible(v->data,size_t(v->capacity)*sizeof(BYTE*),write);
}
static bool AppendExisting(Vector* visible,BYTE* object){
    if(!object||!ValidVector(visible,128,true)){InterlockedExchange(&ErrorCode,5);return false;}
    for(int i=0;i<visible->count;++i)if(visible->data[i]==object)return false;
    const int previous=visible->count;
    // Native FArray::Add(1,sizeof(pointer)) performs the original allocation;
    // its element is initialized before any original UI code regains control.
    int slot=Add(visible,1,sizeof(BYTE*));
    if(slot!=previous||!ValidVector(visible,128,true)||visible->count!=previous+1){
        InterlockedExchange(&ErrorCode,6);return false;
    }
    visible->data[slot]=object;
    return true;
}
static void __fastcall Create(void* self,void*){
    OriginalCreate(self);
    BYTE* implementation=static_cast<BYTE*>(self);
    if(!HasTable(implementation,0x178f098)||!Accessible(implementation,0x1c))return;
    auto inventory=reinterpret_cast<Vector*>(Pointer(implementation+4));
    if(!ValidVector(inventory,128)||inventory->count!=33){InterlockedExchange(&ErrorCode,5);return;}
    BYTE* mode=inventory->data[2];
    BYTE* gameType=inventory->data[3];
    if(!HasTable(mode,0x1792640)||!HasTable(gameType,0x1792d40)||
       !Accessible(mode,0x24)||!Accessible(gameType,0x24)||
       Pointer(Pointer(mode)+0x2c)!=Image+0x946230)return;
    // Original mode virtual+2c returns current enum. Only competitive modes
    // 1..6 have PC ranked leaderboard categories; do not expose ranked coop.
    int selectedMode=reinterpret_cast<ValueFn>(Image+0x946230)(mode);
    if(selectedMode<1||selectedMode>6)return;
    if(AppendExisting(reinterpret_cast<Vector*>(implementation+8),gameType))
        InterlockedIncrement(&CreateRowsAdded);
}
static void AddGameTypeFilter(void* self,int callerMode){
    if(callerMode!=1)return; // Custom Match only; Quick Match remains original.
    BYTE* manager=static_cast<BYTE*>(self);
    if(!HasTable(manager,0x1793ca4)||!Accessible(manager,0x28))return;
    auto inventory=reinterpret_cast<Vector*>(manager+8);
    if(!ValidVector(inventory,32)||inventory->count!=8){InterlockedExchange(&ErrorCode,5);return;}
    BYTE* gameType=inventory->data[1];
    if(!HasTable(gameType,0x1793b00)||!Accessible(gameType,0x10))return;
    if(AppendExisting(reinterpret_cast<Vector*>(manager+0x14),gameType))
        InterlockedIncrement(&FilterRowsAdded);
}
static void __fastcall Filters(void* self,void*,int callerMode){
    OriginalFilters(self,callerMode);
    AddGameTypeFilter(self,callerMode);
    if(const AfterFiltersFn after=AfterFilters)after(self,callerMode);
}
static bool IsJumpTo(BYTE* at,const void* target){
    if(!Accessible(at,5)||at[0]!=0xe9)return false;
    LONG displacement=0;memcpy(&displacement,at+1,4);
    return at+5+displacement==target;
}
static void RemoveHooks(){
    MH_DisableHook(Image+CreateRva);MH_DisableHook(Image+FiltersRva);
    MH_RemoveHook(Image+CreateRva);MH_RemoveHook(Image+FiltersRva);
    Installed=false;Verified=false;
}
static bool Install(BYTE* base){
    if(Installed)return Verified;
    if(!Accessible(base,0x1000)){InterlockedExchange(&ErrorCode,1);return false;}
    auto dos=reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if(dos->e_magic!=IMAGE_DOS_SIGNATURE||dos->e_lfanew<=0||dos->e_lfanew>0x800){InterlockedExchange(&ErrorCode,1);return false;}
    auto pe=reinterpret_cast<IMAGE_NT_HEADERS32*>(base+dos->e_lfanew);
    if(pe->Signature!=IMAGE_NT_SIGNATURE||pe->FileHeader.Machine!=IMAGE_FILE_MACHINE_I386||
       pe->FileHeader.TimeDateStamp!=0x47106421||pe->OptionalHeader.ImageBase!=Preferred||
       pe->OptionalHeader.SizeOfImage!=0x1f56000){InterlockedExchange(&ErrorCode,1);return false;}
    Image=base;
    const BYTE create[]{0x53,0x56,0x57,0x8b,0xf9,0x83,0x7f,0x10,0,0x8d,0x77,8,0xc7,0x46,4,0,0,0,0};
    BYTE filters[]{0x6a,0xff,0x64,0xa1,0,0,0,0,0x68,0,0,0,0,0x50,0x64,0x89,0x25,0,0,0,0};
    DWORD handler=DWORD(reinterpret_cast<uintptr_t>(base+0x14eaa78));memcpy(filters+9,&handler,4);
    const BYTE add[]{0x8b,0x44,0x24,4,0x53,0x56,0x8b,0xf1,0x8b,0x5e,4,0x8d,0x0c,3};
    const BYTE value[]{0x8b,0x41,0x14,0xc3};
    if(!Accessible(base+CreateRva,sizeof(create))||!Accessible(base+FiltersRva,sizeof(filters))||
       !Accessible(base+0x20310,sizeof(add))||!Accessible(base+0x946230,sizeof(value))||
       memcmp(base+CreateRva,create,sizeof(create))||memcmp(base+FiltersRva,filters,sizeof(filters))||
       memcmp(base+0x20310,add,sizeof(add))||memcmp(base+0x946230,value,sizeof(value))){
        InterlockedExchange(&ErrorCode,2);return false;
    }
    Add=reinterpret_cast<AddFn>(base+0x20310);
    if(MH_CreateHook(base+CreateRva,reinterpret_cast<void*>(Create),reinterpret_cast<void**>(&OriginalCreate))!=MH_OK){
        InterlockedExchange(&ErrorCode,3);return false;
    }
    if(MH_CreateHook(base+FiltersRva,reinterpret_cast<void*>(Filters),reinterpret_cast<void**>(&OriginalFilters))!=MH_OK){
        MH_RemoveHook(base+CreateRva);InterlockedExchange(&ErrorCode,3);return false;
    }
    if(MH_EnableHook(base+CreateRva)!=MH_OK||MH_EnableHook(base+FiltersRva)!=MH_OK){
        RemoveHooks();InterlockedExchange(&ErrorCode,4);return false;
    }
    Installed=true;
    Verified=IsJumpTo(base+CreateRva,reinterpret_cast<void*>(Create))&&IsJumpTo(base+FiltersRva,reinterpret_cast<void*>(Filters));
    if(!Verified){RemoveHooks();InterlockedExchange(&ErrorCode,4);}
    return Verified;
}
} // namespace NativeRankedAccess
