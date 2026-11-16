#pragma once
// Original PC SDK producer. Only original service4 operations8/9 and row codecs.
#include <windows.h>
#include <cstdint>
#include <cstring>
#include <array>
#include <initializer_list>
#include "MinHook.h"
namespace NativeHostStatistics {
static_assert(sizeof(void*)==4,"Retail SDK is x86");
static BYTE* Base=nullptr;
static bool Dedicated=false,Installed=false,Verified=false,PersonaKnown=false;
static BYTE Persona=0;
static volatile LONG Registrations=0,Batches=0,Rejected=0;
static unsigned Error=0;
static constexpr uintptr_t Preferred=0x10900000;
static constexpr unsigned Rva(uintptr_t address){return unsigned(address-Preferred);}
struct Addresses {
    unsigned registry,networkName,getLobby,getStats,sessionGlobal,registration,poll,status,release,resultCtor,frame,calculate;
    unsigned statisticsName,descriptor,rowCtor,rowDtor,rowVtable,rowClear,baseDtor,writer,managerGlobal,managerVtable,isHost,setter;
};
static Addresses A{};
using VoidThis=void(__thiscall*)(void*);
using EngineTickFn=void(__thiscall*)(void*,float);
using SetterFn=void(__thiscall*)(void*,int,int,int);
using RegisterFn=void*(__thiscall*)(void*,uint64_t);
using PollFn=bool(__thiscall*)(void*,void*,void*);
using WriteFn=void*(__thiscall*)(void*,unsigned,uint64_t,uint64_t,unsigned,void**,unsigned);
static VoidThis OriginalFrame=nullptr;
static EngineTickFn OriginalEngineTick=nullptr;
static VoidThis OriginalCalculate=nullptr;
static SetterFn OriginalSetter=nullptr;
static bool CaptureActive=false;
static void* CaptureManager=nullptr;
static std::array<BYTE,256> Touched{};
struct Entry {uint64_t entity;int score;int team;};
static std::array<Entry,16> Pending{};
static unsigned PendingCount=0;
static uint64_t PendingSecurity=0;
static void* Task=nullptr;
static void* TaskOwner=nullptr;
static BYTE TaskTag=0;
static void* Sdk=nullptr;
static void* SessionData=nullptr;
static uint64_t Security=0,Arbitration=0;
static ULONGLONG PendingAt=0,RetryAt=0,NextPump=0;
static unsigned Attempts=0;
// 0=unregistered,1=registration pending,2=registered,3=write pending,4=failed.
static unsigned State=0;
static bool Readable(const void* pointer,size_t bytes){
    if(!pointer||!bytes)return false;
    MEMORY_BASIC_INFORMATION info{};
    auto p=reinterpret_cast<uintptr_t>(pointer);
    if(!VirtualQuery(pointer,&info,sizeof(info))||info.State!=MEM_COMMIT||
       (info.Protect&(PAGE_GUARD|PAGE_NOACCESS)))return false;
    auto end=reinterpret_cast<uintptr_t>(info.BaseAddress)+info.RegionSize;
    return end>=p&&bytes<=end-p;
}
template<class T> static T& At(void* p,unsigned offset){return *reinterpret_cast<T*>(static_cast<BYTE*>(p)+offset);}
template<class T> static T Fn(unsigned rva){return reinterpret_cast<T>(Base+rva);}
static bool Bytes(unsigned rva,std::initializer_list<BYTE> value){
    return !memcmp(Base+rva,value.begin(),value.size());
}
static void ObserveLogin(void* task){
    // Original TaskLogin calls the task+2c persona accessor at entry.
    if(Readable(task,0x30)){Persona=At<BYTE>(task,0x2c);PersonaKnown=true;NextPump=0;}
}
static bool ParseEntity(const void* string,uint64_t& value){
    // Original travel URL uses %I64i; negative signed64 values are valid bits.
    if(!Readable(string,12))return false;
    auto words=static_cast<const unsigned*>(string);
    unsigned count=words[1];auto text=reinterpret_cast<const wchar_t*>(words[0]);
    if(count<2||count>21||!Readable(text,count*sizeof(wchar_t))||text[count-1])return false;
    bool negative=text[0]==L'-';unsigned index=negative?1u:0u;
    if(index==count-1)return false;
    if(text[index]==L'0'&&index+1<count-1)return false;
    uint64_t magnitude=0,limit=negative?0x8000000000000000ull:0x7fffffffffffffffull;
    for(;index<count-1;++index){
        if(text[index]<L'0'||text[index]>L'9')return false;
        unsigned digit=unsigned(text[index]-L'0');
        if(magnitude>(limit-digit)/10)return false;
        magnitude=magnitude*10+digit;
    }
    value=negative?uint64_t(0)-magnitude:magnitude;
    return value!=0;
}
static void RejectBatch(unsigned reason){Error=reason;PendingCount=0;InterlockedIncrement(&Rejected);}
static void RegistrationFailed(unsigned reason){
    Error=reason;State=4;InterlockedIncrement(&Rejected);
    // At most three rapid retries, then a one-minute cooldown. Op8 is idempotent.
    ++Attempts;RetryAt=GetTickCount64()+(Attempts<3?ULONGLONG(Attempts)*2000:60000);
    if(Attempts>=3)Attempts=0;
}
static bool OwnTask(){
    // Original manager118874e0 has ten 20-hex-byte slots beginning at +4.
    // Native task tag+1c distinguishes reuse of the same slot.
    if(!Task||!Readable(Sdk,8)||At<void*>(Sdk,0)!=TaskOwner||!Readable(Task,0x20))return false;
    uintptr_t first=reinterpret_cast<uintptr_t>(TaskOwner)+4;
    uintptr_t current=reinterpret_cast<uintptr_t>(Task);
    return current>=first&&current-first<10*0x20&&(current-first)%0x20==0&&At<BYTE>(Task,0x1c)==TaskTag;
}
static bool KeepTask(void* task){
    Task=task;TaskOwner=At<void*>(Sdk,0);
    if(!Readable(task,0x20)){Task=nullptr;return false;}
    TaskTag=At<BYTE>(task,0x1c);
    if(!OwnTask()){Task=nullptr;return false;}return true;
}
static void ReleaseTask(){if(OwnTask())Fn<VoidThis>(A.release)(Task);Task=nullptr;}
static bool CurrentSession(void*& data,uint64_t& security,unsigned* role=nullptr){
    auto sessions=*reinterpret_cast<BYTE**>(Base+A.sessionGlobal);
    if(!Readable(sessions,0x60))return false;
    // The native task uses this same session list. Reject ambiguous multi-session state.
    auto begin=At<BYTE*>(sessions,0x54),end=At<BYTE*>(sessions,0x58);
    if(!begin||end!=begin+4||!Readable(begin,4))return false;
    auto wrapper=*reinterpret_cast<BYTE**>(begin);
    if(!Readable(wrapper,12))return false;
    if(role)*role=At<unsigned>(wrapper,8);
    data=At<void*>(wrapper,4);
    if(!Readable(data,0x50))return false;
    // Original Online create/join/copy paths preserve owner identifier type2;
    // LAN uses type1, even after an earlier successful Online login.
    if(At<unsigned>(data,0x0c)!=2)return false;
    security=At<uint64_t>(data,0x48);
    return security!=0;
}
static bool Context(void*& sdk,void*& data,uint64_t& security){
    if(!PersonaKnown||!CurrentSession(data,security))return false;
    auto registry=Fn<void*(__cdecl*)()>(A.registry)();
    if(!Readable(registry,4))return false;
    auto table=*reinterpret_cast<void***>(registry);
    if(!Readable(table,12))return false;
    auto network=reinterpret_cast<void*(__thiscall*)(void*,const char*)>(table[2])(
        registry,*reinterpret_cast<const char**>(Base+A.networkName));
    if(!Readable(network,0x14))return false;
    auto networkCore=At<void*>(network,0x10);
    if(!Readable(networkCore,0x10+unsigned(Persona)*4+4))return false;
    auto lobby=Fn<void*(__thiscall*)(void*,char)>(A.getLobby)(network,char(Persona));
    if(!Readable(lobby,0x48))return false;
    auto connection=At<void*>(lobby,0x44);
    if(!Readable(connection,4))return false;
    auto connectionTable=*reinterpret_cast<void***>(connection);
    if(!Readable(connectionTable,0x14)||
       reinterpret_cast<int(__thiscall*)(void*)>(connectionTable[4])(connection)!=2)return false;
    sdk=Fn<void*(__thiscall*)(void*)>(A.getStats)(lobby);
    return Readable(sdk,8)&&At<void*>(sdk,0)!=nullptr;
}
static bool AuthoritativeSession(void*& data,uint64_t& security){
    unsigned role=0;
    if(!CurrentSession(data,security,&role)||role!=1)return false;
    auto service=*reinterpret_cast<void**>(Base+A.sessionGlobal);
    return Readable(service,0x7c)&&Fn<BYTE(__thiscall*)(void*)>(A.isHost)(service)==1;
}
static bool Submit(){
    if(!PendingCount||PendingSecurity!=Security||!Arbitration)return false;
    void* activeData=nullptr;uint64_t activeSecurity=0;
    if(!AuthoritativeSession(activeData,activeSecurity)||activeData!=SessionData||activeSecurity!=Security)return false;
    auto registry=Fn<void*(__cdecl*)()>(A.registry)();
    auto table=*reinterpret_cast<void***>(registry);
    auto service=reinterpret_cast<void*(__thiscall*)(void*,const char*)>(table[2])(
        registry,*reinterpret_cast<const char**>(Base+A.statisticsName));
    if(!Readable(service,0x2c))return false;
    auto descriptor=Fn<void*(__thiscall*)(void*,unsigned)>(A.descriptor)(service,0xffffffffu);
    if(!Readable(descriptor,0x3c)||At<unsigned>(descriptor,0x10)!=0xffffffffu||
       At<unsigned>(descriptor,0x18)!=0)return false;
    auto columns=At<BYTE*>(descriptor,4),columnsEnd=At<BYTE*>(descriptor,8);
    if(!columns||columnsEnd!=columns+0x28||!Readable(columns,0x28)||
       At<unsigned>(columns,0)!=2||At<unsigned>(columns,4)!=62||
       At<unsigned>(columns,0x10)!=0x1000800a||At<unsigned>(columns,0x14)!=2||
       At<unsigned>(columns,0x18)!=63||At<unsigned>(columns,0x24)!=0x1000800b)return false;
    // Borrow descriptor pointers without invoking its owning destructor.
    // Original SDK serializes all rows synchronously before returning.
    alignas(8) BYTE localDescriptor[0x3c];memcpy(localDescriptor,descriptor,sizeof(localDescriptor));
    At<unsigned>(localDescriptor,0x18)=62; // Explicit repair: Relative_Score is base score.
    alignas(8) BYTE rows[16][0x74]{};void* pointers[16]{};unsigned constructed=0;
    bool valid=true;
    for(unsigned i=0;i<PendingCount;++i){
        Fn<void*(__thiscall*)(void*,void*)>(A.rowCtor)(rows[i],localDescriptor);
        ++constructed;pointers[i]=rows[i];
        if(At<void*>(rows[i],0)!=Base+A.rowVtable){valid=false;break;}
        auto variants=At<BYTE*>(rows[i],0x68),variantsEnd=At<BYTE*>(rows[i],0x6c);
        if(!variants||variantsEnd!=variants+24||!Readable(variants,24)||
           At<unsigned>(variants,8)!=1||At<unsigned>(variants,20)!=1){valid=false;break;}
        At<uint64_t>(rows[i],4)=Pending[i].entity;
        // Native variants have an eight-byte union followed by kind1.
        At<int64_t>(variants,0)=int64_t(Pending[i].score);
        At<int64_t>(variants,12)=int64_t(Pending[i].team);
    }
    void* handle=nullptr;
    if(valid)handle=Fn<WriteFn>(A.writer)(Sdk,0xffffffffu,Security,Arbitration,1,pointers,PendingCount);
    for(unsigned i=0;i<constructed;++i)Fn<VoidThis>(A.rowDtor)(rows[i]);
    if(!handle)return false;
    PendingCount=0;
    if(!KeepTask(handle))return false;
    State=3;InterlockedIncrement(&Batches);return true;
}
static void Pump(){
    void* sdk=nullptr;void* data=nullptr;uint64_t security=0;
    if(!Context(sdk,data,security))return;
    if(security!=Security||sdk!=Sdk||data!=SessionData){
        // Only cancel our own old SDK task when its transport still exists.
        if(Sdk==sdk&&Readable(Task,0x20))ReleaseTask();else Task=nullptr;
        Security=security;Sdk=sdk;SessionData=data;Arbitration=0;State=0;Attempts=0;RetryAt=0;
        if(PendingSecurity!=Security)PendingCount=0;
    }
    if(PendingCount&&GetTickCount64()-PendingAt>60000)RejectBatch(11);
    if(State==4){if(GetTickCount64()<RetryAt)return;State=0;}
    if(State==0){
        auto handle=Fn<RegisterFn>(A.registration)(Sdk,Security);
        if(!handle||!KeepTask(handle)){RegistrationFailed(1);return;}
        State=1;return;
    }
    if((State==1||State==3)&&Task){
        if(!OwnTask()){
            Task=nullptr;
            if(State==1)RegistrationFailed(12);else{State=2;Error=12;InterlockedIncrement(&Rejected);}
            return;
        }
        unsigned taskState=Fn<unsigned(__thiscall*)(void*)>(A.status)(Task);
        if(taskState==1)return;
        if(taskState!=2){
            ReleaseTask();
            if(State==1)RegistrationFailed(2);else{State=2;Error=2;InterlockedIncrement(&Rejected);}
            return;
        }
        alignas(8) BYTE result[16]{};
        void* output=nullptr;
        if(State==1){Fn<VoidThis>(A.resultCtor)(result);output=result;}
        bool ok=Fn<PollFn>(A.poll)(Sdk,Task,output);
        ReleaseTask();
        if(!ok){
            if(State==1)RegistrationFailed(3);else{State=2;Error=3;InterlockedIncrement(&Rejected);}
            return;
        }
        if(State==1){
            Arbitration=At<uint64_t>(result,8);
            if(!Arbitration){RegistrationFailed(4);return;}
            At<uint64_t>(SessionData,0x20)=Arbitration;
            InterlockedIncrement(&Registrations);Attempts=0;
        }
        State=2;
    }
    if(State==2&&PendingCount&&!Submit())RejectBatch(5);
}
static void* StatisticsManager(){
    auto root=*reinterpret_cast<BYTE**>(Base+A.managerGlobal);
    if(!Readable(root,0x88))return nullptr;
    auto manager=At<void*>(root,0x84);
    return Readable(manager,0x3c)&&At<void*>(manager,0)==Base+A.managerVtable?manager:nullptr;
}
static void Snapshot(){
    void* data=nullptr;uint64_t security=0;
    if(!AuthoritativeSession(data,security)){PendingCount=0;return;}
    auto manager=static_cast<BYTE*>(StatisticsManager());
    if(!manager||manager!=CaptureManager){RejectBatch(6);return;}
    auto entries=At<BYTE*>(manager,0x34);unsigned count=At<unsigned>(manager,0x38);
    PendingCount=0;
    if(count<2)return;
    if(count>256||!Readable(entries,size_t(count)*0x8c)){RejectBatch(7);return;}
    for(unsigned i=0;i<count;++i){
        if(!Touched[i])continue;
        auto entry=entries+i*0x8c;
        if(At<unsigned>(entry,0x88)&1)continue;
        if(PendingCount==Pending.size()){RejectBatch(7);return;}
        Entry value{};
        if(!ParseEntity(entry+0x10,value.entity)){RejectBatch(8);return;}
        value.score=At<int>(entry,0x5c);value.team=At<int>(entry,0x60);
        for(unsigned n=0;n<PendingCount;++n)if(Pending[n].entity==value.entity){RejectBatch(9);return;}
        Pending[PendingCount++]=value;
    }
    if(PendingCount<2){PendingCount=0;return;}
    PendingSecurity=security;PendingAt=GetTickCount64();NextPump=0;
}
static void PostTick(){
    // The original message pump may have just requested shutdown.
    if(*reinterpret_cast<unsigned*>(Base+(Dedicated?0x1b0d780:0x1b0e770)))return;
    ULONGLONG now=GetTickCount64();
    if(now>=NextPump){NextPump=now+100;Pump();}
}
static void __fastcall Frame(void* loop,void*){OriginalFrame(loop);PostTick();}
static void __fastcall EngineTick(void* engine,void*,float delta){OriginalEngineTick(engine,delta);PostTick();}
static void __fastcall SetValue(void* manager,void*,int index,int score,int team){
    OriginalSetter(manager,index,score,team);
    if(CaptureActive&&manager==CaptureManager&&index>0&&unsigned(index)<=Touched.size()&&
       Readable(manager,0x3c)&&unsigned(index)<=At<unsigned>(manager,0x38))Touched[unsigned(index)-1]=1;
}
static void __fastcall Calculate(void* game,void*){
    if(CaptureActive){OriginalCalculate(game);return;}
    Touched.fill(0);CaptureManager=StatisticsManager();CaptureActive=true;
    OriginalCalculate(game);
    CaptureActive=false;Snapshot();CaptureManager=nullptr;
}
static bool JumpTargets(unsigned rva,void* function){
    if(Base[rva]!=0xe9)return false;
    LONG displacement=0;memcpy(&displacement,Base+rva+1,4);
    return Base+rva+5+displacement==function;
}
static bool Calls(unsigned rva,unsigned target){
    if(Base[rva]!=0xe8)return false;
    LONG displacement=0;memcpy(&displacement,Base+rva+1,4);
    return Base+rva+5+displacement==Base+target;
}
static bool Install(BYTE* base,bool dedicated){
    Base=base;Dedicated=dedicated;
    auto dos=reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if(dos->e_magic!=IMAGE_DOS_SIGNATURE)return false;
    auto pe=reinterpret_cast<IMAGE_NT_HEADERS32*>(base+dos->e_lfanew);
    if(pe->Signature!=IMAGE_NT_SIGNATURE||pe->FileHeader.Machine!=IMAGE_FILE_MACHINE_I386||
       pe->OptionalHeader.ImageBase!=Preferred||
       pe->FileHeader.TimeDateStamp!=(dedicated?0x473ebf76u:0x47106421u)||
       pe->OptionalHeader.SizeOfImage!=(dedicated?0x1f53000u:0x1f56000u))return false;
    A=dedicated?Addresses{
        Rva(0x118bdc50),Rva(0x12364798),Rva(0x119128a0),Rva(0x1187e060),Rva(0x12380454),
        Rva(0x11882520),Rva(0x11882860),Rva(0x1187f270),Rva(0x1187f230),Rva(0x11961d10),Rva(0x10d95b20),Rva(0x111a1540),
        Rva(0x123647a4),Rva(0x11922560),Rva(0x11930580),Rva(0x11930610),Rva(0x1214131c),Rva(0x109de490),
        Rva(0x11889c60),Rva(0x11882600),Rva(0x123c0a28),Rva(0x11f51c20),Rva(0x11900250),Rva(0x10ce5000)}:
        Addresses{
        Rva(0x118beac0),Rva(0x12365798),Rva(0x11913720),Rva(0x1187eed0),Rva(0x12381454),
        Rva(0x11883390),Rva(0x118836d0),Rva(0x118800e0),Rva(0x118800a0),Rva(0x11962b90),Rva(0x10924720),Rva(0x111a23d0),
        Rva(0x123657a4),Rva(0x119233e0),Rva(0x11931400),Rva(0x11931490),Rva(0x1214271c),Rva(0x109dfc50),
        Rva(0x1188aad0),Rva(0x11883470),Rva(0x123c1a28),Rva(0x11f53040),Rva(0x119010c0),Rva(0x10ce6270)};
    if(!Bytes(A.registry,{0xa1})||!Bytes(A.getLobby,{0x8b,0x49,0x10,0xe9})||
       !Bytes(A.registration,{0x6a,0xff,0x68})||!Bytes(A.poll,{0x6a,0xff,0x68})||
       !Bytes(A.status,{0x83,0xec,0x10,0x56,0x8b,0xf1,0x8b,0x46,0x14,0x83,0xf8,0x01})||
       !Bytes(A.release,{0x56,0x8b,0xf1,0x8b,0x46,0x14,0x57,0x33,0xff}))return false;
    if(dedicated){
        if(!Bytes(A.frame,{0x55,0x8b,0xec,0x83,0xe4,0xf0,0x6a,0xff,0x68})||
           At<void*>(Base+A.frame,9)!=Base+Rva(0x11d9fdcc)||
           !Bytes(A.frame+13,{0x64,0xa1,0,0,0,0})||!Bytes(Rva(0x10d96d7f),{0xc2,0x04,0x00}))return false;
    }else if(!Bytes(A.frame,{0x55,0x8b,0xec,0x83,0xe4,0xf8,0x83,0xec,0x30,0x53,0x8b,0x1d}))return false;
    if(!Bytes(A.calculate,{0x83,0xec,0x4c,0x53,0x55,0x56,0x8b,0xf1})||
       !Bytes(A.isHost,{0x8a,0x41,0x78,0xc3})||
       !Bytes(A.setter,{0x8b,0x54,0x24,0x04,0x83,0xea,0x01,0x78})||
       !Bytes(A.setter+0x36,{0xc2,0x0c,0x00})||
       *reinterpret_cast<void**>(Base+A.managerVtable+0x10c)!=Base+A.setter||
       !Bytes(A.descriptor,{0x53,0x56,0x8b,0x71,0x24,0x57,0x8b,0x79,0x28})||
       !Bytes(A.rowCtor,{0x6a,0xff,0x68})||!Bytes(A.rowDtor,{0x6a,0xff,0x68})||
       !Bytes(A.rowDtor+0x4b,{0xc3})||!Calls(A.rowDtor+0x28,A.rowClear)||!Calls(A.rowDtor+0x37,A.baseDtor)||
       !Bytes(A.writer,{0x6a,0xff,0x68}))return false;
    void* hooks[]{base+A.frame,base+A.calculate,base+A.setter};
    void* replacements[]{dedicated?reinterpret_cast<void*>(EngineTick):reinterpret_cast<void*>(Frame),reinterpret_cast<void*>(Calculate),reinterpret_cast<void*>(SetValue)};
    void** originals[]{dedicated?reinterpret_cast<void**>(&OriginalEngineTick):reinterpret_cast<void**>(&OriginalFrame),reinterpret_cast<void**>(&OriginalCalculate),reinterpret_cast<void**>(&OriginalSetter)};
    unsigned created=0;
    auto cleanup=[&](){for(unsigned i=0;i<created;++i){MH_DisableHook(hooks[i]);MH_RemoveHook(hooks[i]);}};
    for(;created<3;++created){
        if(MH_CreateHook(hooks[created],replacements[created],originals[created])!=MH_OK){cleanup();return false;}
    }
    for(unsigned i=0;i<3;++i)if(MH_EnableHook(hooks[i])!=MH_OK){cleanup();return false;}
    Verified=JumpTargets(A.frame,replacements[0])&&JumpTargets(A.calculate,replacements[1])&&JumpTargets(A.setter,replacements[2]);
    if(!Verified){cleanup();return false;}
    Installed=true;return true;
}
}
