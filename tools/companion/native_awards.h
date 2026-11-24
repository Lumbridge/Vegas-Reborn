#pragma once
// Vegas 1 client: PLAYER INFO awards (ribbons, medals, badges, trophies).
// Include after MinHook.h in the x86 client TU. Output must be the companion's
// persistent, account-scoped output directory (as for native_pec_defaults.h).
//
// The PC build keeps the original award manager, its 36-award factory and every
// award's detection logic, but nothing on PC ever creates the award lists: the
// only creation paths are the profile container's award block (reset 0x10d20a50
// and load/save 0x10d22ad0), and on PC the container's block serializers and
// version check (0x10d21bd0, 0x10d22ad0, 0x10d22b00, 0x10d20be0) have no callers.
// The PC profile file R6_Profile<name> holds only options (save 0x11175f60,
// load 0x11175f90). The unlock commit at manager vtable +8 is a stub
// (0x10de4540: mov eax,1; ret 4), so an award can never become unlocked.
//
// This adapter keeps the original code paths and supplies only what PC lacks:
//  - the award manager getter 0x10d18710 creates the local profile's list with
//    the original Serialize 0x10d193d0 (from a companion file) or the original
//    ResetForList 0x10d193b0, the same calls the award block makes;
//  - manager vtable +8 marks the award unlocked (award+8 = 1) after the original
//    Unlock 0x10d18900 checks, then saves;
//  - award state is written with the original Serialize into a small archive
//    that implements only the FArchive members those serializers use.
#include <windows.h>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <string>
#include "MinHook.h"

namespace NativeAwards {
static_assert(sizeof(void*)==4,"Original PC client is x86");
static BYTE* Image=nullptr;
static bool Installed=false,Verified=false;
static volatile LONG Error=0,Created=0,Loaded=0,Saved=0,Unlocked=0,Rejected=0;
static std::wstring Directory;
static constexpr unsigned Rva(unsigned va){return va-0x10900000u;}
static constexpr unsigned GetterVa=0x10d18710,UnlockVa=0x10d18900,CreateVa=0x10d19320,ResetVa=0x10d193b0,SerializeVa=0x10d193d0;
static constexpr unsigned ManagerTableVa=0x11f6b460,CommitSlotVa=0x11f6b468,CommitStubVa=0x10de4540;
static constexpr unsigned GameVa=0x123c1a28,ManagerVa=0x123fcfe0,VersionVa=0x122ecea8;
static constexpr unsigned ByteOrderThunkVa=0x109016cc,ByteOrderVa=0x10921260;
static constexpr int AwardCount=0x24,MaxLists=4;
static constexpr unsigned MaxPayload=1024,HeaderSize=12,TrailerSize=4;
// Award vtables built by factory 0x10de57d0 (index 0..0x23), and the per-award
// serializers at vtable +0x24 that the original Serialize calls.
static constexpr unsigned AwardTables[AwardCount]{
    0x11f6b478,0x11f6b4a0,0x11f6b4c8,0x11f6b4f0,0x11f6b518,0x11f6b540,0x11f6b568,0x11f6b590,0x11f6b5b8,
    0x11f6b5e0,0x11f6b608,0x11f6b630,0x11f6b658,0x11f6b680,0x11f6b6a8,0x11f6b6d0,0x11f6b6f8,0x11f6b720,
    0x11f6b748,0x11f6b770,0x11f6b798,0x11f6b7c0,0x11f6b7e8,0x11f6b810,0x11f6b838,0x11f6b860,0x11f6b888,
    0x11f6b8b0,0x11f6b8d8,0x11f6b900,0x11f6b928,0x11f6b950,0x11f6b978,0x11f6b9a0,0x11f6b9c8,0x11f6b9f0};
static constexpr unsigned AwardSerializers[]{
    0x10d248b0,0x10debae0,0x10debb00,0x10debb20,0x10debb40,0x10debb60,0x10debb80,0x10debba0,0x10debbc0,
    0x10debbe0,0x10debc00,0x10debc20};

using GetterFn=BYTE*(__cdecl*)();
using ResetFn=void(__thiscall*)(void*,int);
using SerializeFn=int(__thiscall*)(void*,void*,int);
static GetterFn OriginalGetter=nullptr;
static ResetFn Reset=nullptr;
static SerializeFn Serialize=nullptr;

static CRITICAL_SECTION Mutex;
static bool MutexReady=false;
static __declspec(thread) unsigned Depth=0;
static BYTE* ReadyManager=nullptr;
static int ReadyList=-1;
static bool SaveBlocked=false;
static BYTE LastSaved[MaxPayload];
static unsigned LastSavedSize=0;
static ULONGLONG NextCheck=0;
static constexpr ULONGLONG CheckInterval=2000;

static const char* LastError(){
    switch(InterlockedCompareExchange(&Error,0,0)){
        case 0:return "none";case 1:return "clientIdentityMismatch";case 2:return "nativeSignatureMismatch";
        case 3:return "hookInstallationFailed";case 4:return "awardStorageUnavailable";
        case 5:return "awardWriteFailed";case 6:return "awardDataRejected";case 7:return "awardCreationFailed";
        default:return "unknown";
    }
}
static bool Readable(const void* pointer,size_t size,bool write=false){
    MEMORY_BASIC_INFORMATION m{};
    if(!pointer||!size||!VirtualQuery(pointer,&m,sizeof(m))||m.State!=MEM_COMMIT||
       (m.Protect&(PAGE_GUARD|PAGE_NOACCESS)))return false;
    const auto at=reinterpret_cast<uintptr_t>(pointer),begin=reinterpret_cast<uintptr_t>(m.BaseAddress);
    if(at<begin||at-begin>m.RegionSize||size>m.RegionSize-(at-begin))return false;
    return !write||(m.Protect&(PAGE_READWRITE|PAGE_WRITECOPY|PAGE_EXECUTE_READWRITE|PAGE_EXECUTE_WRITECOPY));
}
static BYTE* Pointer(const void* at){return Readable(at,4)?*reinterpret_cast<BYTE* const*>(at):nullptr;}
template<class T> static T At(const BYTE* object,unsigned offset){T value;memcpy(&value,object+offset,sizeof(value));return value;}

// Minimal FArchive for the original award serializers. They read only
// +0x14 (ArIsLoading, Serialize 0x10d19404) and +0x40 (ForceByteSwapping,
// ByteOrderSerialize 0x10921263), and call only vtable +4 Serialize(void*,int).
struct Archive {
    BYTE raw[0x80];
    BYTE* data;unsigned capacity,size,at;bool loading,failed;
};
static_assert(offsetof(Archive,data)>=0x80,"FArchive fields stay in raw");
static void __fastcall ArchiveSerialize(Archive* self,void*,void* value,int length){
    if(!value||length<0||unsigned(length)>MaxPayload){self->failed=true;return;}
    if(self->loading){
        if(unsigned(length)>self->size-self->at){memset(value,0,size_t(length));self->at=self->size;self->failed=true;return;}
        memcpy(value,self->data+self->at,size_t(length));self->at+=unsigned(length);
    }else{
        if(unsigned(length)>self->capacity-self->size){self->failed=true;return;}
        memcpy(self->data+self->size,value,size_t(length));self->size+=unsigned(length);
    }
}
static void __fastcall ArchiveUnexpected(Archive* self,void*){self->failed=true;}
static void* ArchiveTable[16]{};
static void Open(Archive& archive,BYTE* data,unsigned capacity,unsigned size,bool loading){
    memset(&archive,0,sizeof(archive));
    if(!ArchiveTable[0])for(auto& slot:ArchiveTable)slot=reinterpret_cast<void*>(ArchiveUnexpected);
    ArchiveTable[1]=reinterpret_cast<void*>(ArchiveSerialize);
    void** table=ArchiveTable;memcpy(archive.raw,&table,4);
    const int flag=loading?1:0;memcpy(archive.raw+0x14,&flag,4);
    archive.data=data;archive.capacity=capacity;archive.size=size;archive.loading=loading;
}

static uint32_t Checksum(const BYTE* data,unsigned size){
    uint32_t value=0xffffffffu;
    for(unsigned i=0;i<size;++i){value^=data[i];for(unsigned n=0;n<8;++n)value=(value>>1)^((value&1)?0xedb88320u:0u);}
    return ~value;
}
static std::wstring Path(){return Directory+L"\\player-awards-v1.dat";}
enum class FileState{Missing,Valid,Invalid,Unreadable};
static FileState ReadAwards(BYTE* payload,unsigned& size){
    size=0;const std::wstring path=Path();const DWORD attributes=GetFileAttributesW(path.c_str());
    if(attributes==INVALID_FILE_ATTRIBUTES){
        const DWORD error=GetLastError();
        return error==ERROR_FILE_NOT_FOUND||error==ERROR_PATH_NOT_FOUND?FileState::Missing:FileState::Unreadable;
    }
    if(attributes&(FILE_ATTRIBUTE_DIRECTORY|FILE_ATTRIBUTE_REPARSE_POINT))return FileState::Unreadable;
    HANDLE file=CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
    if(file==INVALID_HANDLE_VALUE)return FileState::Unreadable;
    BYTE bytes[HeaderSize+MaxPayload+TrailerSize+1];DWORD count=0;
    const bool read=ReadFile(file,bytes,sizeof(bytes),&count,nullptr)!=FALSE;CloseHandle(file);
    if(!read)return FileState::Unreadable;
    if(count<HeaderSize+TrailerSize||memcmp(bytes,"VRAW",4)||bytes[4]!=1||bytes[6]||bytes[7])return FileState::Invalid;
    const uint32_t length=At<uint32_t>(bytes,8);
    if(length<4||length>MaxPayload||count!=HeaderSize+length+TrailerSize||
       At<uint32_t>(bytes,HeaderSize+length)!=Checksum(bytes+HeaderSize,length))return FileState::Invalid;
    memcpy(payload,bytes+HeaderSize,length);size=length;return FileState::Valid;
}
static bool WriteAwards(const BYTE* payload,unsigned size,int list){
    const std::wstring target=Path();
    const DWORD attributes=GetFileAttributesW(target.c_str());
    if(attributes!=INVALID_FILE_ATTRIBUTES&&(attributes&(FILE_ATTRIBUTE_DIRECTORY|FILE_ATTRIBUTE_REPARSE_POINT)))return false;
    const std::wstring temporary=target+L".tmp-"+std::to_wstring(GetCurrentProcessId());
    HANDLE file=CreateFileW(temporary.c_str(),GENERIC_WRITE,0,nullptr,CREATE_ALWAYS,FILE_ATTRIBUTE_TEMPORARY|FILE_FLAG_WRITE_THROUGH,nullptr);
    if(file==INVALID_HANDLE_VALUE)return false;
    BYTE bytes[HeaderSize+MaxPayload+TrailerSize]{'V','R','A','W',1,BYTE(list),0,0};
    memcpy(bytes+8,&size,4);memcpy(bytes+HeaderSize,payload,size);
    const uint32_t sum=Checksum(payload,size);memcpy(bytes+HeaderSize+size,&sum,4);
    const DWORD total=HeaderSize+size+TrailerSize;DWORD count=0;
    bool good=WriteFile(file,bytes,total,&count,nullptr)&&count==total&&FlushFileBuffers(file);
    if(!CloseHandle(file))good=false;
    if(good)good=MoveFileExW(temporary.c_str(),target.c_str(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH)!=FALSE;
    if(!good)DeleteFileW(temporary.c_str());
    return good;
}

static bool ManagerValid(BYTE* manager){
    if(!Readable(manager,0x24,true)||Pointer(manager)!=Image+Rva(ManagerTableVa))return false;
    const int lists=At<int>(manager,0x1c);
    return lists>=1&&lists<=MaxLists&&Readable(Pointer(manager+0x18),size_t(lists)*12,true);
}
static int ListSize(BYTE* manager,int list){return At<int>(At<BYTE*>(manager,0x18)+list*12,4);}
// The local profile index the original profile save/load use for options
// (0x11175f60 and 0x11175f95 read [[game]+0x88]+0x4c). The award block passes
// the same profile index as the award list.
static int LocalList(BYTE* manager){
    BYTE* game=Pointer(Image+Rva(GameVa));
    BYTE* options=game?Pointer(game+0x88):nullptr;
    if(!Readable(options,0x50))return -1;
    const int index=At<int>(options,0x4c);
    return index>=0&&index<At<int>(manager,0x1c)?index:-1;
}
// Original Serialize in save mode. Returns false without touching the file
// when the original reports failure or the archive overflows.
static bool Capture(BYTE* manager,int list,BYTE* payload,unsigned& size){
    Archive archive;Open(archive,payload,MaxPayload,0,false);
    const int result=Serialize(manager,&archive,list);
    size=archive.size;
    return result&&!archive.failed&&size>=4;
}
static bool Persist(BYTE* manager,int list,bool force){
    if(SaveBlocked)return false;
    BYTE payload[MaxPayload];unsigned size=0;
    if(!Capture(manager,list,payload,size)){InterlockedExchange(&Error,5);return false;}
    if(!force&&size==LastSavedSize&&!memcmp(payload,LastSaved,size))return true;
    if(!WriteAwards(payload,size,list)){InterlockedExchange(&Error,5);return false;}
    memcpy(LastSaved,payload,size);LastSavedSize=size;InterlockedIncrement(&Saved);return true;
}
static void Backup(){
    const std::wstring path=Path();
    MoveFileExW(path.c_str(),(path+L".rejected").c_str(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH);
}
// Create the list once per manager. Never parse data that was not written by
// this adapter: a missing, unreadable or rejected file creates fresh awards
// through the original ResetForList and does not reuse partially read state.
static void Establish(BYTE* manager,int list){
    BYTE payload[MaxPayload];unsigned size=0;
    const FileState state=ReadAwards(payload,size);
    bool loaded=false;
    if(state==FileState::Valid){
        Archive archive;Open(archive,payload,size,size,true);
        const int result=Serialize(manager,&archive,list);
        loaded=result&&!archive.failed&&archive.at==archive.size&&ListSize(manager,list)==AwardCount;
        if(loaded){memcpy(LastSaved,payload,size);LastSavedSize=size;InterlockedIncrement(&Loaded);}
    }
    if(!loaded){
        if(state==FileState::Invalid||state==FileState::Valid){Backup();InterlockedIncrement(&Rejected);InterlockedExchange(&Error,6);}
        // Do not overwrite a file this session could not read.
        if(state==FileState::Unreadable){SaveBlocked=true;InterlockedExchange(&Error,4);}
        Reset(manager,list);
        if(ListSize(manager,list)!=AwardCount){InterlockedExchange(&Error,7);return;}
        InterlockedIncrement(&Created);LastSavedSize=0;
    }
    ReadyManager=manager;ReadyList=list;NextCheck=GetTickCount64()+CheckInterval;
}
struct Guard {
    bool owned=false;
    Guard(){if(Depth++||!MutexReady)return;EnterCriticalSection(&Mutex);owned=true;}
    ~Guard(){if(owned)LeaveCriticalSection(&Mutex);--Depth;}
    Guard(const Guard&)=delete;Guard& operator=(const Guard&)=delete;
};
static void Ensure(BYTE* manager){
    Guard guard;if(!guard.owned)return;
    if(manager&&manager==ReadyManager){
        // Progress counters change without an unlock. Save them when changed,
        // at most once per interval, from the same game-thread call sites.
        if(ListSize(manager,ReadyList)!=AwardCount)return;
        const ULONGLONG now=GetTickCount64();
        if(now>=NextCheck){NextCheck=now+CheckInterval;Persist(manager,ReadyList,false);}
        return;
    }
    if(!ManagerValid(manager))return;
    const int list=LocalList(manager);if(list<0)return;
    if(ListSize(manager,list)!=0){
        // Created by original code: adopt it rather than replace it.
        ReadyManager=manager;ReadyList=list;NextCheck=GetTickCount64()+CheckInterval;return;
    }
    Establish(manager,list);
}
static BYTE* __cdecl Getter(){
    BYTE* manager=OriginalGetter();
    if(manager&&!Depth)Ensure(manager);
    return manager;
}
// Manager vtable +8. The original Unlock 0x10d18900 calls it only for an
// existing, still-locked award of a mapped list while [0x1240e72c] is clear.
static int __fastcall Commit(BYTE* manager,void*,BYTE* award){
    if(Readable(award,0x10,true)&&Pointer(award+4)==manager&&!At<int>(award,8)){
        const int one=1;memcpy(award+8,&one,4);InterlockedIncrement(&Unlocked);
        Guard guard;
        if(guard.owned&&manager==ReadyManager&&At<int>(award,0xc)==ReadyList)Persist(manager,ReadyList,true);
    }
    return 1;
}

static bool Bytes(unsigned va,const BYTE* bytes,size_t count){return !memcmp(Image+Rva(va),bytes,count);}
static bool Address(unsigned va,unsigned expected){return At<BYTE*>(Image,Rva(va))==Image+Rva(expected);}
static bool CallTo(unsigned va,unsigned target){
    const BYTE* at=Image+Rva(va);if(at[0]!=0xe8&&at[0]!=0xe9)return false;
    return at+5+At<LONG>(at,1)==Image+Rva(target);
}
static bool SerializerSupported(unsigned va){
    // Each per-award serializer is a no-op, ByteOrderSerialize of a contiguous
    // award field, or one archive Serialize(field,1) call.
    static constexpr BYTE none[]{0xc2,0x04,0x00};
    static constexpr BYTE byte[]{0x8b,0xc1,0x8b,0x4c,0x24,0x04,0x8b,0x11,0x6a,0x01,0x83,0xc0,0x10,0x50,0xff,0x52,0x04,0xc2,0x04,0x00};
    static constexpr BYTE head[]{0x83,0xc1},middle[]{0x51,0x8b,0x4c,0x24,0x0c},tail[]{0xc2,0x04,0x00};
    if(Bytes(va,none,sizeof(none))||Bytes(va,byte,sizeof(byte)))return true;
    const BYTE* at=Image+Rva(va);
    return !memcmp(at,head,2)&&(at[2]==0x10||at[2]==0x18)&&at[3]==0x6a&&(at[4]==4||at[4]==8)&&
        !memcmp(at+5,middle,sizeof(middle))&&CallTo(va+10,ByteOrderThunkVa)&&!memcmp(at+15,tail,sizeof(tail));
}
// Image identity and exact code guards for every hooked or called routine.
// Sets Error 1 or 2 and returns false on mismatch.
static bool Matches(BYTE* base){
    Image=base;
    if(!Readable(base,0x1000)){InterlockedExchange(&Error,1);return false;}
    const auto dos=reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    if(dos->e_magic!=IMAGE_DOS_SIGNATURE||dos->e_lfanew<=0||dos->e_lfanew>0x800){InterlockedExchange(&Error,1);return false;}
    const auto pe=reinterpret_cast<const IMAGE_NT_HEADERS32*>(base+dos->e_lfanew);
    if(pe->Signature!=IMAGE_NT_SIGNATURE||pe->FileHeader.Machine!=IMAGE_FILE_MACHINE_I386||pe->FileHeader.TimeDateStamp!=0x47106421||
       pe->OptionalHeader.Magic!=IMAGE_NT_OPTIONAL_HDR32_MAGIC||pe->OptionalHeader.ImageBase!=0x10900000||
       pe->OptionalHeader.SizeOfImage!=0x1f56000){InterlockedExchange(&Error,1);return false;}
    // Getter: SEH prologue, then the singleton load at +0x16.
    const BYTE getter[]{0x6a,0xff,0x68};const BYTE getterBody[]{0x64,0xa1,0,0,0,0,0x50,0x64,0x89,0x25,0,0,0,0,0x51,0xa1};
    const BYTE getterTest[]{0x85,0xc0,0x75,0x4d};
    // Unlock: map lookup, bounds, award, [0x1240e72c], award+8, then vtable +8.
    const BYTE unlockHead[]{0xa1};const BYTE unlockMap[]{0x8b,0x90,0x88,0,0,0,0x8b,0x44,0x24,0x04,0x8b,0x44,0x82,0x40,0x85,0xc0,0x7c,0x3f,0x3b,0x41,0x1c,0x7d,0x3a};
    const BYTE unlockTail[]{0x83,0x78,0x08,0x00,0x75,0x09,0x8b,0x11,0x50,0xff,0x52,0x08,0xc2,0x08,0x00};
    // CreateAll: clear, factory for 0..0x23, append.
    const BYTE createHead[]{0x51,0x53,0x55,0x8b,0x6c,0x24,0x10,0x56,0x57,0x55,0x8b,0xf9};
    const BYTE createLoop[]{0x83,0xc3,0x01,0x83,0xfb,0x24,0x72,0xc8,0x5f,0x5e,0x5d,0x5b,0x59,0xc2,0x04,0x00};
    const BYTE reset[]{0x8b,0x44,0x24,0x04,0x85,0xc0,0x7c,0x0e,0x3b,0x41,0x1c,0x7d,0x09,0x89,0x44,0x24,0x04};
    const BYTE serialize[]{0x56,0x8b,0x74,0x24,0x0c,0x85,0xf6,0x57,0x8b,0xf9,0x7c,0x05,0x3b,0x77,0x1c,0x7c,0x07,0x5f,0x33,0xc0,0x5e,0xc2,0x08,0x00};
    const BYTE loading[]{0x83,0x7b,0x14,0x00,0x74,0x1b};
    const BYTE serializeTail[]{0x5b,0x5f,0xb8,0x01,0,0,0,0x5e,0xc2,0x08,0x00};
    const BYTE versionStore[]{0x8b,0xc1,0x8b,0x0d};const BYTE versionCompare[]{0x8b,0x09,0x33,0xc0,0x3b,0x0d};
    const BYTE versionSerialize[]{0x8b,0x44,0x24,0x08,0x8b,0x4c,0x24,0x04,0x6a,0x04,0x50};
    const BYTE awardSerialize[]{0x56,0x8b,0x74,0x24,0x0c,0x57,0x8b,0x7c,0x24,0x0c,0x6a,0x04,0x8d,0x46,0x08,0x50,0x8b,0xcf};
    const BYTE awardTail[]{0x8b,0x16,0x57,0x8b,0xce,0xff,0x52,0x24,0x5f,0x5e,0xc3};
    const BYTE byteOrder[]{0x56,0x8b,0xf1,0x83,0x7e,0x40,0x00,0x74,0x29};
    const BYTE byteOrderPlain[]{0x8b,0x44,0x24,0x0c,0x8b,0x4c,0x24,0x08,0x8b,0x16,0x50,0x51,0x8b,0xce,0xff,0x52,0x04};
    const BYTE commitStub[]{0xb8,0x01,0,0,0,0xc2,0x04,0x00};
    bool good=Bytes(GetterVa,getter,sizeof(getter))&&Address(GetterVa+3,0x11d9e4cb)&&Bytes(GetterVa+7,getterBody,sizeof(getterBody))&&
        Address(GetterVa+0x17,ManagerVa)&&Bytes(GetterVa+0x1b,getterTest,sizeof(getterTest))&&
        Bytes(UnlockVa,unlockHead,1)&&Address(UnlockVa+1,GameVa)&&Bytes(UnlockVa+5,unlockMap,sizeof(unlockMap))&&
        Address(UnlockVa+0x40,0x1240e72c)&&Bytes(UnlockVa+0x47,unlockTail,sizeof(unlockTail))&&
        Bytes(CreateVa,createHead,sizeof(createHead))&&CallTo(CreateVa+0xc,0x10d18830)&&CallTo(CreateVa+0x23,0x10de57d0)&&
        Bytes(CreateVa+0x50,createLoop,sizeof(createLoop))&&
        Bytes(ResetVa,reset,sizeof(reset))&&CallTo(ResetVa+0x11,CreateVa)&&
        Bytes(SerializeVa,serialize,sizeof(serialize))&&CallTo(SerializeVa+0x1d,0x10d247f0)&&CallTo(SerializeVa+0x2c,0x10d248e0)&&
        Bytes(SerializeVa+0x34,loading,sizeof(loading))&&CallTo(SerializeVa+0x3d,CreateVa)&&CallTo(SerializeVa+0x46,0x10d24800)&&
        CallTo(SerializeVa+0x70,0x10d24940)&&Bytes(SerializeVa+0x7d,serializeTail,sizeof(serializeTail))&&
        Bytes(0x10d247f0,versionStore,sizeof(versionStore))&&Address(0x10d247f4,VersionVa)&&
        Bytes(0x10d24800,versionCompare,sizeof(versionCompare))&&Address(0x10d24806,VersionVa)&&
        Bytes(0x10d248e0,versionSerialize,sizeof(versionSerialize))&&CallTo(0x10d248eb,ByteOrderThunkVa)&&
        Bytes(0x10d24940,awardSerialize,sizeof(awardSerialize))&&CallTo(0x10d24952,ByteOrderThunkVa)&&Bytes(0x10d24957,awardTail,sizeof(awardTail))&&
        CallTo(ByteOrderThunkVa,ByteOrderVa)&&Bytes(ByteOrderVa,byteOrder,sizeof(byteOrder))&&Bytes(0x10921292,byteOrderPlain,sizeof(byteOrderPlain))&&
        Address(ManagerTableVa,0x10de4550)&&Address(CommitSlotVa,CommitStubVa)&&Bytes(CommitStubVa,commitStub,sizeof(commitStub));
    for(unsigned table:AwardTables){
        if(!good)break;
        const unsigned serializer=unsigned(reinterpret_cast<uintptr_t>(At<BYTE*>(Image,Rva(table+0x24)))-reinterpret_cast<uintptr_t>(Image))+0x10900000u;
        bool known=false;for(unsigned va:AwardSerializers)known=known||va==serializer;
        good=known;
    }
    for(unsigned va:AwardSerializers)good=good&&SerializerSupported(va);
    if(!good){InterlockedExchange(&Error,2);return false;}
    return true;
}
static bool Install(BYTE* base,const std::wstring& accountOutput){
    if(Installed)return Verified;
    if(!Matches(base))return false;
    const DWORD attributes=GetFileAttributesW(accountOutput.c_str());
    if(accountOutput.empty()||accountOutput.size()>240||attributes==INVALID_FILE_ATTRIBUTES||
       !(attributes&FILE_ATTRIBUTE_DIRECTORY)||(attributes&FILE_ATTRIBUTE_REPARSE_POINT)){InterlockedExchange(&Error,4);return false;}
    if(!MutexReady){
        if(!InitializeCriticalSectionAndSpinCount(&Mutex,0)){InterlockedExchange(&Error,4);return false;}
        MutexReady=true;
    }
    Directory=accountOutput;
    Reset=reinterpret_cast<ResetFn>(base+Rva(ResetVa));Serialize=reinterpret_cast<SerializeFn>(base+Rva(SerializeVa));
    BYTE* target=base+Rva(GetterVa);
    if(MH_CreateHook(target,reinterpret_cast<void*>(Getter),reinterpret_cast<void**>(&OriginalGetter))!=MH_OK){InterlockedExchange(&Error,3);return false;}
    BYTE* slot=base+Rva(CommitSlotVa);DWORD protection=0;
    if(!VirtualProtect(slot,4,PAGE_READWRITE,&protection)){MH_RemoveHook(target);InterlockedExchange(&Error,3);return false;}
    void* commit=reinterpret_cast<void*>(Commit);memcpy(slot,&commit,4);
    DWORD ignored=0;VirtualProtect(slot,4,protection,&ignored);
    const bool good=MH_EnableHook(target)==MH_OK&&target[0]==0xe9&&target+5+At<LONG>(target,1)==reinterpret_cast<BYTE*>(Getter)&&
        At<void*>(slot,0)==commit;
    if(!good){
        MH_DisableHook(target);MH_RemoveHook(target);
        if(VirtualProtect(slot,4,PAGE_READWRITE,&protection)){void* stub=base+Rva(CommitStubVa);memcpy(slot,&stub,4);VirtualProtect(slot,4,protection,&ignored);}
        InterlockedExchange(&Error,3);return false;
    }
    Installed=true;Verified=true;InterlockedExchange(&Error,0);return true;
}
} // namespace NativeAwards
