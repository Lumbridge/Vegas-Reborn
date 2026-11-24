#pragma once
// Rainbow Six Vegas 2 "ENABLE PUNKBUSTER SERVER" row.
// The row exists only in the native settings menu (section SettingsMenu,
// vtable 0x11ef21e4; R6Menus.int [Punkbuster] EnablePunkbusterServerLabel,
// whose only reference is the data pointer 0x121bd264 read by the refresh).
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
// The adapter skips the row-2 AddRow call from Open, so the list holds five
// rows and the selection, row height and navigation are the list's own.
// During Refresh, positions for this menu's list are shifted past the
// removed row and the row-2 calls are dropped. Each time the menu opens the
// original off routine runs, so the setting cannot stay on.
#include <windows.h>
#include <intrin.h>
#include <cstring>
#include "MinHook.h"

#pragma intrinsic(_ReturnAddress)

namespace NativePunkBusterVegas2 {
constexpr unsigned Preferred = 0x10900000;
constexpr unsigned Open = 0x114cc9e0, Refresh = 0x114ccd70, AddRow = 0x1156c2e0;
constexpr unsigned SetRowText = 0x114f7a70, SetRowEnabled = 0x11565a60, ServerOff = 0x11594980;
// push 2 / push 0x11ef1f9c / call AddRow; the call returns to 0x114cca5c.
constexpr unsigned RowCall = 0x114cca50, RowReturn = 0x114cca5c;
constexpr unsigned ListOffset = 0x70, RemovedRow = 2, HookCount = 4;
constexpr unsigned char OpenPrologue[] = {0x56,0x8b,0xf1,0x83,0x7e,0x0c,0x00,0x0f,0x84,0xf2,0x01,0x00,0x00};
constexpr unsigned char RefreshPrologue[] = {0x64,0xa1,0,0,0,0,0x6a,0xff,0x68,0x88,0x45,0xc0,0x11,0x50};
constexpr unsigned char AddRowPrologue[] = {0x6a,0xff,0x68,0x58,0xd1,0xc0,0x11,0x64,0xa1,0,0,0,0,0x50};
constexpr unsigned char SetRowTextPrologue[] = {0x8b,0x44,0x24,0x0c,0x85,0xc0,0x75,0x05,0xb8,0x90,0x85,0xef,0x11};
constexpr unsigned char SetRowEnabledPrologue[] = {0x53,0x55,0x8b,0x6c,0x24,0x0c,0x56,0x8b,0xf1,0x8b,0x46,0x74};
constexpr unsigned char ServerOffCode[] = {0xe8,0x5b,0xed,0xff,0xff,0x85,0xc0,0x74,0x05,0xe8,0x12,0xee,0xff,0xff};
constexpr unsigned char RowCallCode[] = {0x6a,0x02,0x68,0x9c,0x1f,0xef,0x11,0xe8,0x84,0xf8,0x09,0x00};

using AddRowFn = int(__thiscall*)(void*,const char*,int);
using RefreshFn = void(__thiscall*)(void*);
using SetRowTextFn = int(__cdecl*)(void*,int,const wchar_t*,const wchar_t*);
using SetRowEnabledFn = int(__thiscall*)(void*,int,int);
using ServerOffFn = int(__cdecl*)();

inline unsigned char* Base = nullptr;
inline AddRowFn OriginalAddRow = nullptr;
inline RefreshFn OriginalRefresh = nullptr;
inline SetRowTextFn OriginalSetRowText = nullptr;
inline SetRowEnabledFn OriginalSetRowEnabled = nullptr;
inline bool Verified = false;
inline const char* Error = "notInstalled";
// The settings list while its Refresh runs on the game thread.
inline void* ActiveList = nullptr;
inline DWORD ActiveThread = 0;

inline unsigned char* At(unsigned address) { return Base+address-Preferred; }

// Position after the removed row, or -1 for the removed row itself.
inline int Shift(int position) {
    if (position == int(RemovedRow)) return -1;
    return position > int(RemovedRow) ? position-1 : position;
}
inline bool Remapping(void* list) {
    return list && list == ActiveList && GetCurrentThreadId() == ActiveThread;
}

inline int __fastcall SkipServerRow(void* list, void*, const char* name, int id) {
    if (_ReturnAddress() == At(RowReturn) && id == int(RemovedRow)) {
        // The menu is opening: keep the server off, as unticking it would.
        reinterpret_cast<ServerOffFn>(At(ServerOff))();
        return -1;
    }
    return OriginalAddRow(list,name,id);
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

inline bool CodeReady(unsigned char* base) {
    Base = base;
    return !memcmp(At(Open),OpenPrologue,sizeof(OpenPrologue)) &&
           !memcmp(At(Refresh),RefreshPrologue,sizeof(RefreshPrologue)) &&
           !memcmp(At(AddRow),AddRowPrologue,sizeof(AddRowPrologue)) &&
           !memcmp(At(SetRowText),SetRowTextPrologue,sizeof(SetRowTextPrologue)) &&
           !memcmp(At(SetRowEnabled),SetRowEnabledPrologue,sizeof(SetRowEnabledPrologue)) &&
           !memcmp(At(ServerOff),ServerOffCode,sizeof(ServerOffCode)) &&
           !memcmp(At(RowCall),RowCallCode,sizeof(RowCallCode));
}

inline bool Install(unsigned char* base) {
    if (!CodeReady(base)) { Error = "codeMismatch"; return false; }
    void* const targets[HookCount] = {At(AddRow),At(Refresh),At(SetRowText),At(SetRowEnabled)};
    void* const detours[HookCount] = {reinterpret_cast<void*>(SkipServerRow),reinterpret_cast<void*>(RefreshSettings),
        reinterpret_cast<void*>(SetText),reinterpret_cast<void*>(SetEnabled)};
    void** const originals[HookCount] = {reinterpret_cast<void**>(&OriginalAddRow),reinterpret_cast<void**>(&OriginalRefresh),
        reinterpret_cast<void**>(&OriginalSetRowText),reinterpret_cast<void**>(&OriginalSetRowEnabled)};
    unsigned created = 0;
    for (; created < HookCount; ++created) {
        if (MH_CreateHook(targets[created],detours[created],originals[created]) != MH_OK) break;
    }
    if (created < HookCount) {
        while (created) MH_RemoveHook(targets[--created]);
        Error = "hookCreationFailed"; return false;
    }
    // Refresh and the list calls first, so an open menu never sees a
    // five-row list filled by six-row positions.
    const unsigned order[HookCount] = {1,2,3,0};
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
