#pragma once
// Rainbow Six Vegas 2 display modes, matching the Vegas 1 adapter.
// FWindowsViewport::Resize is 0x10f8f540 (thiscall: viewport, x, y,
// fullscreen; ret 0xc), with the same layout as Vegas 1's RVA 0x579350:
// - fullscreen selects the window style 0x100b0000, windowed loads
//   0x10cf0000 with "mov [esp+0x10], imm32" at 0x10f8f625. Vegas reasserts
//   this style on every resize, so Borderless patches the immediate to
//   WS_VISIBLE|WS_POPUP (0x90000000), as VegasDisplay does for Vegas 1.
// - Windowed and Borderless pass fullscreen=0 to the original resize, and the
//   Direct3D 9 device is created and reset with windowed presentation. The
//   companion places the window (Borderless covers the chosen monitor).
// The adapter reports the device result in launch-options-status.json.
#include <windows.h>
#include <d3d9.h>
#include <cstdio>
#include <cstring>
#include <string>
#include "MinHook.h"
#include "display_style.h"

namespace NativeDisplayVegas2 {
constexpr unsigned Preferred = 0x10900000;
constexpr unsigned Resize = 0x10f8f540, ResizeHandler = 0x11ba1132, StyleSite = 0x10f8f625;

inline unsigned char* Base = nullptr;
inline std::wstring Output;
inline bool Windowed = false, Borderless = false;
inline bool FactoryArmed = false, ViewportArmed = false, StyleVerified = false, HookFailed = false;
inline bool DeviceVerified = false, ActualWindowed = false, DpiAware = false;
inline unsigned ResizeCalls = 0;
inline int RequestedFullscreen = -1, AppliedFullscreen = -1;
inline const char* Error = "notInstalled";
inline SRWLOCK Lock = SRWLOCK_INIT;

inline unsigned char* At(unsigned address) { return Base+address-Preferred; }

inline void WriteLocked() {
    if (Output.empty()) return;
    FILE* file = nullptr;
    if (_wfopen_s(&file,(Output+L"\\launch-options-status.tmp").c_str(),L"wb") || !file) return;
    fprintf(file,"{\"windowedRequested\":%s,\"borderlessRequested\":%s,\"factoryHookInstalled\":%s,\"viewportHookInstalled\":%s,"
        "\"displayStyleVerified\":%s,\"hookFailed\":%s,\"deviceVerified\":%s,\"actualWindowed\":%s,\"resizeCalls\":%u,"
        "\"requestedFullscreen\":%d,\"appliedFullscreen\":%d,\"dpiAware\":%s}\n",
        Windowed?"true":"false",Borderless?"true":"false",FactoryArmed?"true":"false",ViewportArmed?"true":"false",
        StyleVerified?"true":"false",HookFailed?"true":"false",DeviceVerified?"true":"false",ActualWindowed?"true":"false",
        ResizeCalls,RequestedFullscreen,AppliedFullscreen,DpiAware?"true":"false");
    fclose(file);
    MoveFileExW((Output+L"\\launch-options-status.tmp").c_str(),(Output+L"\\launch-options-status.json").c_str(),MOVEFILE_REPLACE_EXISTING);
}
inline void Report() { AcquireSRWLockExclusive(&Lock); WriteLocked(); ReleaseSRWLockExclusive(&Lock); }

// Viewport resize.
using ResizeFn = void(__thiscall*)(void*,int,int,int);
inline ResizeFn OriginalResize = nullptr;
inline void __fastcall ResizeWindow(void* viewport, void*, int x, int y, int fullscreen) {
    OriginalResize(viewport,x,y,Windowed?0:fullscreen);
    AcquireSRWLockExclusive(&Lock);
    ++ResizeCalls; RequestedFullscreen = fullscreen; AppliedFullscreen = Windowed?0:fullscreen; WriteLocked();
    ReleaseSRWLockExclusive(&Lock);
}
inline bool ResizeMatches(unsigned char* base) {
    Base = base;
    unsigned char expected[] = {0x6a,0xff,0x68,0,0,0,0,0x64,0xa1,0,0,0,0};
    const DWORD handler = DWORD(reinterpret_cast<ULONG_PTR>(At(ResizeHandler)));
    memcpy(expected+3,&handler,4);
    return !memcmp(At(Resize),expected,sizeof(expected)) && VegasDisplay::Matches(At(StyleSite));
}

// Direct3D 9 presentation.
inline void Present(D3DPRESENT_PARAMETERS* parameters) {
    if (Windowed && parameters) { parameters->Windowed = TRUE; parameters->FullScreen_RefreshRateInHz = 0; }
}
inline bool Hook(void* target, void* detour, void** original) {
    const auto created = MH_CreateHook(target,detour,original);
    if (created != MH_OK && created != MH_ERROR_ALREADY_CREATED) return false;
    const auto enabled = MH_EnableHook(target);
    return enabled == MH_OK || enabled == MH_ERROR_ENABLED;
}
inline void VerifyDevice(IDirect3DDevice9* device) {
    IDirect3DSwapChain9* chain = nullptr; D3DPRESENT_PARAMETERS actual{};
    const bool verified = device && SUCCEEDED(device->GetSwapChain(0,&chain)) && chain && SUCCEEDED(chain->GetPresentParameters(&actual));
    if (chain) chain->Release();
    AcquireSRWLockExclusive(&Lock);
    DeviceVerified = verified; ActualWindowed = verified && actual.Windowed; WriteLocked();
    ReleaseSRWLockExclusive(&Lock);
}
using ResetFn = HRESULT(WINAPI*)(IDirect3DDevice9*,D3DPRESENT_PARAMETERS*);
using AdditionalFn = HRESULT(WINAPI*)(IDirect3DDevice9*,D3DPRESENT_PARAMETERS*,IDirect3DSwapChain9**);
using CreateDeviceFn = HRESULT(WINAPI*)(IDirect3D9*,UINT,D3DDEVTYPE,HWND,DWORD,D3DPRESENT_PARAMETERS*,IDirect3DDevice9**);
using FactoryFn = IDirect3D9*(WINAPI*)(UINT);
inline ResetFn OriginalReset = nullptr;
inline AdditionalFn OriginalAdditional = nullptr;
inline CreateDeviceFn OriginalCreateDevice = nullptr;
inline FactoryFn OriginalFactory = nullptr;
inline HRESULT WINAPI ResetDevice(IDirect3DDevice9* device, D3DPRESENT_PARAMETERS* parameters) {
    Present(parameters);
    const HRESULT result = OriginalReset(device,parameters);
    if (SUCCEEDED(result)) VerifyDevice(device);
    return result;
}
inline HRESULT WINAPI AdditionalChain(IDirect3DDevice9* device, D3DPRESENT_PARAMETERS* parameters, IDirect3DSwapChain9** chain) {
    Present(parameters);
    return OriginalAdditional(device,parameters,chain);
}
inline HRESULT WINAPI NewDevice(IDirect3D9* factory, UINT adapter, D3DDEVTYPE type, HWND window, DWORD behavior,
    D3DPRESENT_PARAMETERS* parameters, IDirect3DDevice9** output) {
    Present(parameters);
    const HRESULT result = OriginalCreateDevice(factory,adapter,type,window,behavior,parameters,output);
    if (SUCCEEDED(result) && output && *output) {
        auto table = *reinterpret_cast<void***>(*output);
        AcquireSRWLockExclusive(&Lock);
        const bool reset = Hook(table[16],reinterpret_cast<void*>(ResetDevice),reinterpret_cast<void**>(&OriginalReset));
        const bool additional = Hook(table[13],reinterpret_cast<void*>(AdditionalChain),reinterpret_cast<void**>(&OriginalAdditional));
        HookFailed = HookFailed || !reset || !additional;
        ReleaseSRWLockExclusive(&Lock);
        VerifyDevice(*output);
    }
    return result;
}
inline IDirect3D9* WINAPI NewFactory(UINT version) {
    IDirect3D9* factory = OriginalFactory(version);
    if (factory) {
        auto table = *reinterpret_cast<void***>(factory);
        AcquireSRWLockExclusive(&Lock);
        HookFailed = HookFailed || !Hook(table[16],reinterpret_cast<void*>(NewDevice),reinterpret_cast<void**>(&OriginalCreateDevice));
        WriteLocked();
        ReleaseSRWLockExclusive(&Lock);
    }
    return factory;
}

// 0 for the game's own display mode, 1 windowed, 2 borderless, -1 when the
// companion's setting is missing or invalid.
inline int ReadRequest(const std::wstring& output) {
    FILE* file = nullptr;
    if (_wfopen_s(&file,(output+L"\\display-mode.txt").c_str(),L"rb") || !file) return -1;
    char mode[16]{};
    const bool valid = fscanf_s(file,"%15s",mode,unsigned(sizeof(mode))) == 1;
    fclose(file);
    if (!valid) return -1;
    if (!strcmp(mode,"native")) return 0;
    if (!strcmp(mode,"windowed")) return 1;
    if (!strcmp(mode,"borderless")) return 2;
    return -1;
}

// At startup, before the game creates its Direct3D device. The factory hook is
// enabled with the adapter's other startup hooks.
inline bool Arm(const std::wstring& output) {
    Output = output;
    const int request = ReadRequest(output);
    if (request < 0) { Error = "settingInvalid"; return false; }
    Windowed = request > 0; Borderless = request == 2;
    if (!Windowed) { Error = "notRequested"; return true; }
    // Without DPI awareness Windows scales the game window by the display
    // scale (a 2560x1440 window covers 3200x1800 at 125%), so a window sized
    // to the monitor overflows it. Set before the game creates its window;
    // an awareness already set by a compatibility setting is kept.
    using AwarenessFn = BOOL(WINAPI*)(HANDLE);
    if (auto set = reinterpret_cast<AwarenessFn>(GetProcAddress(GetModuleHandleW(L"user32.dll"),"SetProcessDpiAwarenessContext"))) {
        DpiAware = set(reinterpret_cast<HANDLE>(-4)) != FALSE || GetLastError() == ERROR_ACCESS_DENIED;
    } else {
        DpiAware = SetProcessDPIAware() != FALSE;
    }
    if (!LoadLibraryW(L"d3d9.dll") ||
        MH_CreateHookApi(L"d3d9.dll","Direct3DCreate9",reinterpret_cast<void*>(NewFactory),reinterpret_cast<void**>(&OriginalFactory)) != MH_OK) {
        Error = "factoryHookFailed"; return false;
    }
    FactoryArmed = true;
    Error = "armed";
    Report();
    return true;
}

// After Steam has unpacked the client code.
inline bool Install(unsigned char* base) {
    if (!Windowed) return !strcmp(Error,"notRequested");
    if (!FactoryArmed) return false;
    if (!ResizeMatches(base)) { Error = "codeMismatch"; Report(); return false; }
    if (Borderless) {
        StyleVerified = VegasDisplay::Apply(At(StyleSite));
        if (!StyleVerified) { Error = "styleFailed"; Report(); return false; }
    }
    if (!Hook(At(Resize),reinterpret_cast<void*>(ResizeWindow),reinterpret_cast<void**>(&OriginalResize))) {
        Error = "viewportHookFailed"; Report(); return false;
    }
    ViewportArmed = true;
    Error = "ok";
    Report();
    return true;
}
}
