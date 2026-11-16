#pragma once
// Client-only multiplayer appearance policy. Include after MinHook.h.
// Output must be the companion's persistent, account-scoped output directory.
#include <windows.h>
#include <cstdint>
#include <cstring>
#include <string>
#include "MinHook.h"

namespace NativePecDefaults {
static_assert(sizeof(void*)==4,"Original PC client is x86");
static BYTE* Image=nullptr;
static bool Installed=false,Verified=false;
static volatile LONG Error=0,Applied=0,ExplicitChoices=0;
static std::wstring Directory;
static CRITICAL_SECTION PolicyMutex;
static bool MutexReady=false;
struct PolicyLock {
    PolicyLock(){EnterCriticalSection(&PolicyMutex);}
    ~PolicyLock(){LeaveCriticalSection(&PolicyMutex);}
    PolicyLock(const PolicyLock&)=delete;
    PolicyLock& operator=(const PolicyLock&)=delete;
};
struct Preference { bool present=false,explicitIntent=false,pending=false,restorePending=false; BYTE choice=5,origin=1; ULONGLONG retryAt=0; BYTE* cached=nullptr; };
static Preference Preferences[2];
static __declspec(thread) unsigned RestoreDepth=0,GetterDepth=0,CompletionDepth=0;
static __declspec(thread) bool CompletionConsumed=false;
using GetFn=BYTE*(__thiscall*)(void*,int);
using RestoreFn=int(__cdecl*)(void*);
using SelectFn=int(__thiscall*)(void*,unsigned,unsigned,int,unsigned,void*);
using VoidFn=void(__thiscall*)(void*);
using CtorFn=void*(__thiscall*)(void*);
using FacadeFn=BYTE*(__cdecl*)();
using StateFn=bool(__thiscall*)(void*);
using HandleFn=int(__thiscall*)(void*,int);
using ReadyFn=bool(__thiscall*)(void*,int);
static GetFn OriginalGet=nullptr;
static RestoreFn OriginalRestore=nullptr;
static SelectFn OriginalSelect=nullptr;
static VoidFn OriginalCompletion=nullptr;
static CtorFn OriginalCtor=nullptr;
static VoidFn OriginalApplied=nullptr;
static FacadeFn Facade=nullptr;
static StateFn State=nullptr;
static HandleFn Handle=nullptr;
static ReadyFn Ready=nullptr;
static constexpr unsigned Rva(unsigned va){return va-0x10900000u;}
static bool Readable(const void* pointer,size_t size,bool write=false){
    MEMORY_BASIC_INFORMATION m{};
    if(!pointer||!size||!VirtualQuery(pointer,&m,sizeof(m))||m.State!=MEM_COMMIT||
       (m.Protect&(PAGE_GUARD|PAGE_NOACCESS)))return false;
    const auto at=reinterpret_cast<uintptr_t>(pointer),begin=reinterpret_cast<uintptr_t>(m.BaseAddress);
    if(at<begin||at-begin>m.RegionSize||size>m.RegionSize-(at-begin))return false;
    return !write||(m.Protect&(PAGE_READWRITE|PAGE_WRITECOPY|PAGE_EXECUTE_READWRITE|PAGE_EXECUTE_WRITECOPY));
}
static BYTE* Pointer(const void* at){return Readable(at,4)?*reinterpret_cast<BYTE* const*>(at):nullptr;}
static bool Options(BYTE* value){return Readable(value,0xa4,true)&&Pointer(value)==Image+Rva(0x11f4ec80);}
static bool ManagerArray(BYTE* manager){
    if(!Readable(manager,0x50))return false;
    const int count=*reinterpret_cast<int*>(manager+0x38),capacity=*reinterpret_cast<int*>(manager+0x3c);
    BYTE* data=Pointer(manager+0x34);
    return count>=1&&count<=2&&capacity>=count&&capacity<=16&&Readable(data,size_t(count)*4);
}
static BYTE* Manager(){
    BYTE* game=Pointer(Image+Rva(0x123c1a28));
    BYTE* manager=game?Pointer(game+0x88):nullptr;
    return ManagerArray(manager)?manager:nullptr;
}
static bool Slot(BYTE* manager,int index){
    return manager&&index>=0&&index<2&&index<*reinterpret_cast<int*>(manager+0x38);
}
static int IndexOf(BYTE* options){
    BYTE* manager=Manager();if(!manager)return -1;
    BYTE* data=Pointer(manager+0x34);
    for(int i=0;i<*reinterpret_cast<int*>(manager+0x38);++i)if(Pointer(data+i*4)==options)return i;
    return -1;
}
static bool PecReady(BYTE* manager,int index){
    BYTE* facade=Facade();
    if(!Readable(facade,0x28)||!State(facade)||!Readable(Pointer(facade+8),0x18))return false;
    const int handle=Handle(manager,index);
    return handle>=0&&handle<2&&Ready(facade,handle);
}
static std::wstring Path(int index){return Directory+L"\\multiplayer-appearance-v1-"+wchar_t(L'0'+index)+L".dat";}
static bool Load(int index){
    const std::wstring path=Path(index);DWORD attributes=GetFileAttributesW(path.c_str());
    if(attributes==INVALID_FILE_ATTRIBUTES){
        const DWORD error=GetLastError();return error==ERROR_FILE_NOT_FOUND||error==ERROR_PATH_NOT_FOUND;
    }
    if(attributes&(FILE_ATTRIBUTE_DIRECTORY|FILE_ATTRIBUTE_REPARSE_POINT))return false;
    HANDLE file=CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
    if(file==INVALID_HANDLE_VALUE)return false;
    BYTE bytes[9]{};DWORD count=0;bool good=ReadFile(file,bytes,sizeof(bytes),&count,nullptr)!=FALSE;CloseHandle(file);
    good=good&&count==8&&!memcmp(bytes,"VMPA",4)&&bytes[4]==1&&bytes[5]<=17&&bytes[6]>=1&&bytes[6]<=4&&bytes[7]==0;
    good=good&&((bytes[6]!=1&&bytes[6]!=4)||bytes[5]==5)&&(bytes[6]!=2||(bytes[5]>0&&bytes[5]!=5));
    if(!good)return false;
    auto& p=Preferences[index];p.present=true;p.choice=bytes[5];p.origin=bytes[6];p.explicitIntent=p.origin==3;
    return true;
}
static bool Persist(int index){
    auto& p=Preferences[index];if(!p.pending)return true;
    const ULONGLONG now=GetTickCount64();if(now<p.retryAt)return false;
    p.retryAt=now+5000;
    const std::wstring target=Path(index),temporary=target+L".tmp-"+std::to_wstring(GetCurrentProcessId())+L"-"+std::to_wstring(now);
    const DWORD attr=GetFileAttributesW(target.c_str());
    if(attr!=INVALID_FILE_ATTRIBUTES&&(attr&(FILE_ATTRIBUTE_DIRECTORY|FILE_ATTRIBUTE_REPARSE_POINT))){InterlockedExchange(&Error,5);return false;}
    HANDLE file=CreateFileW(temporary.c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_TEMPORARY|FILE_FLAG_WRITE_THROUGH,nullptr);
    if(file==INVALID_HANDLE_VALUE){InterlockedExchange(&Error,5);return false;}
    const BYTE bytes[8]={'V','M','P','A',1,p.choice,p.origin,0};DWORD count=0;
    bool good=WriteFile(file,bytes,sizeof(bytes),&count,nullptr)&&count==sizeof(bytes)&&FlushFileBuffers(file);
    if(!CloseHandle(file))good=false;
    if(good)good=MoveFileExW(temporary.c_str(),target.c_str(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH)!=FALSE;
    if(!good){DeleteFileW(temporary.c_str());InterlockedExchange(&Error,5);return false;}
    p.pending=false;p.present=true;p.retryAt=0;InterlockedExchange(&Error,0);return true;
}
static void Choose(int index,BYTE choice,BYTE origin){
    auto& p=Preferences[index];p.choice=choice;p.origin=origin;p.explicitIntent=origin==3;p.pending=true;p.retryAt=0;p.cached=nullptr;
    Persist(index);
}
static void Apply(BYTE* manager,int index,BYTE* options,bool restored){
    if(!Slot(manager,index)||!Options(options))return;
    auto& p=Preferences[index];if(restored)p.restorePending=true;
    const bool loaded=p.restorePending;
    if(!loaded&&p.cached==options){if(p.pending)Persist(index);return;}
    const BYTE old=options[0x64];if(old>17)return;
    // Both automatic-default origins remain eligible for a later original
    // loaded preset, regardless of archive/config ordering. An adopted preset
    // survives later native zeros; an explicit user choice always wins.
    const bool adopt=loaded&&(p.origin==1||p.origin==4)&&!p.explicitIntent&&old>0&&old!=5;
    const bool initialize=adopt||(!p.present&&!p.pending);
    const BYTE choice=initialize?(old?old:BYTE(5)):p.choice;
    // Native presets need no PEC profile. Only custom/default5 retains the
    // original custom-character readiness contract.
    if(choice==5&&!PecReady(manager,index))return;
    if(initialize)Choose(index,choice,choice!=5?BYTE(2):(loaded?BYTE(4):BYTE(1)));
    // Explicit intent is already known in memory: preserve it through a
    // native reload even while its durable write is pending. Automatic
    // migration still waits for durable metadata before changing options.
    if(!Persist(index)&&!p.explicitIntent)return;
    if(loaded)p.restorePending=false;
    options[0x64]=p.choice;p.cached=options;InterlockedIncrement(&Applied);
}
static BYTE* __fastcall Get(void* self,void*,int index){
    // Always preserve the original accessor result, including other manager
    // instances. Bounds and identity guards restrict only our subsequent policy.
    BYTE* options=OriginalGet(self,index);
    BYTE* manager=Manager();
    if(self!=manager||!Slot(manager,index)||!Options(options))return options;
    if(RestoreDepth||GetterDepth)return options;
    ++GetterDepth;PolicyLock lock;
    if(CompletionDepth&&!CompletionConsumed){
        CompletionConsumed=true;Choose(index,5,3);InterlockedIncrement(&ExplicitChoices);
        // The original completion callback itself writes 5 immediately after
        // this getter returns, then invokes its original conditional save.
    }else Apply(manager,index,options,false);
    --GetterDepth;return options;
}
static int __cdecl Restore(void* archive){
    ++RestoreDepth;const int result=OriginalRestore(archive);--RestoreDepth;
    if(result&&!RestoreDepth){
        PolicyLock lock;
        BYTE* manager=Manager();
        if(manager){
            const int index=*reinterpret_cast<int*>(manager+0x4c);
            if(Slot(manager,index)){
                BYTE* options=OriginalGet(manager,index);Preferences[index].cached=nullptr;
                ++GetterDepth;Apply(manager,index,options,true);--GetterDepth;
            }
        }
    }
    return result;
}
static void __fastcall SettingsApplied(void* self,void*){
    OriginalApplied(self);
    BYTE* options=static_cast<BYTE*>(self);
    if(!Options(options))return;
    const int index=IndexOf(options);if(index<0)return;
    PolicyLock lock;
    auto& p=Preferences[index];p.cached=nullptr;p.restorePending=true;
    if(RestoreDepth||GetterDepth)return;
    ++GetterDepth;Apply(Manager(),index,options,true);--GetterDepth;
}
static int __fastcall Select(void* self,void*,unsigned a,unsigned b,int c,unsigned selected,void* context){
    const bool row=Readable(context,12)&&*reinterpret_cast<int*>(static_cast<BYTE*>(context)+8)==1;
    BYTE* before=row&&Readable(self,0xdc)?Pointer(static_cast<BYTE*>(self)+0xd4):nullptr;
    const int result=OriginalSelect(self,a,b,c,selected,context);
    BYTE* after=row&&Readable(self,0xdc)?Pointer(static_cast<BYTE*>(self)+0xd4):nullptr;
    if(before==after&&Options(after)&&after[0x64]<=17){
        PolicyLock lock;
        const int index=IndexOf(after);if(index>=0){Choose(index,after[0x64],3);Preferences[index].cached=after;InterlockedIncrement(&ExplicitChoices);}
    }
    return result;
}
static void __fastcall Complete(void* self,void*){
    const bool previous=CompletionConsumed;if(!CompletionDepth)CompletionConsumed=false;
    ++CompletionDepth;OriginalCompletion(self);--CompletionDepth;CompletionConsumed=previous;
}
static void* __fastcall Construct(void* self,void*){
    void* result=OriginalCtor(self);
    PolicyLock lock;
    for(auto& p:Preferences)if(p.cached==result)p.cached=nullptr;
    return result;
}
static const char* LastError(){
    switch(InterlockedCompareExchange(&Error,0,0)){
        case 0:return "none";case 1:return "clientIdentityMismatch";case 2:return "nativeSignatureMismatch";
        case 3:return "hookInstallationFailed";case 4:return "appearancePreferenceUnavailable";
        case 5:return "appearancePreferenceWriteFailed";default:return "unknown";
    }
}
static bool Bytes(unsigned va,const BYTE* bytes,size_t count){return !memcmp(Image+Rva(va),bytes,count);}
static bool Address(unsigned va,unsigned expected){return Pointer(Image+Rva(va))==Image+Rva(expected);}
static bool Install(BYTE* base,const std::wstring& accountOutput){
    if(Installed)return false;Image=base;
    if(!Readable(base,0x1000)){InterlockedExchange(&Error,1);return false;}
    const auto dos=reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    if(dos->e_magic!=IMAGE_DOS_SIGNATURE||dos->e_lfanew<=0||dos->e_lfanew>0x800){InterlockedExchange(&Error,1);return false;}
    const auto pe=reinterpret_cast<const IMAGE_NT_HEADERS32*>(base+dos->e_lfanew);
    if(pe->Signature!=IMAGE_NT_SIGNATURE||pe->FileHeader.Machine!=IMAGE_FILE_MACHINE_I386||pe->FileHeader.TimeDateStamp!=0x47106421||
       pe->OptionalHeader.Magic!=IMAGE_NT_OPTIONAL_HDR32_MAGIC||pe->OptionalHeader.ImageBase!=0x10900000||pe->OptionalHeader.SizeOfImage!=0x1f56000){InterlockedExchange(&Error,1);return false;}
    const BYTE get[]{0x8b,0x41,0x34,0x8b,0x4c,0x24,0x04,0x8b,0x04,0x88,0xc2,0x04,0};
    const BYTE ctor[]{0x56,0x8b,0xf1,0xe8,0x18,0x9b,0x12,0,0xc7,0x06};
    const BYTE ctorEnd[]{0x8b,0xc6,0x5e,0xc3};
    const BYTE restore[]{0xa1};const BYTE restoreTail[]{0x8b,0x88,0x88,0,0,0,0x8b,0x51,0x4c,0x56,0x57,0x52,0xe8,0xda,0xe6,0xb6,0xff};
    const BYTE select[]{0x64,0xa1,0,0,0,0,0x6a,0xff,0x68};const BYTE selectEnd[]{0x50,0x8b,0x44,0x24,0x20};
    const BYTE complete[]{0x56,0x8b,0xf1,0x8b,0x8e,0x4c,0xff,0xff,0xff,0x8b,0x01,0x57,0xff,0x50,0x5c,0x8b};
    const BYTE state[]{0x8b,0x11,0x33,0xc0,0x83,0xfa,0x02,0x0f,0x94,0xc0,0xc3};
    const BYTE handle[]{0x8b,0x54,0x24,0x04,0x33,0xc0,0x83,0xc1,0x40};
    const BYTE ready[]{0x56,0x57,0x8b,0x7c,0x24,0x0c,0x8b,0xf1,0x8b,0x4e,0x08,0x57,0xe8,0xff,0x28,0,0};
    const BYTE facade[]{0x6a,0xff,0x68};
    const BYTE applied[]{0x64,0xa1,0,0,0,0,0x6a,0xff,0x68};
    const BYTE appliedEnd[]{0x50,0x64,0x89,0x25,0,0,0,0,0x83,0xec,0x18};
    if(!Bytes(0x10ce4680,get,sizeof(get))||!Bytes(0x10ccfb60,ctor,sizeof(ctor))||!Address(0x10ccfb6a,0x11f4ec80)||!Bytes(0x10ccfb6e,ctorEnd,sizeof(ctorEnd))||
       !Bytes(0x11175f90,restore,sizeof(restore))||!Address(0x11175f91,0x123c1a28)||!Bytes(0x11175f95,restoreTail,sizeof(restoreTail))||
       !Bytes(0x109f6ee0,select,sizeof(select))||!Address(0x109f6ee9,0x11d7bc08)||!Bytes(0x109f6eed,selectEnd,sizeof(selectEnd))||
       !Bytes(0x109d4e40,complete,sizeof(complete))||!Bytes(0x10e62d50,state,sizeof(state))||!Bytes(0x10ce44f0,handle,sizeof(handle))||!Bytes(0x10e62db0,ready,sizeof(ready))||
       !Address(0x11f4ed58,0x10cd0da0)||!Bytes(0x10cd0da0,applied,sizeof(applied))||!Address(0x10cd0da9,0x11d9a930)||!Bytes(0x10cd0dad,appliedEnd,sizeof(appliedEnd))||!Bytes(0x10e642d0,facade,sizeof(facade))||!Address(0x10e642d3,0x11da8b40)){InterlockedExchange(&Error,2);return false;}
    const DWORD attributes=GetFileAttributesW(accountOutput.c_str());
    if(accountOutput.empty()||accountOutput.size()>240||attributes==INVALID_FILE_ATTRIBUTES||!(attributes&FILE_ATTRIBUTE_DIRECTORY)||(attributes&FILE_ATTRIBUTE_REPARSE_POINT)){
        InterlockedExchange(&Error,4);return false;
    }
    if(!MutexReady){
        if(!InitializeCriticalSectionAndSpinCount(&PolicyMutex,0)){InterlockedExchange(&Error,4);return false;}
        MutexReady=true;
    }
    Directory=accountOutput;
    for(int i=0;i<2;++i)if(!Load(i)){InterlockedExchange(&Error,4);return false;}
    Facade=reinterpret_cast<FacadeFn>(base+Rva(0x10e642d0));State=reinterpret_cast<StateFn>(base+Rva(0x10e62d50));
    Handle=reinterpret_cast<HandleFn>(base+Rva(0x10ce44f0));Ready=reinterpret_cast<ReadyFn>(base+Rva(0x10e62db0));
    const unsigned targets[]{0x10ce4680,0x11175f90,0x109f6ee0,0x109d4e40,0x10ccfb60,0x10cd0da0};
    void* replacements[]{reinterpret_cast<void*>(Get),reinterpret_cast<void*>(Restore),reinterpret_cast<void*>(Select),reinterpret_cast<void*>(Complete),reinterpret_cast<void*>(Construct),reinterpret_cast<void*>(SettingsApplied)};
    void** originals[]{reinterpret_cast<void**>(&OriginalGet),reinterpret_cast<void**>(&OriginalRestore),reinterpret_cast<void**>(&OriginalSelect),reinterpret_cast<void**>(&OriginalCompletion),reinterpret_cast<void**>(&OriginalCtor),reinterpret_cast<void**>(&OriginalApplied)};
    unsigned created=0;bool good=true;
    for(;created<6;++created)if(MH_CreateHook(base+Rva(targets[created]),replacements[created],originals[created])!=MH_OK){good=false;break;}
    if(good)for(unsigned i=0;i<6;++i)if(MH_EnableHook(base+Rva(targets[i]))!=MH_OK){good=false;break;}
    if(good)for(unsigned i=0;i<6;++i){BYTE* at=base+Rva(targets[i]);LONG displacement=0;memcpy(&displacement,at+1,4);if(at[0]!=0xe9||at+5+displacement!=replacements[i]){good=false;break;}}
    if(!good){for(unsigned i=0;i<created;++i){MH_DisableHook(base+Rva(targets[i]));MH_RemoveHook(base+Rva(targets[i]));}InterlockedExchange(&Error,3);return false;}
    Installed=true;Verified=true;InterlockedExchange(&Error,0);return true;
}
} // namespace NativePecDefaults
