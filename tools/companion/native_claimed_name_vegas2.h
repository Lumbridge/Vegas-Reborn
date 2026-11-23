#pragma once
// Rainbow Six Vegas 2 claimed player name. Include after native_login_vegas2.h.
// 0x10f81f10(FString* out, int controller) (thiscall on the login manager,
// ret 8) returns the online player name: the signed-in account alias from the
// online user record (+8), or the local profile name when offline.
// 0x10f7ca90 is the manager's wrapper for it with the same signature. Two
// travel-URL builders take the name from them:
// - 0x10d525e7 in 0x10d52440, the game class's Name option builder (vtable
//   +0x88, called by the host URL builder 0x10d4f3b0 after "START %s?listen"),
//   which appends "?Name%s=" and the name.
// - 0x10f82bc6 in 0x10f82b70, which formats "START %s?Name=%s?XUID=%lld?
//   XNADDR=%s" (format 0x11e2e620, pushed at 0x10f82f9d) and its password
//   variant.
// The companion replaces those two calls, so the player's in-game name becomes
// the claim. The XUID, the login credential and the online user record are
// unchanged.
#include <windows.h>
#include <cstdio>
#include <cstring>
#include <string>

namespace NativeClaimedNameVegas2 {
constexpr unsigned Preferred = 0x10900000;
constexpr unsigned Getter = 0x10f81f10, Wrapper = 0x10f7ca90, FormatPush = 0x10f82f9d, Format = 0x11e2e620;
constexpr unsigned Count = 2;
constexpr unsigned Sites[Count] = {0x10d525e7, 0x10f82bc6};
constexpr unsigned Targets[Count] = {Wrapper, Getter};
constexpr unsigned char GetterPrologue[] = {0x51,0x83,0x3d,0xd0,0xc5,0x25,0x12,0x00,0xc7,0x04,0x24,0,0,0,0};
// mov ecx,[ecx+0x18]; ... call 0x10f81f10 at +0x17.
constexpr unsigned char WrapperPrologue[] = {0x51,0x8b,0x44,0x24,0x0c,0x8b,0x49,0x18};

inline unsigned char* Base = nullptr;
inline std::wstring Claim;
inline bool Verified = false;
inline const char* Error = "notInstalled";
inline volatile LONG Applied = 0;
inline unsigned char Originals[Count][5]{};

using GetterFn = NativeLoginVegas2::FString*(__thiscall*)(void*,NativeLoginVegas2::FString*,int);

inline unsigned char* At(unsigned address) { return Base+address-Preferred; }

template <unsigned Index>
NativeLoginVegas2::FString* __fastcall ClaimedName(void* manager, void*, NativeLoginVegas2::FString* out, int controller) {
    auto result = reinterpret_cast<GetterFn>(At(Targets[Index]))(manager,out,controller);
    if (out) { NativeLoginVegas2::Set(out,Claim.c_str()); InterlockedIncrement(&Applied); }
    return result;
}
inline void* const Replacements[Count] = {
    reinterpret_cast<void*>(&ClaimedName<0>), reinterpret_cast<void*>(&ClaimedName<1>)};

// 0 when the companion wrote no claim, 1 for a valid claim, -1 for an invalid
// file. The claim follows the service's name rules (3-16 characters, letters,
// digits, '_', '.', '-', starting with a letter or digit).
inline int ReadClaim(const std::wstring& output) {
    FILE* file = nullptr;
    if (_wfopen_s(&file,(output+L"\\claimed-name.txt").c_str(),L"rb") || !file) {
        return GetFileAttributesW((output+L"\\claimed-name.txt").c_str()) == INVALID_FILE_ATTRIBUTES ? 0 : -1;
    }
    char bytes[19]{};
    size_t count = fread(bytes,1,sizeof(bytes),file);
    const bool bad = ferror(file) != 0;
    fclose(file);
    if (bad || count == sizeof(bytes)) return -1;
    if (count && bytes[count-1] == '\n') { --count; if (count && bytes[count-1] == '\r') --count; }
    auto alnum = [](unsigned char c) { return (c>='a'&&c<='z') || (c>='A'&&c<='Z') || (c>='0'&&c<='9'); };
    if (count < 3 || count > 16 || !alnum(static_cast<unsigned char>(bytes[0]))) return -1;
    for (size_t i = 0; i < count; ++i) {
        const unsigned char c = static_cast<unsigned char>(bytes[i]);
        if (!alnum(c) && c != '_' && c != '.' && c != '-') return -1;
    }
    Claim.assign(bytes,bytes+count);
    return 1;
}

inline bool CallsTo(unsigned site, unsigned target) {
    return *At(site) == 0xe8 && At(site)+5+*reinterpret_cast<int*>(At(site)+1) == At(target);
}

inline bool CodeReady(unsigned char* base) {
    Base = base;
    if (memcmp(At(Getter),GetterPrologue,sizeof(GetterPrologue)) ||
        memcmp(At(Wrapper),WrapperPrologue,sizeof(WrapperPrologue)) || !CallsTo(Wrapper+0x17,Getter)) return false;
    if (*At(FormatPush) != 0x68 || *reinterpret_cast<unsigned*>(At(FormatPush)+1) != Format-Preferred+unsigned(reinterpret_cast<ULONG_PTR>(Base))) return false;
    for (unsigned i = 0; i < Count; ++i) {
        if (!CallsTo(Sites[i],Targets[i])) return false;
    }
    return true;
}

inline bool Write(unsigned site, const unsigned char* bytes) {
    DWORD protection = 0, ignored = 0;
    if (!VirtualProtect(At(site),5,PAGE_EXECUTE_READWRITE,&protection)) return false;
    memcpy(At(site),bytes,5);
    const bool flushed = FlushInstructionCache(GetCurrentProcess(),At(site),5) != FALSE;
    return VirtualProtect(At(site),5,protection,&ignored) != FALSE && flushed && !memcmp(At(site),bytes,5);
}

// Returns true when no claim was written (nothing to do) or both calls were
// replaced. A claim the adapter cannot apply is a failure.
inline bool Install(unsigned char* base, const std::wstring& output) {
    const int claim = ReadClaim(output);
    if (claim < 0) { Error = "claimFileInvalid"; return false; }
    if (claim == 0) { Error = "claimFileAbsent"; return true; }
    if (!CodeReady(base)) { Error = "codeMismatch"; return false; }
    unsigned written = 0;
    for (; written < Count; ++written) {
        unsigned char call[5] = {0xe8};
        const LONG distance = LONG(reinterpret_cast<ULONG_PTR>(Replacements[written])-reinterpret_cast<ULONG_PTR>(At(Sites[written]))-5);
        memcpy(call+1,&distance,4);
        memcpy(Originals[written],At(Sites[written]),5);
        if (!Write(Sites[written],call)) break;
    }
    if (written == Count) { Verified = true; Error = "ok"; return true; }
    // Restore the calls already replaced (and the one that failed part-way).
    for (unsigned i = 0; i <= written && i < Count; ++i) Write(Sites[i],Originals[i]);
    Error = "installationFailed";
    return false;
}
}
