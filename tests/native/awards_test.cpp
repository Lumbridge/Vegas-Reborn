#include "../../tools/companion/native_awards.h"
#include <cstdio>
#include <string>

// Synthetic manager, awards and original-shaped Reset/Serialize. They follow
// 0x10d193b0/0x10d193d0: version dword, then per award its +8 flag and the
// award's own field. Synthetic tests do not establish retail interoperability.
namespace X=NativeAwards;
struct Award {void* table;BYTE* manager;int unlocked;int list;int progress;};
static Award Pool[X::MaxLists][X::AwardCount];
static Award* Data[X::MaxLists][X::AwardCount];
static BYTE Manager[0x24],Lists[X::MaxLists*12],Game[0x90],Options[0x50];
static int Resets=0,Version=3;
template<class T> static void Put(BYTE* object,unsigned offset,T value){memcpy(object+offset,&value,sizeof(value));}
static int Count(int list){return X::At<int>(Lists,list*12+4);}
static void __fastcall FakeReset(BYTE* manager,void*,int list){
    if(list<0||list>=X::At<int>(manager,0x1c))return;
    ++Resets;
    for(int i=0;i<X::AwardCount;++i){Pool[list][i]=Award{nullptr,manager,0,list,0};Data[list][i]=&Pool[list][i];}
    Put(Lists,list*12,&Data[list][0]);Put(Lists,list*12+4,X::AwardCount);Put(Lists,list*12+8,X::AwardCount);
}
static void Field(void* archive,void* value,int length){
    auto table=*static_cast<void***>(archive);
    reinterpret_cast<void(__fastcall*)(void*,void*,void*,int)>(table[1])(archive,nullptr,value,length);
}
static int __fastcall FakeSerialize(BYTE* manager,void*,void* archive,int list){
    if(list<0||list>=X::At<int>(manager,0x1c))return 0;
    int version=Version;Field(archive,&version,4);
    if(X::At<int>(static_cast<BYTE*>(archive),0x14)){FakeReset(manager,nullptr,list);if(version!=Version)return 0;}
    for(int i=0;i<Count(list);++i){Field(archive,&Data[list][i]->unlocked,4);Field(archive,&Data[list][i]->progress,4);}
    return 1;
}
static void Forget(){
    X::ReadyManager=nullptr;X::ReadyList=-1;X::SaveBlocked=false;X::LastSavedSize=0;
    for(int list=0;list<X::MaxLists;++list)Put(Lists,list*12+4,0);
}
static bool Exists(const std::wstring& path){return GetFileAttributesW(path.c_str())!=INVALID_FILE_ATTRIBUTES;}
static void WriteRaw(const BYTE* bytes,DWORD size){
    HANDLE file=CreateFileW(X::Path().c_str(),GENERIC_WRITE,0,nullptr,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr);
    DWORD written=0;WriteFile(file,bytes,size,&written,nullptr);CloseHandle(file);
}
static void WritePayload(const BYTE* payload,uint32_t size){
    BYTE bytes[64+X::MaxPayload]{'V','R','A','W',1,0,0,0};memcpy(bytes+8,&size,4);memcpy(bytes+12,payload,size);
    const uint32_t sum=X::Checksum(payload,size);memcpy(bytes+12+size,&sum,4);WriteRaw(bytes,12+size+4);
}
int main(int argc,char** argv){
    if(argc<2)return 1;
    auto image=static_cast<BYTE*>(VirtualAlloc(nullptr,0x1c00000,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
    if(!image)return 1;
    bool ok=true;
    // Identity and code guards reject anything but the pinned client image.
    ok=ok&&!X::Matches(image)&&!strcmp(X::LastError(),"clientIdentityMismatch");
    auto dos=reinterpret_cast<IMAGE_DOS_HEADER*>(image);dos->e_magic=IMAGE_DOS_SIGNATURE;dos->e_lfanew=0x80;
    auto pe=reinterpret_cast<IMAGE_NT_HEADERS32*>(image+0x80);pe->Signature=IMAGE_NT_SIGNATURE;
    pe->FileHeader.Machine=IMAGE_FILE_MACHINE_I386;pe->FileHeader.TimeDateStamp=0x47106421;
    pe->OptionalHeader.Magic=IMAGE_NT_OPTIONAL_HDR32_MAGIC;pe->OptionalHeader.ImageBase=0x10900000;pe->OptionalHeader.SizeOfImage=0x1f56000;
    ok=ok&&!X::Matches(image)&&!strcmp(X::LastError(),"nativeSignatureMismatch");
    InterlockedExchange(&X::Error,0);

    X::Image=image;X::Reset=reinterpret_cast<X::ResetFn>(FakeReset);X::Serialize=reinterpret_cast<X::SerializeFn>(FakeSerialize);
    X::Directory=std::wstring(argv[1],argv[1]+strlen(argv[1]))+L"\\awards-test";
    CreateDirectoryW(X::Directory.c_str(),nullptr);
    DeleteFileW(X::Path().c_str());DeleteFileW((X::Path()+L".rejected").c_str());
    if(!InitializeCriticalSectionAndSpinCount(&X::Mutex,0))return 1;
    X::MutexReady=true;
    Put(Manager,0,image+X::Rva(X::ManagerTableVa));Put(Manager,0x18,&Lists[0]);Put(Manager,0x1c,4);
    Put(image,X::Rva(X::GameVa),&Game[0]);Put(Game,0x88,&Options[0]);Put(Options,0x4c,0);

    // No saved awards: the original reset creates all 36 for the local profile.
    X::Ensure(Manager);
    ok=ok&&Resets==1&&Count(0)==X::AwardCount&&Count(1)==0&&X::ReadyManager==Manager&&X::ReadyList==0&&X::Created==1;
    X::Ensure(Manager);ok=ok&&Resets==1; // Created once per manager.

    // Unlock commit marks the award and saves the original archive bytes.
    ok=ok&&X::Commit(Manager,nullptr,reinterpret_cast<BYTE*>(&Pool[0][3]))==1&&Pool[0][3].unlocked==1&&Exists(X::Path())&&X::Saved==1;
    // A foreign object is ignored; the return value matches the original stub.
    Award stranger{nullptr,Game,0,0,0};
    ok=ok&&X::Commit(Manager,nullptr,reinterpret_cast<BYTE*>(&stranger))==1&&stranger.unlocked==0&&X::Unlocked==1;

    // Progress fields change without an unlock and are saved when changed.
    Pool[0][5].progress=7;X::NextCheck=0;X::Ensure(Manager);ok=ok&&X::Saved==2;
    X::NextCheck=0;X::Ensure(Manager);ok=ok&&X::Saved==2; // Unchanged: no rewrite.

    // A new session loads the saved state through the original Serialize.
    Forget();Resets=0;X::Ensure(Manager);
    ok=ok&&X::Loaded==1&&Count(0)==X::AwardCount&&Pool[0][3].unlocked==1&&Pool[0][5].progress==7&&Pool[0][4].unlocked==0;

    // Corrupt file: never parsed; kept aside, fresh awards created.
    BYTE bad[]{'V','R','A','W',1,0,0,0,8,0,0,0,1,2,3,4,5,6,7,8,0,0,0,0};
    WriteRaw(bad,sizeof(bad));Forget();X::Ensure(Manager);
    ok=ok&&X::Rejected==1&&Exists(X::Path()+L".rejected")&&!Exists(X::Path())&&Count(0)==X::AwardCount&&Pool[0][3].unlocked==0&&
        !strcmp(X::LastError(),"awardDataRejected");

    // Intact file whose payload stops early (version only): the archive refuses
    // to read past it and the partially read list is replaced.
    const BYTE versionOnly[]{3,0,0,0};
    WritePayload(versionOnly,sizeof(versionOnly));Forget();X::Ensure(Manager);
    ok=ok&&X::Rejected==2&&Count(0)==X::AwardCount&&Pool[0][0].unlocked==0;
    // Extra trailing data is not silently accepted either.
    BYTE longer[4+X::AwardCount*8+4]{3};longer[4]=1;
    WritePayload(longer,sizeof(longer));Forget();X::Ensure(Manager);
    ok=ok&&X::Rejected==3&&Pool[0][0].unlocked==0;
    // Version mismatch reported by the original is rejected.
    BYTE other[4+X::AwardCount*8]{9};other[4]=1;
    WritePayload(other,sizeof(other));Forget();X::Ensure(Manager);
    ok=ok&&X::Rejected==4&&Pool[0][0].unlocked==0;
    // Exact payload loads.
    BYTE exact[4+X::AwardCount*8]{3};exact[4+8*2]=1;
    WritePayload(exact,sizeof(exact));Forget();X::Ensure(Manager);
    ok=ok&&X::Loaded==2&&Pool[0][2].unlocked==1&&Pool[0][0].unlocked==0;

    // Unready profile index or wrong manager object: nothing is created.
    Forget();Resets=0;Put(Options,0x4c,7);X::Ensure(Manager);ok=ok&&Resets==0&&X::ReadyManager==nullptr;
    Put(Options,0x4c,-1);X::Ensure(Manager);ok=ok&&Resets==0;
    Put(Options,0x4c,0);BYTE wrong[0x24]{};memcpy(wrong,Manager,sizeof(wrong));Put(wrong,0,image);
    X::Ensure(wrong);ok=ok&&Resets==0&&X::ReadyManager==nullptr;
    // A list the original already created is adopted, not replaced.
    FakeReset(Manager,nullptr,0);Pool[0][1].unlocked=1;Resets=0;X::Ensure(Manager);
    ok=ok&&Resets==0&&X::ReadyManager==Manager&&Pool[0][1].unlocked==1;
    // Second profile index uses its own list.
    Forget();Put(Options,0x4c,1);DeleteFileW(X::Path().c_str());X::Ensure(Manager);
    ok=ok&&X::ReadyList==1&&Count(1)==X::AwardCount&&Count(0)==0;
    Put(Options,0x4c,0);

    // Archive bounds.
    BYTE room[4]{};X::Archive archive;X::Open(archive,room,sizeof(room),0,false);
    int value=5;
    Field(&archive,&value,4);ok=ok&&!archive.failed&&archive.size==4;
    Field(&archive,&value,4);ok=ok&&archive.failed&&archive.size==4;
    X::Open(archive,room,sizeof(room),sizeof(room),true);int read=0;long long wide=-1;
    Field(&archive,&read,4);ok=ok&&read==5&&!archive.failed;
    Field(&archive,&wide,8);ok=ok&&archive.failed&&wide==0;

    DeleteFileW(X::Path().c_str());DeleteFileW((X::Path()+L".rejected").c_str());RemoveDirectoryW(X::Directory.c_str());
    VirtualFree(image,0,MEM_RELEASE);
    puts(ok?"PASS: awards created once, unlock and progress saved, saved state loaded, corrupt, short, long and mismatched files rejected without reuse, unready profiles ignored":"FAIL: awards");
    return ok?0:2;
}
