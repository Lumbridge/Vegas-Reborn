#pragma once
// Rainbow Six Vegas 2 Discord sign-in.
// R6MagmaLogin (RTTI .?AVR6MagmaLogin@@, vtable 0x11ef11d4):
// - 0x114c9160 fills the page when it opens: 0x114c80a0(FString* user,
//   FString* password) loads the remembered credential, then each value is
//   copied into a game-allocated std::wstring and passed to the control's
//   SetText (0x115707f0) for T_UserName (this+0x98) and T_Password (this+0x9c).
//   The game's strings use the engine allocator, so the controls are only
//   filled through this path, never written directly.
// - 0x114c7e70 (B_Login) copies each control's text into engine FStrings and
//   calls 0x10f7c930(listener, 2, 0, 0, FString user, FString password, 0),
//   which owns and frees both (ret 0x2c).
// - 0x10f7cd40 replays 0x10f7c930 with the user and password the manager
//   cached (this+0x24, this+0x30) when the game signs in again, for example
//   before Create Match.
// The companion returns the account alias and a placeholder from the loader,
// then supplies a freshly minted one-use credential in the login call when the
// placeholder is still in place, or when the game replays the credential it
// last supplied (already spent). A typed credential is passed through unchanged.
#include <windows.h>
#include <cstring>
#include <string>
#include "MinHook.h"

namespace NativeLoginVegas2 {
constexpr unsigned Preferred = 0x10900000;
constexpr unsigned LoadAddress = 0x114c80a0, LoginAddress = 0x10f7c930;
constexpr unsigned FStringConstruct = 0x1091b510, FStringAssign = 0x1091b590, FStringDestruct = 0x10901940;
constexpr wchar_t Placeholder[] = L"UseDiscord";
constexpr unsigned char LoadPrologue[] = {0x6a,0xff,0x68,0xec,0x3f,0xc0,0x11,0x64,0xa1,0,0,0,0,0x50};
constexpr unsigned char LoginPrologue[] = {0x64,0xa1,0,0,0,0,0x6a,0xff,0x68,0xb8,0xfd,0xb9,0x11,0x50};
constexpr unsigned char ConstructPrologue[] = {0x53,0x8b,0x5c,0x24,0x08,0x66,0x83,0x3b,0x00,0x56,0x8b,0xf1};
constexpr unsigned char AssignPrologue[] = {0x55,0x8b,0x6c,0x24,0x08,0x56,0x8b,0xf1,0x3b,0xf5};
constexpr unsigned char DestructPrologue[] = {0x6a,0xff,0x68,0x88,0x58,0xb3,0x11,0x64,0xa1,0,0,0,0};
// The page fill's loader call and its T_UserName/T_Password reads
// (mov ecx,[ebp+0x98] / mov ecx,[ebp+0x9c]), checked so the loader's
// results are this page's controls.
constexpr unsigned FillLoadCall = 0x114c91ad, FillUser = 0x114c9217, FillPassword = 0x114c92a4;
// The binder's B_Login click target, checked so the login call is this page's.
constexpr unsigned ClickCall = 0x114c8f9c, ClickHandler = 0x114c7e70;

struct FString { wchar_t* data; int count; int capacity; };
using Load = void(__thiscall*)(void*,FString*,FString*);
using Login = int(__thiscall*)(void*,void*,int,int,int,FString,FString,int);
using Construct = void*(__thiscall*)(FString*,const wchar_t*);
using Assign = FString*(__thiscall*)(FString*,const FString*);
using Destruct = void(__thiscall*)(FString*);

inline unsigned char* Base = nullptr;
inline Load OriginalLoad = nullptr;
inline Login OriginalLogin = nullptr;
inline std::wstring Alias;
inline bool (*Mint)(std::string&,std::string&) = nullptr;
inline bool Verified = false;
inline const char* Error = "notInstalled";
inline volatile LONG Prefilled = 0, Supplied = 0, Replayed = 0, MintFailures = 0;
// The last credential supplied to the game. Its lease is spent once the
// service accepts it, so a replay of it needs a fresh one.
inline std::wstring LastName, LastSecret;
inline CRITICAL_SECTION Gate;

inline unsigned char* At(unsigned address) { return Base+address-Preferred; }

// Assign text to an engine FString with the engine's own allocator.
inline void Set(FString* value, const wchar_t* text) {
    FString temporary{};
    reinterpret_cast<Construct>(At(FStringConstruct))(&temporary,text);
    reinterpret_cast<Assign>(At(FStringAssign))(value,&temporary);
    reinterpret_cast<Destruct>(At(FStringDestruct))(&temporary);
}

inline void __fastcall LoadRemembered(void* page, void*, FString* user, FString* password) {
    OriginalLoad(page,user,password);
    if (user && password) {
        Set(user,Alias.c_str());
        Set(password,Placeholder);
        InterlockedIncrement(&Prefilled);
    }
}

inline void Replace(FString& value, const wchar_t* text) {
    reinterpret_cast<Destruct>(At(FStringDestruct))(&value);
    reinterpret_cast<Construct>(At(FStringConstruct))(&value,text);
}

inline bool Equals(const FString& value, const wchar_t* text, size_t length) {
    return value.data && value.count == int(length+1) && !wcscmp(value.data,text);
}

inline void Forget(std::wstring& value) {
    SecureZeroMemory(value.data(),value.size()*sizeof(wchar_t));
    value.clear();
}

inline int __fastcall LoginWithDiscord(void* manager, void*, void* listener, int kind, int a3, int a4,
                                       FString user, FString password, int a7) {
    EnterCriticalSection(&Gate);
    const bool placeholder = Equals(password,Placeholder,wcslen(Placeholder));
    const bool replay = !placeholder && !LastSecret.empty() &&
        Equals(user,LastName.c_str(),LastName.size()) && Equals(password,LastSecret.c_str(),LastSecret.size());
    if (placeholder || replay) {
        std::string name, secret;
        if (Mint && Mint(name,secret)) {
            Forget(LastSecret);
            LastName.assign(name.begin(),name.end());
            LastSecret.assign(secret.begin(),secret.end());
            Replace(user,LastName.c_str());
            Replace(password,LastSecret.c_str());
            SecureZeroMemory(secret.data(),secret.size());
            InterlockedIncrement(replay ? &Replayed : &Supplied);
        } else {
            // The placeholder or spent credential is not valid, so the service
            // rejects it and the game shows its own login failure message.
            InterlockedIncrement(&MintFailures);
        }
    }
    LeaveCriticalSection(&Gate);
    return OriginalLogin(manager,listener,kind,a3,a4,user,password,a7);
}

inline bool CallsTo(unsigned site, unsigned target) {
    return *At(site) == 0xe8 && At(site)+5+*reinterpret_cast<int*>(At(site)+1) == At(target);
}

inline bool CodeReady(unsigned char* base) {
    Base = base;
    return !memcmp(At(LoadAddress),LoadPrologue,sizeof(LoadPrologue)) &&
           !memcmp(At(LoginAddress),LoginPrologue,sizeof(LoginPrologue)) &&
           !memcmp(At(FStringConstruct),ConstructPrologue,sizeof(ConstructPrologue)) &&
           !memcmp(At(FStringAssign),AssignPrologue,sizeof(AssignPrologue)) &&
           !memcmp(At(FStringDestruct),DestructPrologue,sizeof(DestructPrologue));
}

inline bool Install(unsigned char* base, const std::string& alias, bool (*mint)(std::string&,std::string&)) {
    Base = base;
    if (!CodeReady(base)) { Error = "codeMismatch"; return false; }
    const unsigned char user[] = {0x8b,0x8d,0x98,0,0,0}, password[] = {0x8b,0x8d,0x9c,0,0,0};
    // 0x114c8f9c: lea ecx,[ebp-0x3c]; call 0x114c7e70 (B_Login handler).
    const unsigned char click[] = {0x8d,0x4d,0xc4};
    if (!CallsTo(FillLoadCall,LoadAddress) || memcmp(At(FillUser),user,sizeof(user)) ||
        memcmp(At(FillPassword),password,sizeof(password)) ||
        memcmp(At(ClickCall),click,sizeof(click)) || !CallsTo(ClickCall+3,ClickHandler)) {
        Error = "pageMismatch"; return false;
    }
    Alias.assign(alias.begin(),alias.end());
    Mint = mint;
    InitializeCriticalSection(&Gate);
    if (MH_CreateHook(At(LoadAddress),reinterpret_cast<void*>(LoadRemembered),reinterpret_cast<void**>(&OriginalLoad)) != MH_OK) {
        Error = "hookCreationFailed"; return false;
    }
    if (MH_CreateHook(At(LoginAddress),reinterpret_cast<void*>(LoginWithDiscord),reinterpret_cast<void**>(&OriginalLogin)) != MH_OK) {
        MH_RemoveHook(At(LoadAddress)); Error = "hookCreationFailed"; return false;
    }
    if (MH_EnableHook(At(LoadAddress)) != MH_OK || MH_EnableHook(At(LoginAddress)) != MH_OK) {
        MH_DisableHook(At(LoadAddress)); MH_DisableHook(At(LoginAddress));
        MH_RemoveHook(At(LoadAddress)); MH_RemoveHook(At(LoginAddress));
        Error = "hookEnableFailed"; return false;
    }
    Verified = true;
    Error = "ok";
    return true;
}
}
