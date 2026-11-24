#pragma once
// Rainbow Six Vegas 2 startup intro sequence.
// The startup builder 0x113a5710 queues five groups on the sequence manager
// with 0x113a5320 (thiscall: TArray<Group*>* at manager+4, Group** item; adds
// the item once):
// - ../kellergame/startup/ubilogo            (append returns to 0x113a57f9)
// - ../kellergame/startup/dw_mm_splash_a     (0x113a58a6)
// - utech_sw.dds PICTURE with a TIMER        (0x113a59e9)
// - ../kellergame/startup/ersb_ rating       (0x113a5ac2, skipped by the game
//   itself when global 0x12399368 is 5)
// - rainbowrun + localizeloading, the loading group (0x113a5c5a), kept.
// The manager's tick 0x113a1d20 advances to the next group when the current
// group's completion check 0x113a1b80 (thiscall on the group) returns true.
// The movie groups are reported complete, once each. The utech group's timer
// check is the engine start handshake (below), so it still runs and the group
// ends as soon as the engine has started, as Vegas 1 keeps its equivalent
// group. The queue, finish callbacks and ownership are unchanged. The
// MovieLoading.ini [INTRO] setting alone leaves these screens and their waits.
#include <windows.h>
#include <cstdio>
#include <cstring>
#include <string>
#include <intrin.h>
#include "MinHook.h"

namespace NativeSkipIntrosVegas2 {
constexpr unsigned Preferred = 0x10900000;
constexpr unsigned Builder = 0x113a5710, Append = 0x113a5320, Complete = 0x113a1b80, Tick = 0x113a1d20;
constexpr unsigned Count = 4;
constexpr unsigned IntroReturns[Count] = {0x113a57f9, 0x113a58a6, 0x113a59e9, 0x113a5ac2};
constexpr unsigned LoadingReturn = 0x113a5c5a;
// Engine start handshake. The utech TIMER item's completion check 0x113a0ba0
// (vtable 0x11ed0bc0 slot) sets 0x121b8054 from -1 to 0 after 0.1 s and waits
// until the main thread, which waits at 0x113d9457 while it is -1, has
// started the engine and set it to 1 (0x113d94bc).
constexpr unsigned HandshakeGroup = 2, StartupState = 0x121b8054, TimerCheck = 0x113a0ba0, EngineWait = 0x113d9457;
constexpr LONG StartupDone = 1;
constexpr unsigned char AppendPrologue[] = {0x53,0x8b,0x5c,0x24,0x08,0x56,0x8b,0xf1,0x8b,0x4e,0x04};
constexpr unsigned char CompletePrologue[] = {0x56,0x57,0x8b,0xf9,0x33,0xf6,0x39,0x77,0x08,0x7e,0x1e};
constexpr unsigned char BuilderPrologue[] = {0x6a,0xff,0x68,0xa7,0x02,0xbf,0x11,0x64,0xa1,0,0,0,0};

using AppendFn = int(__thiscall*)(void*,void**);
using CompleteFn = int(__thiscall*)(void*);

inline unsigned char* Base = nullptr;
inline AppendFn OriginalAppend = nullptr;
inline CompleteFn OriginalComplete = nullptr;
inline bool Requested = false, Verified = false;
inline const char* Error = "notInstalled";
inline void* Groups[Count]{};
inline volatile LONG Registered = 0, Skipped = 0;
inline SRWLOCK Lock = SRWLOCK_INIT;

inline unsigned char* At(unsigned address) { return Base+address-Preferred; }
inline bool CallsTo(unsigned returnAddress, unsigned target) {
    unsigned char* call = At(returnAddress)-5;
    return call[0] == 0xe8 && call+5+*reinterpret_cast<int*>(call+1) == At(target);
}

inline int __fastcall AppendGroup(void* list, void*, void** group) {
    const auto caller = reinterpret_cast<unsigned char*>(_ReturnAddress());
    if (group && *group) {
        for (unsigned i = 0; i < Count; ++i) {
            if (caller == At(IntroReturns[i])) {
                AcquireSRWLockExclusive(&Lock);
                const bool added = !Groups[i];
                if (added) { Groups[i] = *group; InterlockedIncrement(&Registered); }
                ReleaseSRWLockExclusive(&Lock);
                if (added) { char line[48]; sprintf_s(line,"intro group %u queued",i); OutputDebugStringA(line); }
            }
        }
    }
    return OriginalAppend(list,group);
}

inline volatile LONG* StartupFlag() { return reinterpret_cast<volatile LONG*>(At(StartupState)); }

inline int __fastcall GroupComplete(void* group, void*) {
    bool marked = false;
    unsigned index = 0;
    if (group) {
        AcquireSRWLockShared(&Lock);
        for (unsigned i = 0; i < Count; ++i) if (Groups[i] == group) { marked = true; index = i; break; }
        ReleaseSRWLockShared(&Lock);
    }
    if (!marked) return OriginalComplete(group);
    if (index == HandshakeGroup) {
        // The timer item's own check releases the engine's main thread; it
        // returns once the engine has started. Only the remaining logo time
        // is skipped.
        const int original = OriginalComplete(group);
        if (*StartupFlag() != StartupDone) return original;
    }
    AcquireSRWLockExclusive(&Lock);
    const bool first = Groups[index] == group;
    if (first) Groups[index] = nullptr;
    ReleaseSRWLockExclusive(&Lock);
    if (first) { InterlockedIncrement(&Skipped); char line[48]; sprintf_s(line,"intro group %u completed",index); OutputDebugStringA(line); }
    return 1;
}

inline bool CodeReady(unsigned char* base) {
    Base = base;
    if (memcmp(At(Append),AppendPrologue,sizeof(AppendPrologue)) ||
        memcmp(At(Complete),CompletePrologue,sizeof(CompletePrologue)) ||
        memcmp(At(Builder),BuilderPrologue,sizeof(BuilderPrologue)) ||
        !CallsTo(Tick+0xaa,Complete) || !CallsTo(LoadingReturn,Append)) return false;
    for (unsigned i = 0; i < Count; ++i) if (!CallsTo(IntroReturns[i],Append)) return false;
    // cmp dword ptr [StartupState], -1 in the timer check and the engine wait.
    const DWORD flag = DWORD(reinterpret_cast<ULONG_PTR>(At(StartupState)));
    const unsigned char compare[] = {0x83,0x3d,BYTE(flag),BYTE(flag>>8),BYTE(flag>>16),BYTE(flag>>24),0xff};
    return !memcmp(At(TimerCheck+3),compare,sizeof(compare)) && !memcmp(At(EngineWait),compare,sizeof(compare));
}

// 0 when the companion asked to play the intros, 1 to skip them, -1 when the
// setting is missing or invalid.
inline int ReadRequest(const std::wstring& output) {
    FILE* file = nullptr;
    if (_wfopen_s(&file,(output+L"\\skip-intros.txt").c_str(),L"rb") || !file) return -1;
    char text[8]{};
    const size_t count = fread(text,1,sizeof(text)-1,file);
    fclose(file);
    if (count >= 4 && !strncmp(text,"skip",4)) return 1;
    if (count >= 4 && !strncmp(text,"play",4)) return 0;
    return -1;
}

// True when the intros are to play (nothing to do) or both hooks are enabled.
inline bool Install(unsigned char* base, const std::wstring& output) {
    const int request = ReadRequest(output);
    if (request < 0) { Error = "settingInvalid"; return false; }
    if (request == 0) { Error = "notRequested"; return true; }
    Requested = true;
    if (!CodeReady(base)) { Error = "codeMismatch"; return false; }
    if (MH_CreateHook(At(Append),reinterpret_cast<void*>(AppendGroup),reinterpret_cast<void**>(&OriginalAppend)) != MH_OK) {
        Error = "hookCreationFailed"; return false;
    }
    if (MH_CreateHook(At(Complete),reinterpret_cast<void*>(GroupComplete),reinterpret_cast<void**>(&OriginalComplete)) != MH_OK) {
        MH_RemoveHook(At(Append)); Error = "hookCreationFailed"; return false;
    }
    if (MH_EnableHook(At(Append)) != MH_OK || MH_EnableHook(At(Complete)) != MH_OK) {
        MH_DisableHook(At(Append)); MH_DisableHook(At(Complete));
        MH_RemoveHook(At(Append)); MH_RemoveHook(At(Complete));
        Error = "hookEnableFailed"; return false;
    }
    Verified = true;
    Error = "ok";
    return true;
}
}
