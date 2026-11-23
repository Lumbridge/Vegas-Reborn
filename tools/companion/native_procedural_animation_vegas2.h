#pragma once
// Rainbow Six Vegas 2 head and face animation at a fixed rate.
// UR6AnimNodeFrankHeadControl (class registration 0x10be8760, size 0x214,
// constructor 0x10be8700, primary vtable 0x11d3e494, secondary interface at
// +0x30 with vtable 0x11d3e40c) ties gaze, head PD, noise, blinking and eyebrow
// state to the frame rate as in Vegas 1. Each hooked method's persistent state
// is restored and its rand() results replayed within one ProceduralClock step,
// so a step advances once however many frames render. State fields come from
// each method's own writes and the secondary-interface initializer 0x10be2020,
// which resets the same fields.
#include <windows.h>
#include <array>
#include <cstring>
#include <mutex>
#include "MinHook.h"
#include "procedural_clock.h"

namespace NativeProceduralAnimationVegas2 {
constexpr unsigned Preferred = 0x10900000;
constexpr unsigned PrimaryVtable = 0x11d3e494, SecondaryVtable = 0x11d3e40c, SecondaryOffset = 0x30;
constexpr unsigned InitAddress = 0x10be2020, RandomSlot = 0x11c85704;
struct Segment { unsigned offset, bytes; };
struct MethodInfo {
    unsigned address, slot;
    unsigned char prologue[14];
    Segment segments[3];
};
constexpr unsigned MethodCount = 8;
// Order: gaze, head PD, pitch/yaw noise, bone output, blink, eyebrow PD,
// gaze-target timing, roll noise (the only method with a stack argument).
constexpr MethodInfo Methods[MethodCount] = {
    {0x10be27d0,0x120,{0x55,0x8b,0xec,0x83,0xe4,0xf0,0x83,0xec,0x54,0x53,0x56,0x8b,0xf1,0xd9},{{0x140,0x30},{0x1c0,4}}},
    {0x10be66b0,0x11c,{0x55,0x8b,0xec,0x83,0xe4,0xf0,0x83,0xec,0x24,0xa1,0x04,0x76,0x4a,0x12},{{0x1a0,0x18}}},
    {0x10be6960,0x124,{0x55,0x8b,0xec,0x83,0xe4,0xf0,0x81,0xec,0x94,0,0,0,0xa1,0x04},{{0x17c,0x10}}},
    {0x10be72e0,0x128,{0x55,0x8b,0xec,0x83,0xe4,0xf0,0x81,0xec,0x94,0,0,0,0x53,0x56},{}},
    {0x10be3010,0x130,{0x55,0x8b,0xec,0x83,0xe4,0xf0,0x83,0xec,0x44,0x53,0x56,0x57,0x51,0x8b},{{0x1bc,4}}},
    {0x10be34c0,0x134,{0x83,0xec,0x40,0x53,0x55,0x56,0x57,0x51,0x8b,0xc4,0xc7,0x00,0x0f,0x02},{{0x1c4,8}}},
    {0x10be39e0,0x13c,{0x83,0xec,0x74,0x53,0x56,0x8b,0xd9,0x8b,0x03,0x8b,0x90,0x18,0x01,0x00},{{0xa8,12},{0x1d8,4},{0x1e0,4}}},
    {0x10be4130,0x140,{0x55,0x8b,0xec,0x83,0xe4,0xf0,0x81,0xec,0x14,0x01,0,0,0x53,0x56},{{0x17c,0x18}}},
};
constexpr unsigned char InitPrologue[] = {0x56,0x8b,0xf1,0xe8,0x78,0x27,0x6d,0x00};

using Method = void(__thiscall*)(void*);
using MethodArg = void(__thiscall*)(void*,unsigned);
using Random = int(__cdecl*)();
inline void* Originals[MethodCount]{};
inline Method OriginalInit = nullptr;
inline Random OriginalRandom = nullptr;
inline unsigned char* Base = nullptr;
inline LARGE_INTEGER Frequency{};
inline bool Verified = false;
inline const char* Error = "notInstalled";
inline std::recursive_mutex Mutex;
inline thread_local ProceduralClock::Replay* ActiveReplay = nullptr;
struct Entry {
    void* node = nullptr;
    std::uint64_t touched = 0;
    ProceduralClock::Replay methods[MethodCount];
};
inline std::array<Entry,128> Entries{};

inline unsigned char* At(unsigned address) { return Base+address-Preferred; }

inline Entry& Find(void* node, std::uint64_t step) {
    Entry* oldest = &Entries[0];
    for (auto& entry : Entries) {
        if (entry.node == node) {
            entry.touched = step;
            return entry;
        }
        if (!entry.node || entry.touched < oldest->touched) oldest = &entry;
    }
    *oldest = Entry{};
    oldest->node = node;
    oldest->touched = step;
    return *oldest;
}

inline int __cdecl ReplayRandom() {
    return ActiveReplay ? ActiveReplay->Next(OriginalRandom) : OriginalRandom();
}

struct Scope {
    ProceduralClock::Replay* previous;
    explicit Scope(ProceduralClock::Replay* replay): previous(ActiveReplay) { ActiveReplay = replay; }
    ~Scope() { ActiveReplay = previous; }
};

// Gather the method's state segments, let Replay snapshot or restore them,
// then scatter them back into the node.
template<unsigned Index> ProceduralClock::Replay& Prepare(void* node, std::uint64_t step) {
    auto& replay = Find(node,step).methods[Index];
    auto object = static_cast<unsigned char*>(node);
    unsigned char state[sizeof(replay.before)]{};
    unsigned bytes = 0;
    for (const auto& segment : Methods[Index].segments) {
        if (!segment.bytes) continue;
        memcpy(state+bytes,object+segment.offset,segment.bytes);
        bytes += segment.bytes;
    }
    replay.Begin(step,state,bytes);
    bytes = 0;
    for (const auto& segment : Methods[Index].segments) {
        if (!segment.bytes) continue;
        memcpy(object+segment.offset,state+bytes,segment.bytes);
        bytes += segment.bytes;
    }
    return replay;
}

inline std::uint64_t Now() {
    LARGE_INTEGER now{};
    QueryPerformanceCounter(&now);
    return ProceduralClock::Step(now.QuadPart,Frequency.QuadPart);
}

template<unsigned Index> void __fastcall Update(void* node, void*) {
    std::lock_guard<std::recursive_mutex> lock(Mutex);
    Scope scope(&Prepare<Index>(node,Now()));
    // Always evaluate against the current pose and target. Skipping the
    // native method would omit its bone transforms.
    reinterpret_cast<Method>(Originals[Index])(node);
}

template<unsigned Index> void __fastcall UpdateArg(void* node, void*, unsigned argument) {
    std::lock_guard<std::recursive_mutex> lock(Mutex);
    Scope scope(&Prepare<Index>(node,Now()));
    reinterpret_cast<MethodArg>(Originals[Index])(node,argument);
}

inline void __fastcall Initialize(void* secondary, void*) {
    std::lock_guard<std::recursive_mutex> lock(Mutex);
    auto node = static_cast<unsigned char*>(secondary) - SecondaryOffset;
    for (auto& entry : Entries) if (entry.node == node) entry = Entry{};
    OriginalInit(secondary);
}

// True once Steam has unpacked every hooked method.
inline bool CodeReady(unsigned char* base) {
    Base = base;
    for (const auto& method : Methods)
        if (memcmp(At(method.address),method.prologue,sizeof(method.prologue))) return false;
    return !memcmp(At(InitAddress),InitPrologue,sizeof(InitPrologue));
}

inline bool Install(unsigned char* base) {
    Base = base;
    auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0 || dos->e_lfanew > 0x1000) return false;
    auto pe = reinterpret_cast<IMAGE_NT_HEADERS32*>(base+dos->e_lfanew);
    if (pe->Signature != IMAGE_NT_SIGNATURE || pe->FileHeader.Machine != IMAGE_FILE_MACHINE_I386 ||
        pe->FileHeader.TimeDateStamp != 0x484efa91 || pe->OptionalHeader.SizeOfImage != 0x1edb000 ||
        pe->OptionalHeader.ImageBase != Preferred) { Error = "unsupportedImage"; return false; }
    if (!CodeReady(base) || !QueryPerformanceFrequency(&Frequency) || Frequency.QuadPart <= 0) {
        Error = "codeMismatch"; return false;
    }
    for (const auto& method : Methods)
        if (*reinterpret_cast<void**>(At(PrimaryVtable+method.slot)) != At(method.address)) {
            Error = "classMismatch"; return false;
        }
    if (*reinterpret_cast<void**>(At(SecondaryVtable)) != At(InitAddress)) { Error = "classMismatch"; return false; }
    auto randomSlot = reinterpret_cast<void**>(At(RandomSlot));
    const auto crt = GetModuleHandleW(L"MSVCR80.dll");
    if (!crt || *randomSlot != reinterpret_cast<void*>(GetProcAddress(crt,"rand"))) {
        Error = "randomImportMismatch"; return false;
    }
    OriginalRandom = reinterpret_cast<Random>(*randomSlot);
    void* replacements[MethodCount] = {
        reinterpret_cast<void*>(Update<0>),reinterpret_cast<void*>(Update<1>),
        reinterpret_cast<void*>(Update<2>),reinterpret_cast<void*>(Update<3>),
        reinterpret_cast<void*>(Update<4>),reinterpret_cast<void*>(Update<5>),
        reinterpret_cast<void*>(Update<6>),reinterpret_cast<void*>(UpdateArg<7>)};
    void* hooks[MethodCount+1]{};
    unsigned created = 0;
    for (unsigned i=0;i<MethodCount;++i) {
        hooks[i] = At(Methods[i].address);
        if (MH_CreateHook(hooks[i],replacements[i],&Originals[i]) != MH_OK) break;
        ++created;
    }
    hooks[MethodCount] = At(InitAddress);
    if (created == MethodCount && MH_CreateHook(hooks[MethodCount],reinterpret_cast<void*>(Initialize),
            reinterpret_cast<void**>(&OriginalInit)) == MH_OK) ++created;
    auto rollback = [&] { for (unsigned i=0;i<created;++i) { MH_DisableHook(hooks[i]); MH_RemoveHook(hooks[i]); } };
    if (created != MethodCount+1) { rollback(); Error = "hookCreationFailed"; return false; }
    DWORD protection = 0;
    if (!VirtualProtect(randomSlot,sizeof(void*),PAGE_READWRITE,&protection)) {
        rollback(); Error = "randomImportProtectionFailed"; return false;
    }
    InterlockedExchangePointer(randomSlot,reinterpret_cast<void*>(ReplayRandom));
    DWORD ignored = 0;
    VirtualProtect(randomSlot,sizeof(void*),protection,&ignored);
    for (unsigned i=0;i<created;++i) {
        if (MH_EnableHook(hooks[i]) != MH_OK) {
            rollback();
            if (VirtualProtect(randomSlot,sizeof(void*),PAGE_READWRITE,&ignored)) {
                InterlockedExchangePointer(randomSlot,reinterpret_cast<void*>(OriginalRandom));
                VirtualProtect(randomSlot,sizeof(void*),protection,&ignored);
            }
            Error = "hookEnableFailed"; return false;
        }
    }
    Verified = true;
    Error = "ok";
    return true;
}
}
