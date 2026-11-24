#pragma once
// Rainbow Six Vegas 2 "ENABLE PUNKBUSTER SERVER" and "ENABLE PUNKBUSTER CLIENT"
// rows. They exist only in the native settings menu (section SettingsMenu,
// vtable 0x11ef21e4; R6Menus.int [Punkbuster] EnablePunkbusterServerLabel and
// EnablePunkbusterClientLabel, whose only references are the data pointers
// 0x121bd264 and 0x121bd268 read by the refresh).
// Addresses are the unpacked client image; the unprotected dedicated build
// RainbowSixVegas2_SADS.exe has the same code 0x280 lower here.
// - Open 0x114cc9e0 clears the list at menu +0x70 and adds rows with
//   AddRow 0x1156c2e0 (thiscall list, name, id; ret 8): ids 0 LAN name,
//   1 tutorial tips, 2 PunkBuster server, 3 PunkBuster client, 4 uPnP,
//   5 custom head port. Rows are 0x50 bytes in a vector at list +0x74; the
//   id is stored at row +0x24 and the activation handler switches on it.
// - Refresh 0x114ccd70 fills the list by position: SetRowText 0x114f7a70
//   (cdecl list, position, label, value) for positions 0-5 and
//   SetRowEnabled 0x11565a60 (thiscall list, position, flag; ret 8) for
//   positions 5 and 3 (the client row is locked while the server is on).
// - The server row's handler is the only caller of the PunkBuster server
//   enable path (0x115936e0 query, 0x11593740 enable via 0x11594940).
//   0x11594980 is the game's own "turn the server off" routine: it turns the
//   server off if it is on and does nothing when PunkBuster is not loaded.
// - The client row's handler toggles the client with 0x11594960 (on) and
//   0x115949a0 (off, the same shape as the server's). 0x11594960 has two
//   other callers: the confirmation after turning the server on
//   (0x114cc860) and the server browser's join prompt (0x114c38a0), which
//   joins either way.
// The adapter skips the row-2 and row-3 AddRow calls from Open, so the list
// holds four rows and the selection, row height and navigation are the
// list's own. During Refresh, positions for this menu's list are shifted
// past the removed rows and their calls are dropped. Each time the menu
// opens the original off routines run, and the client on routine does
// nothing, so neither setting can stay on.
//
// The PunkBuster match filter (R6MatchFilterPunkBuster, vtable 0x11f93f8c,
// id 0x29 from slot 1):
// - The filters manager builds the shown filters with 0x11ab8940
//   (thiscall, category), adding each option found by id (0x11ab7ef0) unless
//   its +0x10 flag is set; the game sets that flag itself to hide filters that
//   do not apply (0x11ab83ac, 0x11ab8453). The PunkBuster option is checked
//   last (0x11ab8ba9).
// - The option's value +8 maps through 0x11f92b8c to -1 (Any), 0 or 1. Its
//   own load (slot 19, 0x11ac3a10) reads the saved value from the profile
//   ([+0x34]+0x4c); its save (slot 18, 0x11ac39e0) writes it back.
// Before each build, the adapter sets the PunkBuster option's +0x10 flag. Its
// load sets the value to Any and saves that with the original save routine
// instead of reading the saved value.
//
// The server browser's PunkBuster sort button (R6MagmaSelectMatch, vtable
// 0x11ef0a5c; R6Menus.int [Punkbuster] PunkBusterSortBtn, read through
// 0x121bcf0c only by the menu's refresh):
// - Bind (slot 5, 0x114c3b90) looks up the elements named in the table at
//   0x121bce0c in order; B_Link07 (0x121bce4c, entry 16) lands at menu +0xa0
//   and its text at +0xf0, where the refresh writes PunkBusterSortBtn.
// - The click handler (slot 23) sorts by the PunkBuster filter id 0x29 when
//   B_Link07 is clicked (0x114c3667).
// - Show (slot 19, 0x114c6020) runs each time the menu is shown and hides
//   two unused elements (+0x74, +0x7c) by clearing bit 0 of element +0x18.
// After Show, the adapter hides B_Link07 the same way.
//
// The server list's PunkBuster column (Lst_SelectMatchPC, menu +0x5c):
// - The row fill (0x114c5030) writes Yes or No to column 4 from the match's
//   PunkBuster flag (0x11ac09f0, at 0x114c5273), then shows that cell with the
//   list's set-cell-shown routine 0x11563670 (thiscall list, row, column,
//   flag; ret 0xc), returning to 0x114c5320.
// - The same flag is what the list's sort compares for id 0x29
//   (0x11ac0efa) and what the join prompt for PunkBuster servers checks.
// The adapter passes 0 from that call, so the cell stays hidden.
#include <windows.h>
#include <intrin.h>
#include <cstring>
#include "MinHook.h"

#pragma intrinsic(_ReturnAddress)

namespace NativePunkBusterVegas2 {
constexpr unsigned Preferred = 0x10900000;
constexpr unsigned Open = 0x114cc9e0, Refresh = 0x114ccd70, AddRow = 0x1156c2e0;
constexpr unsigned SetRowText = 0x114f7a70, SetRowEnabled = 0x11565a60, ServerOff = 0x11594980;
constexpr unsigned ClientOn = 0x11594960, ClientOff = 0x115949a0;
// push 2 / push 0x11ef1f9c / call AddRow, returning to 0x114cca5c; then
// push 3 / push 0x11ef1fa0 / call AddRow, returning to 0x114cca6b.
constexpr unsigned RowCall = 0x114cca50, ServerRowReturn = 0x114cca5c, ClientRowReturn = 0x114cca6b;
constexpr unsigned ListOffset = 0x70, ServerRow = 2, ClientRow = 3, HookCount = 9;
constexpr unsigned char OpenPrologue[] = {0x56,0x8b,0xf1,0x83,0x7e,0x0c,0x00,0x0f,0x84,0xf2,0x01,0x00,0x00};
constexpr unsigned char RefreshPrologue[] = {0x64,0xa1,0,0,0,0,0x6a,0xff,0x68,0x88,0x45,0xc0,0x11,0x50};
constexpr unsigned char AddRowPrologue[] = {0x6a,0xff,0x68,0x58,0xd1,0xc0,0x11,0x64,0xa1,0,0,0,0,0x50};
constexpr unsigned char SetRowTextPrologue[] = {0x8b,0x44,0x24,0x0c,0x85,0xc0,0x75,0x05,0xb8,0x90,0x85,0xef,0x11};
constexpr unsigned char SetRowEnabledPrologue[] = {0x53,0x55,0x8b,0x6c,0x24,0x0c,0x56,0x8b,0xf1,0x8b,0x46,0x74};
constexpr unsigned char ServerOffCode[] = {0xe8,0x5b,0xed,0xff,0xff,0x85,0xc0,0x74,0x05,0xe8,0x12,0xee,0xff,0xff};
constexpr unsigned char ClientOnCode[] = {0xe8,0xbb,0xe0,0xff,0xff,0x85,0xc0,0x75,0x05,0xe8,0x72,0xe1,0xff,0xff};
constexpr unsigned char ClientOffCode[] = {0xe8,0x7b,0xe0,0xff,0xff,0x85,0xc0,0x74,0x05,0xe8,0x82,0xe1,0xff,0xff};
constexpr unsigned BuildFilters = 0x11ab8940, FindFilter = 0x11ab7ef0, FilterBuildCheck = 0x11ab8ba9;
constexpr unsigned FilterLoad = 0x11ac3a10, FilterSave = 0x11ac39e0, FilterSaveStore = 0x11ac39fb;
constexpr unsigned FilterVtable = 0x11f93f8c, FilterId = 0x11ac3a50, FilterVtableStore = 0x11ac3990;
constexpr unsigned FilterIdSlot = 1, FilterSaveSlot = 18, FilterLoadSlot = 19;
constexpr unsigned FilterPunkBuster = 0x29, FilterHidden = 0x10, FilterValue = 8;
constexpr unsigned char BuildFiltersCode[] = {0x53,0x55,0x56,0x57,0x8b,0xf9,0x8d,0x77,0x14,0x33,0xc0,0x39,0x46,0x08};
constexpr unsigned char FindFilterCode[] = {0x53,0x56,0x57,0x8b,0xf9,0x33,0xf6,0x39,0x77,0x0c,0x7e,0x1d};
// push 0x29 / find / ... / cmp byte ptr [edi+0x10],0: the build skips a hidden option.
constexpr unsigned char FilterBuildCheckCode[] = {0x6a,0x29,0x8b,0xcf,0xe8,0x3e,0xf3,0xff,0xff,0x8b,0xf8,0x8b,0x17,0x8b,0x42,0x44,
    0x8b,0xcf,0xff,0xd0,0x3b,0xc5,0x75,0x1c,0x80,0x7f,0x10,0x00};
constexpr unsigned char FilterLoadCode[] = {0x56,0x8b,0xf1,0x8b,0x4e,0x04,0x85,0xc9,0x74,0x23,0xe8,0x11,0x43,0xff,0xff};
constexpr unsigned char FilterSaveCode[] = {0x56,0x8b,0xf1,0x8b,0x4e,0x04,0x85,0xc9,0x74,0x1a,0xe8,0x41,0x43,0xff,0xff};
// mov eax,[eax+0x34] / mov ecx,[esi+8] / mov [eax+0x4c],ecx: the saved value.
constexpr unsigned char FilterSaveStoreCode[] = {0x8b,0x40,0x34,0x8b,0x4e,0x08,0x89,0x48,0x4c};
constexpr unsigned char FilterIdCode[] = {0xb8,0x29,0x00,0x00,0x00,0xc3};
constexpr unsigned char FilterVtableStoreCode[] = {0xc7,0x06,0x8c,0x3f,0xf9,0x11};
constexpr unsigned MatchVtable = 0x11ef0a5c, MatchShow = 0x114c6020, MatchBind = 0x114c3b90;
constexpr unsigned MatchBindStore = 0x114c3c18, SortClick = 0x114c3667, SortButtonName = 0x121bce4c;
constexpr unsigned MatchBindSlot = 5, MatchShowSlot = 19, SortButtonOffset = 0xa0, ElementFlags = 0x18;
// Ends with Show's own hiding of +0x74 and +0x7c.
constexpr unsigned char MatchShowCode[] = {0x56,0x8b,0xf1,0x8b,0x8e,0xac,0x00,0x00,0x00,0x57,0xe8,0xd1,0x5c,0x0a,0x00,
    0x8b,0x8e,0xb0,0x00,0x00,0x00,0xe8,0xc6,0x5c,0x0a,0x00,0x8b,0x46,0x74,0xb1,0xfe,0x20,0x48,0x18,0x8b,0x46,0x7c,0x20,0x48,0x18};
// Bind's loop storing the named elements from menu +0x60.
constexpr unsigned char MatchBindStoreCode[] = {0xbb,0x0c,0xce,0x1b,0x12,0x8d,0x7e,0x60,0xc7,0x07,0x00,0x00,0x00,0x00,
    0x8b,0x0b,0x8b,0x56,0x1c,0x51,0x52,0xe8,0x9e,0x50,0x07,0x00,0x83,0xc4,0x08,0x85,0xc0,0x89,0x07,0x0f,0x84,0x9a,0x00,0x00,0x00,
    0x83,0xc3,0x04,0x83,0xc7,0x04,0x81,0xfb,0x58,0xce,0x1b,0x12};
// B_Link07's click: sort by id 0x29.
constexpr unsigned char SortClickCode[] = {0x8b,0x0d,0x4c,0xce,0x1b,0x12,0x8b,0x28,0x51,0x8d,0x54,0x24,0x14,0x52,0xe8,0x96,0xc8,0x07,0x00,
    0x83,0xc4,0x08,0x3b,0x28,0x75,0x38,0x8b,0x07,0x8b,0x50,0x08,0x8d,0x4e,0x50,0xbb,0x0b,0x00,0x00,0x00,0x39,0x9e,0x00,0x01,0x00,0x00,
    0x51,0x8b,0xcf,0x74,0x0e,0x6a,0x01,0x6a,0x29};
constexpr unsigned SetCellShown = 0x11563670, PunkBusterCell = 0x114c5269, PunkBusterCellShow = 0x114c5309;
constexpr unsigned PunkBusterCellReturn = 0x114c5320, PunkBusterSort = 0x11ac0efa;
constexpr unsigned char SetCellShownCode[] = {0x53,0x55,0x8b,0x2d,0x90,0x56,0xc8,0x11,0x56,0x0f,0xbf,0x74,0x24,0x10,0x57};
// call the PunkBuster flag / ... / push "Yes".
constexpr unsigned char PunkBusterCellCode[] = {0x8b,0xcd,0xc6,0x84,0x24,0x9c,0x00,0x00,0x00,0x08,0xe8,0x78,0xb7,0x5f,0x00,
    0x85,0xc0,0x53,0x53,0x74,0x37,0x68,0x40,0x09,0xef,0x11};
// Set column 4's text, then show column 4.
constexpr unsigned char PunkBusterCellShowCode[] = {0x50,0x6a,0x04,0x57,0x8b,0xce,0xe8,0x2c,0x4d,0x0a,0x00,0x6a,0x01,0x6a,0x04,
    0x57,0x8b,0xce,0xe8,0x50,0xe3,0x09,0x00};
// cmp sort id,0x29 / ... / call the PunkBuster flag.
constexpr unsigned char PunkBusterSortCode[] = {0x83,0xf9,0x29,0x8b,0x70,0x20,0x75,0x26,0x8b,0x4c,0x24,0x0c,0xe8,0xe5,0xfa,0xff,0xff};
constexpr unsigned char RowCallCode[] = {0x6a,0x02,0x68,0x9c,0x1f,0xef,0x11,0xe8,0x84,0xf8,0x09,0x00,
    0x8b,0x4e,0x70,0x6a,0x03,0x68,0xa0,0x1f,0xef,0x11,0xe8,0x75,0xf8,0x09,0x00};

using AddRowFn = int(__thiscall*)(void*,const char*,int);
using RefreshFn = void(__thiscall*)(void*);
using SetRowTextFn = int(__cdecl*)(void*,int,const wchar_t*,const wchar_t*);
using SetRowEnabledFn = int(__thiscall*)(void*,int,int);
using ToggleFn = int(__cdecl*)();

inline unsigned char* Base = nullptr;
inline AddRowFn OriginalAddRow = nullptr;
inline RefreshFn OriginalRefresh = nullptr;
inline SetRowTextFn OriginalSetRowText = nullptr;
inline SetRowEnabledFn OriginalSetRowEnabled = nullptr;
inline ToggleFn OriginalClientOn = nullptr;
using BuildFiltersFn = void(__thiscall*)(void*,int);
using FindFilterFn = void*(__thiscall*)(void*,int);
using FilterFn = void(__thiscall*)(void*);
inline BuildFiltersFn OriginalBuildFilters = nullptr;
inline FilterFn OriginalFilterLoad = nullptr;
using ShowFn = void(__thiscall*)(void*);
inline ShowFn OriginalMatchShow = nullptr;
using SetCellShownFn = void(__thiscall*)(void*,int,int,int);
inline SetCellShownFn OriginalSetCellShown = nullptr;
inline bool Verified = false;
inline const char* Error = "notInstalled";
// The settings list while its Refresh runs on the game thread.
inline void* ActiveList = nullptr;
inline DWORD ActiveThread = 0;

inline unsigned char* At(unsigned address) { return Base+address-Preferred; }

// Position after the removed rows, or -1 for a removed row itself.
inline int Shift(int position) {
    if (position == int(ServerRow) || position == int(ClientRow)) return -1;
    return position > int(ClientRow) ? position-2 : position;
}
inline bool Remapping(void* list) {
    return list && list == ActiveList && GetCurrentThreadId() == ActiveThread;
}

inline int __fastcall SkipPunkBusterRows(void* list, void*, const char* name, int id) {
    void* const caller = _ReturnAddress();
    // The menu is opening: keep both off, as unticking them would.
    if (caller == At(ServerRowReturn) && id == int(ServerRow)) {
        reinterpret_cast<ToggleFn>(At(ServerOff))();
        return -1;
    }
    if (caller == At(ClientRowReturn) && id == int(ClientRow)) {
        reinterpret_cast<ToggleFn>(At(ClientOff))();
        return -1;
    }
    return OriginalAddRow(list,name,id);
}
inline int __cdecl KeepClientOff() { return 0; }
inline void __fastcall BuildShownFilters(void* manager, void*, int category) {
    // Hidden the way the game hides filters that do not apply.
    void* const option = reinterpret_cast<FindFilterFn>(At(FindFilter))(manager,int(FilterPunkBuster));
    if (option && *static_cast<void**>(option) == At(FilterVtable))
        static_cast<unsigned char*>(option)[FilterHidden] = 1;
    OriginalBuildFilters(manager,category);
}
inline void __fastcall LoadPunkBusterFilter(void* option, void*) {
    // Any, saved back with the original save in place of the saved value.
    *reinterpret_cast<int*>(static_cast<unsigned char*>(option)+FilterValue) = 0;
    reinterpret_cast<FilterFn>(At(FilterSave))(option);
}
inline void __fastcall ShowMatchList(void* menu, void*) {
    OriginalMatchShow(menu);
    // Hidden the way Show hides the menu's other unused elements.
    unsigned char* const button = *reinterpret_cast<unsigned char**>(static_cast<unsigned char*>(menu)+SortButtonOffset);
    if (button) button[ElementFlags] &= 0xfe;
}
inline void __fastcall ShowCell(void* list, void*, int row, int column, int shown) {
    if (_ReturnAddress() == At(PunkBusterCellReturn)) shown = 0;
    OriginalSetCellShown(list,row,column,shown);
}
inline void __fastcall RefreshSettings(void* menu, void*) {
    void* const list = *reinterpret_cast<void**>(static_cast<unsigned char*>(menu)+ListOffset);
    void* const previousList = ActiveList;
    const DWORD previousThread = ActiveThread;
    ActiveList = list; ActiveThread = GetCurrentThreadId();
    OriginalRefresh(menu);
    ActiveList = previousList; ActiveThread = previousThread;
}
inline int __cdecl SetText(void* list, int position, const wchar_t* label, const wchar_t* value) {
    if (Remapping(list)) {
        position = Shift(position);
        if (position < 0) return 0;
    }
    return OriginalSetRowText(list,position,label,value);
}
inline int __fastcall SetEnabled(void* list, void*, int position, int flag) {
    if (Remapping(list)) {
        // The original passes a short position in a full argument slot.
        const int shifted = Shift(static_cast<short>(position));
        if (shifted < 0) return 0;
        position = (position & ~0xffff) | (shifted & 0xffff);
    }
    return OriginalSetRowEnabled(list,position,flag);
}

inline bool FilterTableReady() {
    void* const* const table = reinterpret_cast<void* const*>(At(FilterVtable));
    return table[FilterIdSlot] == At(FilterId) && table[FilterSaveSlot] == At(FilterSave) &&
           table[FilterLoadSlot] == At(FilterLoad);
}
inline bool SortButtonReady() {
    void* const* const table = reinterpret_cast<void* const*>(At(MatchVtable));
    const char* const name = *reinterpret_cast<const char* const*>(At(SortButtonName));
    return table[MatchBindSlot] == At(MatchBind) && table[MatchShowSlot] == At(MatchShow) &&
           name && !strcmp(name,"B_Link07");
}
inline bool CodeReady(unsigned char* base) {
    Base = base;
    return !memcmp(At(Open),OpenPrologue,sizeof(OpenPrologue)) &&
           !memcmp(At(Refresh),RefreshPrologue,sizeof(RefreshPrologue)) &&
           !memcmp(At(AddRow),AddRowPrologue,sizeof(AddRowPrologue)) &&
           !memcmp(At(SetRowText),SetRowTextPrologue,sizeof(SetRowTextPrologue)) &&
           !memcmp(At(SetRowEnabled),SetRowEnabledPrologue,sizeof(SetRowEnabledPrologue)) &&
           !memcmp(At(ServerOff),ServerOffCode,sizeof(ServerOffCode)) &&
           !memcmp(At(ClientOn),ClientOnCode,sizeof(ClientOnCode)) &&
           !memcmp(At(ClientOff),ClientOffCode,sizeof(ClientOffCode)) &&
           !memcmp(At(BuildFilters),BuildFiltersCode,sizeof(BuildFiltersCode)) &&
           !memcmp(At(FindFilter),FindFilterCode,sizeof(FindFilterCode)) &&
           !memcmp(At(FilterBuildCheck),FilterBuildCheckCode,sizeof(FilterBuildCheckCode)) &&
           !memcmp(At(FilterLoad),FilterLoadCode,sizeof(FilterLoadCode)) &&
           !memcmp(At(FilterSave),FilterSaveCode,sizeof(FilterSaveCode)) &&
           !memcmp(At(FilterSaveStore),FilterSaveStoreCode,sizeof(FilterSaveStoreCode)) &&
           !memcmp(At(FilterId),FilterIdCode,sizeof(FilterIdCode)) &&
           !memcmp(At(FilterVtableStore),FilterVtableStoreCode,sizeof(FilterVtableStoreCode)) &&
           FilterTableReady() &&
           !memcmp(At(MatchShow),MatchShowCode,sizeof(MatchShowCode)) &&
           !memcmp(At(MatchBindStore),MatchBindStoreCode,sizeof(MatchBindStoreCode)) &&
           !memcmp(At(SortClick),SortClickCode,sizeof(SortClickCode)) &&
           SortButtonReady() &&
           !memcmp(At(SetCellShown),SetCellShownCode,sizeof(SetCellShownCode)) &&
           !memcmp(At(PunkBusterCell),PunkBusterCellCode,sizeof(PunkBusterCellCode)) &&
           !memcmp(At(PunkBusterCellShow),PunkBusterCellShowCode,sizeof(PunkBusterCellShowCode)) &&
           !memcmp(At(PunkBusterSort),PunkBusterSortCode,sizeof(PunkBusterSortCode)) &&
           !memcmp(At(RowCall),RowCallCode,sizeof(RowCallCode));
}

inline bool Install(unsigned char* base) {
    if (!CodeReady(base)) { Error = "codeMismatch"; return false; }
    void* const targets[HookCount] = {At(AddRow),At(Refresh),At(SetRowText),At(SetRowEnabled),At(ClientOn),
        At(FilterLoad),At(BuildFilters),At(MatchShow),At(SetCellShown)};
    void* const detours[HookCount] = {reinterpret_cast<void*>(SkipPunkBusterRows),reinterpret_cast<void*>(RefreshSettings),
        reinterpret_cast<void*>(SetText),reinterpret_cast<void*>(SetEnabled),reinterpret_cast<void*>(KeepClientOff),
        reinterpret_cast<void*>(LoadPunkBusterFilter),reinterpret_cast<void*>(BuildShownFilters),
        reinterpret_cast<void*>(ShowMatchList),reinterpret_cast<void*>(ShowCell)};
    void** const originals[HookCount] = {reinterpret_cast<void**>(&OriginalAddRow),reinterpret_cast<void**>(&OriginalRefresh),
        reinterpret_cast<void**>(&OriginalSetRowText),reinterpret_cast<void**>(&OriginalSetRowEnabled),
        reinterpret_cast<void**>(&OriginalClientOn),reinterpret_cast<void**>(&OriginalFilterLoad),
        reinterpret_cast<void**>(&OriginalBuildFilters),reinterpret_cast<void**>(&OriginalMatchShow),
        reinterpret_cast<void**>(&OriginalSetCellShown)};
    unsigned created = 0;
    for (; created < HookCount; ++created) {
        if (MH_CreateHook(targets[created],detours[created],originals[created]) != MH_OK) break;
    }
    if (created < HookCount) {
        while (created) MH_RemoveHook(targets[--created]);
        Error = "hookCreationFailed"; return false;
    }
    // Refresh and the list calls first, so an open menu never sees a
    // four-row list filled by six-row positions.
    const unsigned order[HookCount] = {4,5,6,7,8,1,2,3,0};
    for (unsigned i = 0; i < HookCount; ++i) {
        if (MH_EnableHook(targets[order[i]]) != MH_OK) {
            for (unsigned j = 0; j < HookCount; ++j) { MH_DisableHook(targets[j]); MH_RemoveHook(targets[j]); }
            Error = "hookEnableFailed"; return false;
        }
    }
    Verified = true;
    Error = "ok";
    return true;
}
}
