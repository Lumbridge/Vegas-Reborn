#pragma once

// Include after claimed_name.h. Display-only CALL sites for the original PC
// Internet session path. Never change AgUserInformation.username or its hash.
#include <windows.h>
#include <cstring>

namespace NativeClaimedSessionName {
static bool Installed=false,Verified=false;
static const char* Error="notInstalled";
static BYTE* Image=nullptr;
static constexpr unsigned Count=4;
static const unsigned Sites[Count]={0x10025fa,0x1002726,0x100bb3c,0x945f48};
static const BYTE Originals[Count][5]={
    {0xe8,0xe1,0x0c,0xfc,0xff},{0xe8,0xb5,0x0b,0xfc,0xff},
    {0xe8,0x9f,0x77,0xfb,0xff},{0xe8,0xe3,0xb1,0xc3,0xff}};
static BYTE Replacements[Count][5]{};
static DWORD Protections[Count]{};
using AgStringCtor=void*(__thiscall*)(void*,const wchar_t*);
static AgStringCtor OriginalCtor=nullptr;

static void* __fastcall DisplayString(void* destination,void*,const wchar_t*) {
    // This is the original allocating wide-string constructor, not an in-place
    // edit to either the source username or a shared network object.
    return OriginalCtor(destination,ClaimedName::Claim.c_str());
}
static void* __fastcall TravelName(void* owner,void*,void* destination,unsigned index) {
    void* result=ClaimedName::NativePlayerGetter(owner,destination,index);
    ClaimedName::Apply(destination);
    return result;
}
static bool Restore(unsigned count) {
    bool success=true;
    for(unsigned n=0;n<count;++n){
        BYTE* site=Image+Sites[n];
        // Only undo our own complete five-byte operation.
        if(memcmp(site,Replacements[n],5)){success=false;continue;}
        DWORD temporary=0,ignored=0;
        if(!VirtualProtect(site,5,PAGE_EXECUTE_READWRITE,&temporary)){success=false;continue;}
        memcpy(site,Originals[n],5);
        if(!FlushInstructionCache(GetCurrentProcess(),site,5))success=false;
        if(!VirtualProtect(site,5,Protections[n],&ignored))success=false;
        if(memcmp(site,Originals[n],5))success=false;
    }
    return success;
}
static bool Install(BYTE* base) {
    if(Installed){Error="alreadyInstalled";return false;}
    if(ClaimedName::Claim.empty()){Error="claimFileAbsent";return true;}
    if(!base||!ClaimedName::Identity(base)||!ClaimedName::Verified||
       !ClaimedName::NativePlayerGetter||!ClaimedName::NativeAssign){
        Error="claimedNameDependencyUnavailable";return false;
    }
    // Constructor RET4, same-object return, original AgString allocator. The
    // immediate addresses below are CALL displacements and require no rebasing.
    const BYTE ctor[]{0x8b,0x44,0x24,0x04,0x56,0x8b,0xf1,0x50,0xc7,0x06,
        0,0,0,0,0x66,0xc7,0x46,0x04,0,0,0xe8,0x27,0xfe,0xff,0xff,0x8b,0xc6,0x5e,0xc2,0x04,0};
    const BYTE memberPublic[]{0x83,0xc0,0x08,0x50,0x8d,0x4c,0x24,0x40};
    const BYTE memberPrivate[]{0x83,0xc0,0x08,0x50,0x8d,0x4c,0x24,0x38};
    const BYTE host[]{0x83,0xc0,0x08,0x50,0x8d,0x4c,0x24,0x20};
    const BYTE travel[]{0x8d,0x54,0x24,0x70,0x56,0x52,0xe8,0xaa,0xac,0x71,0xff,0x8b,0xc8};
    if(memcmp(base+0xfc32e0,ctor,sizeof(ctor))||
       memcmp(base+0x10025f2,memberPublic,sizeof(memberPublic))||
       memcmp(base+0x100271e,memberPrivate,sizeof(memberPrivate))||
       memcmp(base+0x100bb34,host,sizeof(host))||
       memcmp(base+0x945f3b,travel,sizeof(travel))){Error="nativeContextMismatch";return false;}
    for(unsigned n=0;n<Count;++n)
        if(memcmp(base+Sites[n],Originals[n],5)){Error="nativeCallMismatch";return false;}
    Image=base;
    OriginalCtor=reinterpret_cast<AgStringCtor>(base+0xfc32e0);
    for(unsigned n=0;n<Count;++n)
        ClaimedName::MakeCall(Replacements[n],base+Sites[n],n==3?
            reinterpret_cast<const void*>(TravelName):reinterpret_cast<const void*>(DisplayString));
    unsigned written=0;
    for(unsigned n=0;n<Count;++n){
        BYTE* site=base+Sites[n];DWORD ignored=0;
        if(!VirtualProtect(site,5,PAGE_EXECUTE_READWRITE,&Protections[n]))break;
        memcpy(site,Replacements[n],5);++written;
        const bool flushed=FlushInstructionCache(GetCurrentProcess(),site,5)!=FALSE;
        const bool restored=VirtualProtect(site,5,Protections[n],&ignored)!=FALSE;
        if(!flushed||!restored||memcmp(site,Replacements[n],5))break;
        if(n+1==Count){Installed=true;Verified=true;Error="none";return true;}
    }
    const bool restored=Restore(written);
    Error=restored?"installationFailedRolledBack":"installationFailedRollbackIncomplete";
    return false;
}
static const char* LastError(){return Error;}
} // namespace NativeClaimedSessionName
