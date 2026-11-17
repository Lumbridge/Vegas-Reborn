#include "../../tools/companion/native_report_xp.h"
#include <cstdio>
#include <map>
#include <string>

namespace X=NativeReportXp;
static BYTE Profiles[2][0x20]{};
static unsigned Delta=0;static bool Succeed=true;static bool Missing=false;
static int Mode=8;
static int __fastcall GetMode(void*,void*){return Mode;}
static void __fastcall Populate(void*,void*){}
static std::map<void*,std::wstring> Labels;
static BYTE* __fastcall Resolve(void*,void*,unsigned handle){return Missing||handle>1?nullptr:Profiles[handle];}
static bool __fastcall Calculate(void*,void*,unsigned handle,void*){
    if(Succeed&&handle<2)*reinterpret_cast<unsigned*>(Profiles[handle]+0x10)+=Delta;
    return Succeed;
}
static void __fastcall Text(void* widget,void*,const wchar_t* value){
    auto& stored=Labels[widget];stored=value;
    auto bytes=static_cast<BYTE*>(widget);const wchar_t* data=stored.c_str();
    unsigned count=unsigned(stored.size()),capacity=count<8?8:count;
    memcpy(bytes+0x14,&data,4);memcpy(bytes+0x24,&count,4);memcpy(bytes+0x28,&capacity,4);
}
template<class T> static void Put(BYTE* object,unsigned offset,T value){memcpy(object+offset,&value,sizeof(value));}
int main(){
    auto image=static_cast<BYTE*>(VirtualAlloc(nullptr,0x1c00000,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
    if(!image)return 1;
    X::Image=image;X::Resolve=reinterpret_cast<X::ResolveFn>(Resolve);
    X::OriginalCalculate=reinterpret_cast<X::CalculateFn>(Calculate);X::Text=reinterpret_cast<X::TextFn>(Text);
    X::OriginalCoop=X::OriginalVersus=reinterpret_cast<X::PopulateFn>(Populate);
    BYTE manager[4]{},table[0x220]{};
    Put(manager,0,table+0);Put(table,0x21c,reinterpret_cast<void*>(GetMode));Put(image,0x1ac1a28,manager+0);
    BYTE sessions[0x5c]{},slot[4]{},wrapper[8]{},session[0x10]{};
    Put(image,0x1a81454,sessions+0);Put(sessions,0x54,slot+0);Put(sessions,0x58,slot+4);
    Put(slot,0,wrapper+0);Put(wrapper,4,session+0);Put(session,0xc,2u);
    BYTE controller[0x18]{},service[0x18]{},contexts[0x50]{},owner[0x10]{},persona[0x4c]{},report[0x160]{};
    BYTE label[0x50]{},value[0x50]{},score[0x50]{};
    Put(controller,8,service+0);Put(controller,0x10,contexts+0);Put(controller,0x14,2u);
    Put(image,0x1bf48bc,controller+0);Put(report,4,owner+0);Put(owner,0xc,persona+0);Put(persona,0x48,0u);
    Put(report,0xf8,label+0);Put(report,0x128,score+0);
    Put(report,0x154,label+0);Put(report,0x13c,value+0);Text(score,nullptr,L"500");
    Delta=250;
    bool ok=X::Calculate(controller,nullptr,0,nullptr);
    auto award=X::ForReport(report);
    ok=ok&&award.known&&award.earned==250&&X::At<unsigned>(Profiles[0],0x10)==250;
    X::CoopReport(report,award);
    ok=ok&&Labels[label]==L"SCORE / XP EARNED"&&Labels[score]==L"500 / 250 XP";
    X::CoopReport(report,award);ok=ok&&Labels[score]==L"500 / 250 XP";
    BYTE failure[0x50]{};Put(report,0xd4,failure+0);Text(failure,nullptr,L"YOUR TEAM WAS ELIMINATED...");
    X::CoopFailure(report,award);X::CoopFailure(report,award);
    ok=ok&&Labels[failure]==L"YOUR TEAM WAS ELIMINATED...\nXP EARNED: 250 XP";
    // Exercise both actual hook callbacks with native report identities. Keep
    // unrelated modes and incomplete reports unchanged; all PvP enums use XP.
    Put(report,0,image+0x15b5d48);Put(report,0xb8,1u);Put(report,0xc0,1u);Put(report,0xbc,1u);
    Text(score,nullptr,L"600");Mode=0;X::Coop(report,nullptr);ok=ok&&Labels[score]==L"600";
    Mode=8;X::Coop(report,nullptr);ok=ok&&Labels[score]==L"600 / 250 XP";
    Put(session,0xc,0u);Text(score,nullptr,L"600");X::Coop(report,nullptr);
    ok=ok&&Labels[score]==L"600"&&!X::ForReport(report).known;
    Put(session,0xc,1u);X::Coop(report,nullptr);ok=ok&&Labels[score]==L"600 / 250 XP"; // LAN too.
    Mode=7;X::Coop(report,nullptr);ok=ok&&Labels[score]==L"600 / 250 XP";
    Put(report,0xc0,0u);X::Coop(report,nullptr);
    ok=ok&&Labels[failure]==L"YOUR TEAM WAS ELIMINATED...\nXP EARNED: 250 XP";
    Put(report,0,image+0x15b6c44);
    for(Mode=1;Mode<=6;++Mode){X::Versus(report,nullptr);ok=ok&&Labels[value]==L"250 XP";}
    // A real zero award replaces the previous match's250, rather than accumulating it.
    Delta=0;X::Calculate(controller,nullptr,0,nullptr);X::VersusReport(report,X::ForReport(report));
    ok=ok&&Labels[label]==L"XP EARNED"&&Labels[value]==L"0 XP";
    Delta=60;X::Calculate(controller,nullptr,1,nullptr);
    ok=ok&&X::ForReport(report).earned==0;
    Put(persona,0x48,1u);ok=ok&&X::ForReport(report).earned==60;
    // Failed or missing-profile transactions must not display a stale or fabricated zero.
    Succeed=false;X::Calculate(controller,nullptr,1,nullptr);X::VersusReport(report,X::ForReport(report));
    ok=ok&&!X::ForReport(report).known&&Labels[value]==L"XP unavailable";
    Succeed=true;Missing=true;X::Calculate(controller,nullptr,1,nullptr);
    ok=ok&&!X::ForReport(report).known;
    Missing=false;Delta=1;Put(Profiles[1],0x10,0xffffffffu);X::Calculate(controller,nullptr,1,nullptr);
    ok=ok&&!X::ForReport(report).known; // Do not present an unsigned wrap as a huge gain.
    BYTE replacement[0x50]{};Put(controller,0x10,replacement+0);
    ok=ok&&!X::ForReport(report).known;
    Put(persona,0x48,16u);ok=ok&&!X::ForReport(report).known;
    VirtualFree(image,0,MEM_RELEASE);
    puts(ok?"PASS: actual award deltas, zero, profile isolation, failed transactions, report text and mission score preserved":"FAIL: report XP");
    return ok?0:2;
}
