#pragma once
// Restore the original PEC item/bundle availability path. No XP or catalog edits.
#include <windows.h>
#include <cstdint>
#include <cstring>
#include <initializer_list>
namespace NativeUnlockPolicy {
static_assert(sizeof(void*)==4,"Original PC client is x86");
static BYTE* Base=nullptr;
static bool Installed=false,Verified=false;
static const char* Error="notInstalled";
static constexpr uintptr_t Preferred=0x10900000;
static constexpr unsigned Rva(uintptr_t address){return unsigned(address-Preferred);}
using ProfileFn=void*(__thiscall*)(void*,unsigned);
using InfoFn=void*(__thiscall*)(void*);
using AvailableFn=bool(__thiscall*)(void*,void*,unsigned);
static ProfileFn Profile=nullptr;
static InfoFn Info=nullptr;
static AvailableFn OriginalAvailable=nullptr;
static const char* LastError(){return Error;}
static bool Readable(const void* p,size_t size){
    if(!p)return false;MEMORY_BASIC_INFORMATION m{};
    if(!VirtualQuery(p,&m,sizeof(m))||m.State!=MEM_COMMIT||(m.Protect&(PAGE_GUARD|PAGE_NOACCESS)))return false;
    uintptr_t at=reinterpret_cast<uintptr_t>(p);
    uintptr_t end=reinterpret_cast<uintptr_t>(m.BaseAddress)+m.RegionSize;
    return end>=at&&size<=end-at;
}
static bool Bytes(uintptr_t address,std::initializer_list<BYTE> data){return !memcmp(Base+Rva(address),data.begin(),data.size());}
static bool Calls(uintptr_t address,uintptr_t target){
    auto p=Base+Rva(address);if(*p!=0xe8)return false;LONG relative=0;memcpy(&relative,p+1,4);
    return p+5+relative==Base+Rva(target);
}
// Bind before creating the existing facade hook. The owner keeps its raw-XP
// redirect and exact JMP readback, enabling the facade only after that redirect.
static bool Install(BYTE* base){
    Base=base;Error="imageIdentityMismatch";
    auto dos=reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if(!Readable(dos,sizeof(*dos))||dos->e_magic!=IMAGE_DOS_SIGNATURE)return false;
    auto pe=reinterpret_cast<IMAGE_NT_HEADERS32*>(base+dos->e_lfanew);
    if(!Readable(pe,sizeof(*pe))||pe->Signature!=IMAGE_NT_SIGNATURE||pe->FileHeader.Machine!=IMAGE_FILE_MACHINE_I386||
       pe->FileHeader.TimeDateStamp!=0x47106421||pe->OptionalHeader.ImageBase!=Preferred||
       pe->OptionalHeader.SizeOfImage!=0x1f56000)return false;
    Error="nativeSignatureMismatch";
    if(!Bytes(0x10e656c0,{0x8b,0x44,0x24,0x04,0x50,0xe8,0xd6,0xfb,0xff,0xff,0x8b,0x51,0x14,0x83,0xc4,0x04})||
       !Bytes(0x10e65380,{0x8b,0x41,0x04,0xc3})||
       !Bytes(0x10e6a300,{0x56,0x57,0x8b,0x7c,0x24,0x0c,0x85,0xff,0x8b,0xf1,0x75,0x07})||
       !Bytes(0x10e6a319,{0x8d,0x5e,0x4c})||!Bytes(0x10e6a34c,{0x8d,0x8e,0xdc,0,0,0})||
       !Bytes(0x10e6a364,{0xc2,0x08,0x00})||
       !Calls(0x10e6a31f,0x10e6e9c0)||!Calls(0x10e6a334,0x10e69e50)||
       !Calls(0x10e6a33c,0x10e69ea0)||!Calls(0x10e6a346,0x10e6e9e0)||!Calls(0x10e6a352,0x10e70f70))return false;
    Profile=reinterpret_cast<ProfileFn>(base+Rva(0x10e656c0));
    Info=reinterpret_cast<InfoFn>(base+Rva(0x10e65380));
    OriginalAvailable=reinterpret_cast<AvailableFn>(base+Rva(0x10e6a300));
    Installed=true;Verified=true;Error="none";return true;
}
static bool Available(void* controller,unsigned profileHandle,unsigned itemId){
    if(!Verified||!Readable(controller,12))return false;
    auto service=*reinterpret_cast<void**>(static_cast<BYTE*>(controller)+8);
    if(!Readable(service,0x18))return false;
    auto profile=Profile(service,profileHandle);auto info=Info(service);
    if(!Readable(profile,0x14)||!Readable(info,0xec))return false;
    // Original helper resolves item+4c -> group -> bundle+dc and compares its
    // named rank ordinal. Its original missing-item/default behavior is kept.
    return OriginalAvailable(info,profile,itemId);
}
}
