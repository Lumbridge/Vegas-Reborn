#pragma once

// Rainbow Six Vegas 1 PunkBuster checkboxes.
//
// Server: "ENABLE PUNKBUSTER SERVER" (Create Match).
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
// Every PunkBuster server start goes through 0x1045090 (PunkBuster command
// 0x75): the checkbox, and three automatic starts gated on the saved
// m_bPunkBusterSv flag (0x65376, 0xaf186 at the menu, 0x4e77a2). The adapter
// replaces that routine with one that does nothing, so the server stays off and
// the menu saves m_bPunkBusterSv=False with the off state. Turning the server
// off (0x10450f0) is unchanged.
//
// Client: the PunkBuster client checkbox in the match browser and Quick Match.
// - The match browser (two vtables sharing init 0xa6c80) binds names from
//   0x122e74c0 to menu +0xc at 0xa6c8f; CB_PBClientCustomMatch (17th, last)
//   is at +0x4c. Quick Match binds names from 0x122e7688 to +0xd4 at 0xb2a66;
//   CB_PBClientQuickMatch (14th, last) is at +0x108. Both are visible from
//   the menu file; no code shows them.
// - Their show methods (0xa76c0, 0xb5850; thiscall, int shown) turn the
//   client on when entering with the client off and a profile flag set.
// - The client routines share the PunkBuster instance at 0x125a9f10 +4:
//   0x1046a20 queries (command 0x71), 0x1046ae0 turns it on (0x75) and
//   0x1046b40 turns it off (0x76). Turning it on is also called by both
//   checkboxes (0xa8c2c, 0xb5c4e) and PunkBuster's own join path
//   (0x10487a3). Clicking a ticked checkbox queries and then turns it off.
// After each show method, the adapter hides that menu's checkbox the same way
// as the server checkbox and, as unticking it would, turns the client off if
// the query says it is on. The on routine does nothing, so the menus save
// m_bPunkBusterCl=False.
//
// Filter: the PUNKBUSTER match filter (Any/Yes/No).
// - The filters manager (R6MatchFiltersManager) init 0x952960 (thiscall,
//   mode) creates eight options in its array at +8; the PunkBuster option
//   (vtable 0x12093bf0, constructor 0x951670 at 0x952b8c) is element 6. In
//   mode 1 it then appends the filters shown on the page to the array at
//   +0x14 (count +0x18); element 6 is appended last (0x952efd).
// - The option's value +8 maps through 0x120935ec to -1 (Any), 0 or 1. Its
//   own load (vtable slot 14, 0x951760) reads the saved value from the
//   profile ([+0x38]+0x50); its save (slot 13, 0x951730) writes it back. The
//   search reads the value from the option.
// After init, the adapter drops the PunkBuster entry from the shown filters.
// The ranked access module hooks the same init and appends the game type
// filter after it; while that hook is installed, this module runs from it
// (NativeRankedAccess::AfterFilters) instead of hooking the init again. Its load sets the value to Any and saves that with the original
// save routine instead of reading the saved value.
//
// Column: the match list's PunkBuster column.
// - The match browser init (0xa6c80) requires the list columns named in the
//   table at 0x122e7514 (0xa6cfa); the last, I_PunkBuster, is column 8. It
//   loads the ic_PunkBuster image from MP_RSMenus.mgb into menu +0xa0.
// - The row fill sets column 8 to that image when the match reports
//   PunkBuster, or to none (0xa880a), with the list's set-cell-image routine
//   0xcf2540 (thiscall list, row, column, image; ret 0xc).
// The adapter passes no image from that call, so the column stays empty.
//
// Details: the PunkBuster entry in the selected match's details.
// - The details refresh (0xac020) fills the details list at menu +0x54: three
//   rows, each with a label/value pair in columns 0-1 and another in 2-3. The
//   third row's columns 0 and 1 get [ServerOptions] PunkBusterLabel and Yes
//   or No (0xac42c), through the list's set-cell-text routine 0xcf2e40
//   (thiscall list, row, column, text; ret 0xc), returning to 0xac470 and
//   0xac499.
// The adapter passes the game's empty string from those two calls instead.
#include <windows.h>
#include <intrin.h>
#include <cstdint>
#include <cstring>
#include "MinHook.h"
#include "native_ranked_access.h"

#pragma intrinsic(_ReturnAddress)

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

static constexpr unsigned ClientMenuCount=2;
static constexpr unsigned ClientShowRvas[ClientMenuCount]{0xa76c0,0xb5850};
static constexpr BYTE ClientShowPrologues[ClientMenuCount][10]{
    {0x83,0x7c,0x24,0x04,0x00,0x56,0x8b,0xf1,0x74,0x3c},
    {0x56,0x57,0x8b,0x7c,0x24,0x0c,0x6a,0xff,0x6a,0x00}};
static constexpr unsigned ClientCheckboxOffsets[ClientMenuCount]{0x4c,0x108};
// mov edi,table / lea esi,[menu+first]; then cmp edi,table end.
static constexpr unsigned ClientBindRvas[ClientMenuCount]{0xa6c8f,0xb2a66};
static constexpr BYTE ClientBindCodes[ClientMenuCount][11]{
    {0xbf,0xc0,0x74,0x2e,0x12,0x8d,0x75,0x0c,0,0,0},
    {0xbf,0x88,0x76,0x2e,0x12,0x8d,0xb3,0xd4,0x00,0x00,0x00}};
static constexpr unsigned ClientBindSizes[ClientMenuCount]{8,11};
static constexpr unsigned ClientBindEndRvas[ClientMenuCount]{0xa6cb9,0xb2a92};
static constexpr BYTE ClientBindEnds[ClientMenuCount][6]{
    {0x81,0xff,0x04,0x75,0x2e,0x12},{0x81,0xff,0xc0,0x76,0x2e,0x12}};
static constexpr unsigned ClientNameRvas[ClientMenuCount]{0x19e7500,0x19e76bc};
static constexpr const char* ClientNames[ClientMenuCount]{"CB_PBClientCustomMatch","CB_PBClientQuickMatch"};
static constexpr unsigned ClientQueryRva=0x1046a20,ClientOnRva=0x1046ae0,ClientOffRva=0x1046b40;
static constexpr BYTE ClientQueryCode[]{0xa1,0x10,0x9f,0x5a,0x12,0x85,0xc0,0x75,0x01,0xc3,0x56,0x8d,0x70,0x04};
static constexpr BYTE ClientToggleCode[]{0xa1,0x10,0x9f,0x5a,0x12,0x85,0xc0,0x74,0x47,0x56,0x8d,0x70,0x04};
// push 0x71/0x75/0x76: the PunkBuster command each client routine sends.
static constexpr unsigned ClientCommandOffset=0x43,ClientQueryCommandOffset=0x47;

static constexpr unsigned FilterInitRva=0x952960,FilterLoadRva=0x951760,FilterSaveRva=0x951730;
static constexpr unsigned FilterVtableRva=0x1793bf0,FilterSaveSlot=13,FilterLoadSlot=14;
static constexpr BYTE FilterInitCode[]{0x6a,0xff,0x64,0xa1,0x00,0x00,0x00,0x00,0x68,0x78,0xaa,0xde,0x11,0x50};
static constexpr BYTE FilterLoadCode[]{0x56,0x8b,0xf1,0x8b,0x4e,0x04,0x85,0xc9,0x74,0x21,0xe8,0xd1,0x09,0x00,0x00};
static constexpr BYTE FilterSaveCode[]{0x56,0x8b,0xf1,0x8b,0x4e,0x04,0x85,0xc9,0x74,0x1a,0xe8,0x01,0x0a,0x00,0x00};
// mov eax,[eax+0x38] / mov ecx,[esi+8] / mov [eax+0x50],ecx: the saved value.
static constexpr unsigned FilterSaveStoreRva=0x95174b;
static constexpr BYTE FilterSaveStore[]{0x8b,0x40,0x38,0x8b,0x4e,0x08,0x89,0x48,0x50};
// call constructor (element 6); mov [esi],vtable; the last append reads element 6 (+0x18).
static constexpr unsigned FilterCreateRva=0x952b8c,FilterVtableStoreRva=0x95169d,FilterLastAppendRva=0x952efd;
static constexpr BYTE FilterCreate[]{0xe8,0xdf,0xea,0xff,0xff};
static constexpr BYTE FilterVtableStore[]{0xc7,0x06,0xf0,0x3b,0x09,0x12};
static constexpr BYTE FilterLastAppend[]{0x8b,0x6e,0x18,0x8b,0x3f,0x8d,0x4d,0x01,0x83,0xc7,0x18};
static constexpr unsigned FilterShownMode=1,FilterShownList=0x14,FilterShownCount=0x18,FilterValue=8;

static constexpr unsigned CellImageRva=0xcf2540,PunkBusterCellRva=0xa880a,PunkBusterCellReturnRva=0xa8829;
static constexpr unsigned ColumnCheckRva=0xa6cfa,PunkBusterColumnNameRva=0x19e7534,PunkBusterImageNameRva=0x19e7510;
static constexpr BYTE CellImageCode[]{0x0f,0xbf,0x44,0x24,0x04,0x53,0x56,0x57,0x8d,0x3c,0x80,0x8b,0xf1,0x8b,0x46,0x74};
// cmp [match+0x18],0 / push the image at menu +0xa0 or 0 / push 8 / push row / call.
static constexpr BYTE PunkBusterCellCode[]{0x83,0x79,0x18,0x00,0x8b,0xce,0x74,0x0d,0x8b,0x54,0x24,0x10,0x8b,0x82,0xa0,0x00,0x00,0x00,
    0x50,0xeb,0x02,0x6a,0x00,0x6a,0x08,0x57,0xe8,0x17,0x9d,0xc4,0x00};
// The init's loop over the required columns, ending after I_PunkBuster.
static constexpr BYTE ColumnCheckCode[]{0xbe,0x14,0x75,0x2e,0x12,0x90,0x8b,0x16,0x8b,0x4d,0x00,0x52,0xe8,0x95,0xa3,0xc4,0x00,
    0x85,0xc0,0x0f,0x8c,0x9d,0x00,0x00,0x00,0x83,0xc6,0x04,0x81,0xfe,0x38,0x75,0x2e,0x12,0x7c,0xe2};
static constexpr unsigned CellTextRva=0xcf2e40,DetailsRva=0xac42c,DetailsLabelReturnRva=0xac470,DetailsValueReturnRva=0xac499;
static constexpr unsigned DetailsLabelNameRva=0x19e75b8,EmptyTextRva=0x15a09a0;
static constexpr BYTE CellTextCode[]{0x6a,0xff,0x68,0x38,0xa9,0xe0,0x11,0x64,0xa1,0x00,0x00,0x00,0x00,0x50,0x64,0x89};
// Load [ServerOptions] PunkBusterLabel, set column 0; set the Yes/No text in column 1.
static constexpr BYTE DetailsCode[]{0xa1,0x48,0x75,0x2e,0x12,0x8b,0x0d,0xb8,0x75,0x2e,0x12,0x8b,0x15,0xb4,0x75,0x2e,0x12,
    0x6a,0x00,0x6a,0x00,0x50,0x51,0x52,0x8d,0x44,0x24,0x40,0x50,0xe8,0xf2,0x61,0x44,0x00,0x83,0xc4,0x18,0x83,0x78,0x04,0x00,
    0xc6,0x44,0x24,0x40,0x0d,0x74,0x04,0x8b,0x00,0xeb,0x05,0xb8,0xa0,0x09,0xea,0x11,0x50,0x6a,0x00,0x55,0x8b,0xce,
    0xe8,0xd0,0x69,0xc4,0x00,0x8d,0x4c,0x24,0x2c,0xc6,0x44,0x24,0x40,0x0b,0xe8,0xa2,0x6c,0xf5,0xff,0x83,0x7c,0x24,0x18,0x00,
    0x8b,0x44,0x24,0x14,0x75,0x05,0xb8,0xa0,0x09,0xea,0x11,0x50,0x6a,0x01,0x55,0x8b,0xce,0xe8,0xa7,0x69,0xc4,0x00};

static constexpr unsigned HookCount=SetupCount+1+ClientMenuCount+1+2+1+1;

static BYTE* Image=nullptr;
static bool Verified=false;
static unsigned Error=0;
using SetupFn=void(__thiscall*)(void*);
using ClientShowFn=void(__thiscall*)(void*,int);
using ShowFn=void(__thiscall*)(void*,int);
using StartFn=void(__cdecl*)();
using ClientFn=int(__cdecl*)();
static SetupFn OriginalSetups[SetupCount]{};
static StartFn OriginalStart=nullptr;
static ClientShowFn OriginalClientShows[ClientMenuCount]{};
static ClientFn OriginalClientOn=nullptr;
using FilterInitFn=void(__thiscall*)(void*,int);
using FilterFn=void(__thiscall*)(void*);
static FilterInitFn OriginalFilterInit=nullptr;
static FilterFn OriginalFilterLoad=nullptr;
using CellImageFn=void(__thiscall*)(void*,int,int,void*);
static CellImageFn OriginalCellImage=nullptr;
using CellTextFn=void(__thiscall*)(void*,int,int,const wchar_t*);
static CellTextFn OriginalCellText=nullptr;

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
static void HideCheckbox(void* menu,unsigned offset){
    if(!Readable(static_cast<BYTE*>(menu)+offset,4))return;
    BYTE* checkbox=*reinterpret_cast<BYTE**>(static_cast<BYTE*>(menu)+offset);
    if(!Readable(checkbox,FlagsOffset+1))return;
    void** table=*reinterpret_cast<void***>(checkbox);
    if(!Readable(table+ShowSlot,4))return;
    checkbox[FlagsOffset]&=0xfe;
    reinterpret_cast<ShowFn>(table[ShowSlot])(checkbox,0);
}
template<unsigned Index> static void __fastcall Setup(void* menu,void*){
    OriginalSetups[Index](menu);
    HideCheckbox(menu,CheckboxOffset);
}
static void __cdecl KeepServerOff(){}
template<unsigned Index> static void __fastcall ShowClientMenu(void* menu,void*,int shown){
    OriginalClientShows[Index](menu,shown);
    if(!shown)return;
    HideCheckbox(menu,ClientCheckboxOffsets[Index]);
    if(reinterpret_cast<ClientFn>(Image+ClientQueryRva)())reinterpret_cast<ClientFn>(Image+ClientOffRva)();
}
static int __cdecl KeepClientOff(){return 0;}
// The init appends the PunkBuster option last, but the ranked access module
// can append the game type filter after it, so find it in the shown list.
static void RemoveShownFilter(void* manager,int mode){
    if(mode!=int(FilterShownMode))return;
    BYTE* const fields=static_cast<BYTE*>(manager);
    if(!Readable(fields+FilterShownList,8))return;
    void** const shown=*reinterpret_cast<void***>(fields+FilterShownList);
    int& count=*reinterpret_cast<int*>(fields+FilterShownCount);
    if(count<=0||count>32||!Readable(shown,size_t(count)*4))return;
    for(int i=count-1;i>=0;--i){
        if(!Readable(shown[i],4)||*static_cast<void**>(shown[i])!=Image+FilterVtableRva)continue;
        memmove(shown+i,shown+i+1,size_t(count-1-i)*4);
        --count;
        return;
    }
}
static void __fastcall InitFilters(void* manager,void*,int mode){
    OriginalFilterInit(manager,mode);
    RemoveShownFilter(manager,mode);
}
static void __fastcall LoadPunkBusterFilter(void* option,void*){
    // Any, saved back with the original save in place of the saved value.
    *reinterpret_cast<int*>(static_cast<BYTE*>(option)+FilterValue)=0;
    reinterpret_cast<FilterFn>(Image+FilterSaveRva)(option);
}
static void __fastcall SetCellImage(void* list,void*,int row,int column,void* image){
    if(_ReturnAddress()==Image+PunkBusterCellReturnRva)image=nullptr;
    OriginalCellImage(list,row,column,image);
}
static void __fastcall SetCellText(void* list,void*,int row,int column,const wchar_t* text){
    void* const caller=_ReturnAddress();
    if(caller==Image+DetailsLabelReturnRva||caller==Image+DetailsValueReturnRva)
        text=reinterpret_cast<const wchar_t*>(Image+EmptyTextRva);
    OriginalCellText(list,row,column,text);
}

static bool NameIs(BYTE* base,unsigned rva,const char* name){
    if(!Readable(base+rva,4))return false;
    const char* text=*reinterpret_cast<const char**>(base+rva);
    const size_t length=strlen(name)+1;
    return Readable(text,length)&&!memcmp(text,name,length);
}
// sharedInit: the ranked access hook already owns the filters init, so its
// prologue is that hook's jump; ranked access checked the same prologue first.
static bool Matches(BYTE* base,bool sharedInit){
    for(unsigned i=0;i<SetupCount;++i)if(memcmp(base+SetupRvas[i],SetupPrologues[i],sizeof(SetupPrologues[i])))return false;
    if(memcmp(base+BindRva,BindCode,sizeof(BindCode))||memcmp(base+HideRva,HideCode,sizeof(HideCode))||
       memcmp(base+StartRva,StartCode,sizeof(StartCode))||memcmp(base+StartCommandRva,StartCommand,sizeof(StartCommand)))return false;
    for(unsigned i=0;i<ClientMenuCount;++i){
        if(memcmp(base+ClientShowRvas[i],ClientShowPrologues[i],sizeof(ClientShowPrologues[i]))||
           memcmp(base+ClientBindRvas[i],ClientBindCodes[i],ClientBindSizes[i])||
           memcmp(base+ClientBindEndRvas[i],ClientBindEnds[i],sizeof(ClientBindEnds[i]))||
           !NameIs(base,ClientNameRvas[i],ClientNames[i]))return false;
    }
    if((sharedInit?!NativeRankedAccess::IsJumpTo(base+FilterInitRva,reinterpret_cast<void*>(&NativeRankedAccess::Filters))
                  :memcmp(base+FilterInitRva,FilterInitCode,sizeof(FilterInitCode))!=0)||
       memcmp(base+FilterLoadRva,FilterLoadCode,sizeof(FilterLoadCode))||
       memcmp(base+FilterSaveRva,FilterSaveCode,sizeof(FilterSaveCode))||
       memcmp(base+FilterSaveStoreRva,FilterSaveStore,sizeof(FilterSaveStore))||
       memcmp(base+FilterCreateRva,FilterCreate,sizeof(FilterCreate))||
       memcmp(base+FilterVtableStoreRva,FilterVtableStore,sizeof(FilterVtableStore))||
       memcmp(base+FilterLastAppendRva,FilterLastAppend,sizeof(FilterLastAppend)))return false;
    void* const* const filterTable=reinterpret_cast<void* const*>(base+FilterVtableRva);
    if(!Readable(filterTable,(FilterLoadSlot+1)*4)||filterTable[FilterSaveSlot]!=base+FilterSaveRva||
       filterTable[FilterLoadSlot]!=base+FilterLoadRva)return false;
    if(memcmp(base+CellImageRva,CellImageCode,sizeof(CellImageCode))||
       memcmp(base+PunkBusterCellRva,PunkBusterCellCode,sizeof(PunkBusterCellCode))||
       memcmp(base+ColumnCheckRva,ColumnCheckCode,sizeof(ColumnCheckCode))||
       !NameIs(base,PunkBusterColumnNameRva,"I_PunkBuster")||!NameIs(base,PunkBusterImageNameRva,"ic_PunkBuster"))return false;
    static constexpr wchar_t DetailsLabel[]=L"PunkBusterLabel";
    if(memcmp(base+CellTextRva,CellTextCode,sizeof(CellTextCode))||memcmp(base+DetailsRva,DetailsCode,sizeof(DetailsCode))||
       !Readable(base+EmptyTextRva,2)||*reinterpret_cast<const wchar_t*>(base+EmptyTextRva)!=0||
       !Readable(base+DetailsLabelNameRva,4))return false;
    const wchar_t* const detailsLabel=*reinterpret_cast<const wchar_t* const*>(base+DetailsLabelNameRva);
    if(!Readable(detailsLabel,sizeof(DetailsLabel))||memcmp(detailsLabel,DetailsLabel,sizeof(DetailsLabel)))return false;
    static constexpr BYTE QueryCommand[]{0x6a,0x71},OnCommand[]{0x6a,0x75},OffCommand[]{0x6a,0x76};
    return !memcmp(base+ClientQueryRva,ClientQueryCode,sizeof(ClientQueryCode))&&
        !memcmp(base+ClientOnRva,ClientToggleCode,sizeof(ClientToggleCode))&&
        !memcmp(base+ClientOffRva,ClientToggleCode,sizeof(ClientToggleCode))&&
        !memcmp(base+ClientQueryRva+ClientQueryCommandOffset,QueryCommand,sizeof(QueryCommand))&&
        !memcmp(base+ClientOnRva+ClientCommandOffset,OnCommand,sizeof(OnCommand))&&
        !memcmp(base+ClientOffRva+ClientCommandOffset,OffCommand,sizeof(OffCommand));
}
static_assert(FilterInitRva==NativeRankedAccess::FiltersRva,"Both modules use the same filters init");
static bool Install(BYTE* base){
    Image=base;
    const bool sharedInit=NativeRankedAccess::Verified&&NativeRankedAccess::Image==base;
    if(!Matches(base,sharedInit)){Error=1;return false;}
    // The filters init is last so a shared init can leave it out.
    const unsigned hooks=sharedInit?HookCount-1:HookCount;
    void* const targets[HookCount]{base+SetupRvas[0],base+SetupRvas[1],base+SetupRvas[2],base+StartRva,
        base+ClientShowRvas[0],base+ClientShowRvas[1],base+ClientOnRva,base+FilterLoadRva,
        base+CellImageRva,base+CellTextRva,base+FilterInitRva};
    void* const detours[HookCount]{reinterpret_cast<void*>(&Setup<0>),reinterpret_cast<void*>(&Setup<1>),
        reinterpret_cast<void*>(&Setup<2>),reinterpret_cast<void*>(&KeepServerOff),
        reinterpret_cast<void*>(&ShowClientMenu<0>),reinterpret_cast<void*>(&ShowClientMenu<1>),
        reinterpret_cast<void*>(&KeepClientOff),reinterpret_cast<void*>(&LoadPunkBusterFilter),
        reinterpret_cast<void*>(&SetCellImage),reinterpret_cast<void*>(&SetCellText),
        reinterpret_cast<void*>(&InitFilters)};
    void** const originals[HookCount]{reinterpret_cast<void**>(&OriginalSetups[0]),reinterpret_cast<void**>(&OriginalSetups[1]),
        reinterpret_cast<void**>(&OriginalSetups[2]),reinterpret_cast<void**>(&OriginalStart),
        reinterpret_cast<void**>(&OriginalClientShows[0]),reinterpret_cast<void**>(&OriginalClientShows[1]),
        reinterpret_cast<void**>(&OriginalClientOn),reinterpret_cast<void**>(&OriginalFilterLoad),
        reinterpret_cast<void**>(&OriginalCellImage),reinterpret_cast<void**>(&OriginalCellText),
        reinterpret_cast<void**>(&OriginalFilterInit)};
    unsigned created=0;
    for(;created<hooks;++created)if(MH_CreateHook(targets[created],detours[created],originals[created])!=MH_OK)break;
    if(created<hooks){while(created)MH_RemoveHook(targets[--created]);Error=2;return false;}
    // The on routine first, so a menu shown meanwhile cannot turn the client
    // on; the filter load before init, so shown filters are never filtering.
    static constexpr unsigned Order[HookCount]{6,3,0,1,2,4,5,7,8,9,10};
    for(unsigned i=0;i<hooks;++i){
        if(MH_EnableHook(targets[Order[i]])!=MH_OK){
            for(unsigned j=0;j<hooks;++j){MH_DisableHook(targets[j]);MH_RemoveHook(targets[j]);}
            Error=3;return false;
        }
    }
    if(sharedInit)NativeRankedAccess::AfterFilters=&RemoveShownFilter;
    Verified=true;
    return true;
}
}
