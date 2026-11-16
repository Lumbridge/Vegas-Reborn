#pragma once
#include <windows.h>
#include <array>
#include <mutex>
#include "MinHook.h"
#include "procedural_clock.h"

namespace NativeProceduralAnimation {
constexpr unsigned Preferred = 0x10900000;
constexpr unsigned MethodCount = 6;
// UR6AnimNodeFrankHeadControl: gaze, head PD, pitch/yaw noise, roll noise,
// blinking and eyebrow PD. Only each method's persistent history is replayed.
constexpr unsigned Addresses[MethodCount] = {
    0x111049c0,0x11107a50,0x11107d10,0x11108620,0x11105180,0x111055c0};
constexpr unsigned HistoryOffsets[MethodCount] = {0x144,0x1a4,0x180,0x190,0x1c0,0x1c8};
constexpr unsigned HistoryBytes[MethodCount] = {0x3c,12,16,8,4,8};
using Method = void(__thiscall*)(void*);
using Random = int(__cdecl*)();
inline Method Originals[MethodCount]{}, OriginalInit = nullptr;
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

template<unsigned Index> void Evaluate(void* node, std::uint64_t step) {
    std::lock_guard<std::recursive_mutex> lock(Mutex);
    auto& replay = Find(node,step).methods[Index];
    auto history = static_cast<unsigned char*>(node) + HistoryOffsets[Index];
    // The eye target method also owns a non-adjacent frame counter.
    unsigned char eyeHistory[64]{};
    void* savedHistory = history;
    unsigned bytes = HistoryBytes[Index];
    if constexpr (Index == 0) {
        memcpy(eyeHistory,history,bytes);
        memcpy(eyeHistory+bytes,static_cast<unsigned char*>(node)+0x1c4,4);
        savedHistory = eyeHistory;
        bytes += 4;
    }
    replay.Begin(step,savedHistory,bytes);
    if constexpr (Index == 0) {
        memcpy(history,eyeHistory,HistoryBytes[Index]);
        memcpy(static_cast<unsigned char*>(node)+0x1c4,eyeHistory+HistoryBytes[Index],4);
    }
    struct Scope {
        ProceduralClock::Replay* previous;
        Scope(ProceduralClock::Replay* replay): previous(ActiveReplay) { ActiveReplay = replay; }
        ~Scope() { ActiveReplay = previous; }
    } scope(&replay);
    // Always evaluate against the current pose/target. Skipping the native
    // method would omit its bone transforms and snap back to the input pose.
    Originals[Index](node);
}

template<unsigned Index> void __fastcall Update(void* node, void*) {
    LARGE_INTEGER now{};
    QueryPerformanceCounter(&now);
    Evaluate<Index>(node,ProceduralClock::Step(now.QuadPart,Frequency.QuadPart));
}

inline void __fastcall Initialize(void* secondary, void*) {
    std::lock_guard<std::recursive_mutex> lock(Mutex);
    // This initializer belongs to the node's second interface at +0x34.
    auto node = static_cast<unsigned char*>(secondary) - 0x34;
    for (auto& entry : Entries) if (entry.node == node) entry = Entry{};
    OriginalInit(secondary);
}

inline bool Bytes(unsigned address,std::initializer_list<unsigned char> expected) {
    return !memcmp(Base+address-Preferred,expected.begin(),expected.size());
}
inline bool Install(unsigned char* base) {
    Base = base;
    auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0 || dos->e_lfanew > 0x1000) return false;
    auto pe = reinterpret_cast<IMAGE_NT_HEADERS32*>(base+dos->e_lfanew);
    if (pe->Signature != IMAGE_NT_SIGNATURE || pe->FileHeader.Machine != IMAGE_FILE_MACHINE_I386 ||
        pe->FileHeader.TimeDateStamp != 0x47106421 || pe->OptionalHeader.SizeOfImage != 0x1f56000 ||
        pe->OptionalHeader.ImageBase != Preferred) { Error = "unsupportedImage"; return false; }
    // Check all hooks and the original class dispatch before creating any hook.
    if (!Bytes(Addresses[0],{0x83,0xec,0x44,0x53,0x55,0x56,0x8b,0xf1}) ||
        !Bytes(Addresses[1],{0xa1}) ||
        *reinterpret_cast<void**>(base+Addresses[1]-Preferred+1) != base+0x123c0604-Preferred ||
        !Bytes(Addresses[1]+5,{0x83,0xec,0x2c}) ||
        !Bytes(Addresses[2],{0x83,0xec,0x70,0xa1}) ||
        *reinterpret_cast<void**>(base+Addresses[2]-Preferred+4) != base+0x123c0604-Preferred ||
        !Bytes(Addresses[2]+8,{0x53,0x56}) ||
        !Bytes(Addresses[3],{0x55,0x8b,0xec,0x83,0xe4,0xf0,0x81,0xec,0x94,0,0,0}) ||
        !Bytes(Addresses[4],{0x83,0xec,0x1c,0x53,0x55,0x56}) ||
        !Bytes(Addresses[5],{0x83,0xec,0x3c,0x53,0x55,0x56,0x57}) ||
        !Bytes(0x111035f0,{0x56,0x8b,0xf1,0xe8}) ||
        !QueryPerformanceFrequency(&Frequency) || Frequency.QuadPart <= 0) {
        Error = "codeMismatch"; return false;
    }
    constexpr unsigned Slots[MethodCount] = {0x124,0x120,0x128,0x12c,0x134,0x138};
    for (unsigned i=0;i<MethodCount;++i) {
        auto actual = *reinterpret_cast<void**>(base+0x1202de80-Preferred+Slots[i]);
        if (actual != base+Addresses[i]-Preferred) { Error = "classMismatch"; return false; }
    }
    auto randomSlot = reinterpret_cast<void**>(base+0x125b1e40-Preferred);
    const auto crt = GetModuleHandleW(L"MSVCR71.dll");
    if (!crt || *randomSlot != reinterpret_cast<void*>(GetProcAddress(crt,"rand"))) {
        Error = "randomImportMismatch"; return false;
    }
    OriginalRandom = reinterpret_cast<Random>(*randomSlot);
    void* replacements[MethodCount] = {Update<0>,Update<1>,Update<2>,Update<3>,Update<4>,Update<5>};
    void* hooks[MethodCount+1]{};
    unsigned created = 0;
    for (unsigned i=0;i<MethodCount;++i) {
        hooks[i] = base+Addresses[i]-Preferred;
        if (MH_CreateHook(hooks[i],replacements[i],reinterpret_cast<void**>(&Originals[i])) != MH_OK) break;
        ++created;
    }
    hooks[MethodCount] = base+0x111035f0-Preferred;
    if (created == MethodCount && MH_CreateHook(hooks[MethodCount],Initialize,
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
