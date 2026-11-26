#pragma once

// Read the PEC delta across the original end-of-match award transaction.
// Presentation only: no reward calculation, profile writes or network changes.
#include <windows.h>
#include <cstdint>
#include <cstring>
#include <cwchar>
#include <array>
#include <string>
#include "MinHook.h"

namespace NativeReportXp {
static_assert(sizeof(void*)==4,"Original retail client is x86");
static BYTE* Image=nullptr;
static bool Verified=false;
static unsigned Error=0;
static constexpr unsigned CalculateRva=0x563b90,CoopRva=0x81940,VersusRva=0x85520;
using CalculateFn=bool(__thiscall*)(void*,unsigned,void*);
using PopulateFn=void(__thiscall*)(void*);
using ResolveFn=BYTE*(__thiscall*)(void*,unsigned);
using TextFn=void(__thiscall*)(void*,const wchar_t*);
static CalculateFn OriginalCalculate=nullptr;
static PopulateFn OriginalCoop=nullptr,OriginalVersus=nullptr;
static ResolveFn Resolve=nullptr;
static TextFn Text=nullptr;
struct Award {BYTE* context=nullptr;unsigned earned=0;bool known=false;BYTE* session=nullptr;};
static std::array<Award,16> Awards{};
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
template<class T> static T At(const void* pointer,unsigned offset){
    T value{};memcpy(&value,static_cast<const BYTE*>(pointer)+offset,sizeof(value));return value;
}
static BYTE* Session(){
    // Same original native session vector and owner-identifier discriminator
    // used by the existing statistics adapter: type1 LAN, type2 Online.
    BYTE* service=At<BYTE*>(Image,0x1a81454);
    if(!Readable(service,0x5c))return nullptr;
    BYTE* begin=At<BYTE*>(service,0x54);
    if(!Readable(begin,4)||At<BYTE*>(service,0x58)!=begin+4)return nullptr;
    BYTE* wrapper=At<BYTE*>(begin,0);
    if(!Readable(wrapper,8))return nullptr;
    BYTE* data=At<BYTE*>(wrapper,4);
    if(!Readable(data,0x10))return nullptr;
    unsigned ownerType=At<unsigned>(data,0xc);
    return ownerType==1||ownerType==2?data:nullptr;
}
static BYTE* Context(BYTE* controller,unsigned handle){
    if(handle>=Awards.size()||!Readable(controller,0x18)||handle>=At<unsigned>(controller,0x14))return nullptr;
    BYTE* contexts=At<BYTE*>(controller,0x10);
    if(!Readable(contexts,(size_t(handle)+1)*0x28))return nullptr;
    return contexts+handle*0x28;
}
static bool Experience(BYTE* controller,unsigned handle,unsigned& xp){
    if(!Context(controller,handle))return false;
    BYTE* service=At<BYTE*>(controller,8);
    if(!Readable(service,0x18))return false;
    BYTE* profile=Resolve(service,handle);
    if(!Readable(profile,0x14))return false;
    xp=At<unsigned>(profile,0x10);return true;
}
static bool __fastcall Calculate(void* self,void*,unsigned handle,void* provider){
    auto controller=static_cast<BYTE*>(self);
    if(handle<Awards.size())Awards[handle]={}; // Invalidate before every transaction, even failures.
    unsigned before=0,after=0;
    const bool haveBefore=Experience(controller,handle,before);
    const bool result=OriginalCalculate(self,handle,provider);
    if(handle<Awards.size()){
        BYTE* context=Context(controller,handle);
        const bool known=result&&haveBefore&&Experience(controller,handle,after)&&after>=before;
        Awards[handle]={context,known?after-before:0,known,Session()};
    }
    return result;
}
static Award ForReport(BYTE* report){
    if(!Readable(report,8))return {};
    BYTE* owner=At<BYTE*>(report,4);
    if(!Readable(owner,0x10))return {};
    BYTE* persona=At<BYTE*>(owner,0xc);
    if(!Readable(persona,0x4c))return {};
    unsigned handle=At<unsigned>(persona,0x48);
    if(handle>=Awards.size())return {};
    BYTE* controller=At<BYTE*>(Image,0x1bf48bc); // Existing singleton only; never allocate it.
    BYTE* context=Context(controller,handle);
    Award award=Awards[handle];
    return context&&context==award.context&&Session()&&Session()==award.session?award:Award{};
}
static bool Multiplayer(){
    BYTE* manager=At<BYTE*>(Image,0x1ac1a28);
    if(!Readable(manager,4))return false;
    BYTE* table=At<BYTE*>(manager,0);
    if(!Readable(table,0x220))return false;
    auto mode=At<int(__thiscall*)(void*)>(table,0x21c);
    // Recovered competitive enums1..6, coop story7 and coop Hunt8.
    int value=mode(manager);return value>=1&&value<=8&&Session();
}
static void Set(BYTE* report,unsigned offset,const wchar_t* value){
    BYTE* widget=At<BYTE*>(report,offset);
    if(Readable(widget,0x50))Text(widget,value);
}
static void Format(const Award& award,wchar_t (&text)[48]){
    if(award.known)swprintf_s(text,L"%u XP",award.earned);
    else wcscpy_s(text,L"XP unavailable"); // Missing observation is not a zero award.
}
static bool ReadText(BYTE* widget,std::wstring& value){
    // Original1095bf30 assigns the std::wstring at widget+10. Its original
    //1095bc40 uses +4 for inline/pointer data, +14 length and +18 capacity.
    if(!Readable(widget,0x2c))return false;
    unsigned count=At<unsigned>(widget,0x24),capacity=At<unsigned>(widget,0x28);
    if(count>1024||capacity<count)return false;
    auto text=capacity<8?reinterpret_cast<const wchar_t*>(widget+0x14):At<const wchar_t*>(widget,0x14);
    if(!Readable(text,(size_t(count)+1)*sizeof(wchar_t))||text[count])return false;
    value.assign(text,count);return true;
}
static void CoopReport(BYTE* report,const Award& award){
    // Resource loader10980070 intentionally does NOT bind a sixth middle
    // value(+110). Keep both totals in its existing, bound score value(+128).
    // In MP_RSMenus.mgb that element (L_ScoreValue) is a 75 px right-aligned
    // points cell sized for "= 8,000"; the companion's layout copy widens it
    // left to the label (ReportXpLayout) so "score / XP" fits on one line.
    wchar_t value[48]{};Format(award,value);
    std::wstring score;
    if(!ReadText(At<BYTE*>(report,0x128),score))return;
    auto suffix=score.find(L" / ");if(suffix!=std::wstring::npos)score.resize(suffix);
    if(score.empty()||score.size()>12||score.find_first_not_of(L"-0123456789")!=std::wstring::npos)return;
    score+=L" / ";score+=value;
    Set(report,0xf8,L"SCORE / XP");Set(report,0x128,score.c_str());
}
static void CoopFailure(BYTE* report,const Award& award){
    std::wstring description;
    if(!ReadText(At<BYTE*>(report,0xd4),description))return;
    auto suffix=description.find(L"\nXP EARNED: ");if(suffix!=std::wstring::npos)description.resize(suffix);
    wchar_t value[48]{};Format(award,value);
    description+=L"\nXP EARNED: ";description+=value;
    Set(report,0xd4,description.c_str());
}
static void VersusReport(BYTE* report,const Award& award){
    // The final results value is a 76 px right-aligned cell sized for "9999";
    // the label carries the unit so the value stays a short number.
    wchar_t value[16]{};
    if(award.known)swprintf_s(value,L"%u",award.earned);
    else wcscpy_s(value,L"N/A");
    Set(report,0x154,L"XP EARNED");Set(report,0x13c,value);
}
static void __fastcall Coop(void* self,void*){
    OriginalCoop(self);
    auto report=static_cast<BYTE*>(self);
    if(Readable(report,0x12c)&&At<BYTE*>(report,0)==Image+0x15b5d48&&
       At<unsigned>(report,0xb8)&&Multiplayer()){
        if(!At<unsigned>(report,0xc0))CoopFailure(report,ForReport(report));
        else if(At<unsigned>(report,0xbc))CoopReport(report,ForReport(report));
    }
}
static void __fastcall Versus(void* self,void*){
    OriginalVersus(self);
    auto report=static_cast<BYTE*>(self);
    if(Readable(report,0x15c)&&At<BYTE*>(report,0)==Image+0x15b6c44&&Multiplayer())
        VersusReport(report,ForReport(report));
}
static bool Install(BYTE* base){
    if(Verified)return true;
    Image=base;
    // Caller already checks the exact Steam client PE identity and executable
    // readiness. Check the original transaction, both report entries and helpers.
    const BYTE calculate[]{0x53,0x8b,0x5c,0x24,8,0x57,0x8b,0xf9,0x3b,0x5f,0x14,0x72,7,0x5f,0x32,0xc0,0x5b,0xc2,8,0};
    BYTE coop[]{0x64,0xa1,0,0,0,0,0x6a,0xff,0x68,0,0,0,0,0x50};
    BYTE versus[]{0x6a,0xff,0x64,0xa1,0,0,0,0,0x68,0,0,0,0,0x50};
    DWORD handler=DWORD(reinterpret_cast<uintptr_t>(base+0x14717c8));memcpy(coop+9,&handler,4);
    handler=DWORD(reinterpret_cast<uintptr_t>(base+0x1471da8));memcpy(versus+9,&handler,4);
    const BYTE resolve[]{0x8b,0x44,0x24,4,0x50,0xe8,0xd6,0xfb,0xff,0xff,0x8b,0x51,0x14};
    const BYTE text[]{0x56,0x57,0x8b,0x7c,0x24,0x0c,0x57,0x8b,0xf1};
    if(memcmp(base+CalculateRva,calculate,sizeof(calculate))||memcmp(base+CoopRva,coop,sizeof(coop))||
       memcmp(base+VersusRva,versus,sizeof(versus))||memcmp(base+0x5656c0,resolve,sizeof(resolve))||
       memcmp(base+0x5bf30,text,sizeof(text))){Error=1;return false;}
    Resolve=reinterpret_cast<ResolveFn>(base+0x5656c0);Text=reinterpret_cast<TextFn>(base+0x5bf30);
    struct Hook {unsigned rva;void* callback;void** original;};
    Hook hooks[]{{CalculateRva,reinterpret_cast<void*>(Calculate),reinterpret_cast<void**>(&OriginalCalculate)},
        {CoopRva,reinterpret_cast<void*>(Coop),reinterpret_cast<void**>(&OriginalCoop)},
        {VersusRva,reinterpret_cast<void*>(Versus),reinterpret_cast<void**>(&OriginalVersus)}};
    unsigned created=0;
    for(const auto& hook:hooks){
        if(MH_CreateHook(base+hook.rva,hook.callback,hook.original)!=MH_OK){Error=2;break;}
        ++created;
    }
    if(created==3){
        Verified=true;
        for(const auto& hook:hooks){
            if(MH_EnableHook(base+hook.rva)!=MH_OK){Verified=false;Error=3;break;}
            LONG delta=0;memcpy(&delta,base+hook.rva+1,4);
            if(base[hook.rva]!=0xe9||base+hook.rva+5+delta!=hook.callback){Verified=false;Error=3;break;}
        }
    }
    if(!Verified)for(unsigned i=0;i<created;++i){MH_DisableHook(base+hooks[i].rva);MH_RemoveHook(base+hooks[i].rva);}
    return Verified;
}
} // namespace NativeReportXp
