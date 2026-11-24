#pragma once
// Controller support for Rainbow Six Vegas and Rainbow Six Vegas 2.
//
// Original evidence (both clients, read from the installed files):
// - The clients import DINPUT8!DirectInput8Create. Vegas 1 also imports XINPUT1_3
//   ordinal 2 (XInputGetState). Vegas 2 imports ordinals 2, 3 and 5 (XInputGetState,
//   XInputSetState, XInputEnable).
// - The joystick manager (UWinJoystickMgr) reads [Joystick%i] sections of
//   Joysticks.ini. Each section holds ProductName, VendorID, ProductID and either
//   DirectInput button and axis names or UseXInput.
// - The shipped DefaultJoysticks.ini sets UseXInput=true only for VendorID=1118
//   (0x045e), ProductID=654 (0x028e): the wired Xbox 360 controller. Both titles ship
//   the same table. ProductName cannot be the key: [Joystick5] and [Joystick8] share
//   "Xbox 360 USB controller" with different product IDs.
//
// Current XInput controllers (Xbox One and Series controllers, wireless receivers,
// Steam Input's virtual controller) have other product IDs. They are either missing
// from the table or match a DirectInput layout written for another device.
//
// This module reports every XInput-capable controller under the wired Xbox 360 IDs, so
// the game uses its own XInput path from the original table. Windows marks
// XInput-capable HID devices with "IG_" in the device path. It also places the first
// connected XInput controller in slot 0, the slot read when only one controller is
// used. Other slots keep their order. Nothing in the game executable is changed.
#include <windows.h>
#include <cstring>
#include <new>
#include "MinHook.h"

namespace NativeGamepad {
constexpr WORD XboxVendor = 0x045e, Xbox360Product = 0x028e;
constexpr DWORD Xbox360Ids = MAKELONG(XboxVendor, Xbox360Product);
constexpr unsigned MaxDevices = 16, Slots = 4;

inline bool Verified = false;
inline const char* Error = "notInstalled";

// Pure rules, covered by tests/native/gamepad_test.cpp.
inline bool XInputPath(const wchar_t* path) {
    if (!path) return false;
    for (; path[0] && path[1] && path[2]; ++path)
        if ((path[0] == L'I' || path[0] == L'i') && (path[1] == L'G' || path[1] == L'g') && path[2] == L'_') return true;
    return false;
}
// Vendor and product IDs as DirectInput packs them (guidProduct.Data1, DIPROP_VIDPID).
inline DWORD ReportedIds(DWORD ids, const DWORD* xinput, unsigned count) {
    for (unsigned i = 0; i < count; ++i) if (xinput[i] == ids) return Xbox360Ids;
    return ids;
}
// Swaps slot 0 with the first connected slot; every other slot is unchanged.
inline DWORD MappedSlot(DWORD index, DWORD first) {
    if (index >= Slots || first == 0 || first >= Slots) return index;
    if (index == 0) return first;
    if (index == first) return 0;
    return index;
}

// XInput-capable controllers currently attached.
inline SRWLOCK DeviceLock = SRWLOCK_INIT;
inline DWORD Devices[MaxDevices]{};
inline unsigned DeviceCount = 0;
inline volatile LONG Scanned = 0;
inline void Scan() {
    DWORD found[MaxDevices]{}; unsigned count = 0;
    UINT total = 0;
    if (GetRawInputDeviceList(nullptr, &total, sizeof(RAWINPUTDEVICELIST)) == 0 && total) {
        auto list = new (std::nothrow) RAWINPUTDEVICELIST[total];
        if (list) {
            UINT listed = GetRawInputDeviceList(list, &total, sizeof(RAWINPUTDEVICELIST));
            for (UINT i = 0; listed != UINT(-1) && i < listed && count < MaxDevices; ++i) {
                if (list[i].dwType != RIM_TYPEHID) continue;
                wchar_t name[512]{}; UINT length = 512;
                if (GetRawInputDeviceInfoW(list[i].hDevice, RIDI_DEVICENAME, name, &length) == UINT(-1) || !XInputPath(name)) continue;
                RID_DEVICE_INFO info{}; info.cbSize = sizeof(info); UINT size = sizeof(info);
                if (GetRawInputDeviceInfoW(list[i].hDevice, RIDI_DEVICEINFO, &info, &size) == UINT(-1) || info.dwType != RIM_TYPEHID) continue;
                const DWORD ids = MAKELONG(WORD(info.hid.dwVendorId), WORD(info.hid.dwProductId));
                if (ids != Xbox360Ids) found[count++] = ids;
            }
            delete[] list;
        }
    }
    AcquireSRWLockExclusive(&DeviceLock);
    memcpy(Devices, found, sizeof(found)); DeviceCount = count;
    ReleaseSRWLockExclusive(&DeviceLock);
    InterlockedExchange(&Scanned, 1);
}
inline DWORD Reported(DWORD ids) {
    if (!Scanned) Scan();
    AcquireSRWLockShared(&DeviceLock);
    const DWORD result = ReportedIds(ids, Devices, DeviceCount);
    ReleaseSRWLockShared(&DeviceLock);
    return result;
}

// DIDEVICEINSTANCEA and DIDEVICEINSTANCEW share this prefix.
struct InstancePrefix { DWORD dwSize; GUID guidInstance; GUID guidProduct; };
constexpr size_t LargestInstance = 1024;
using EnumCallback = BOOL(CALLBACK*)(const void*, void*);
struct EnumContext { EnumCallback callback; void* reference; };
inline BOOL CALLBACK ReportDevice(const void* instance, void* reference) {
    auto context = static_cast<EnumContext*>(reference);
    auto prefix = static_cast<const InstancePrefix*>(instance);
    if (!prefix || prefix->dwSize < sizeof(InstancePrefix) || prefix->dwSize > LargestInstance)
        return context->callback(instance, context->reference);
    alignas(8) unsigned char copy[LargestInstance];
    memcpy(copy, instance, prefix->dwSize);
    auto reported = reinterpret_cast<InstancePrefix*>(copy);
    reported->guidProduct.Data1 = Reported(reported->guidProduct.Data1);
    return context->callback(copy, context->reference);
}

// DirectInput keeps separate ANSI and Unicode methods. Each slot owns one trampoline.
inline CRITICAL_SECTION HookLock;
inline void* HookedTargets[16]{};
inline unsigned HookedCount = 0;
inline bool HookOnce(void* target, void* detour, void** original) {
    if (!target) return false;
    EnterCriticalSection(&HookLock);
    bool result = false, known = false;
    for (unsigned i = 0; i < HookedCount; ++i) if (HookedTargets[i] == target) known = true;
    if (known) result = true;
    else if (HookedCount < 16 && MH_CreateHook(target, detour, original) == MH_OK) {
        if (MH_EnableHook(target) == MH_OK) { HookedTargets[HookedCount++] = target; result = true; }
        else MH_RemoveHook(target);
    }
    LeaveCriticalSection(&HookLock);
    return result;
}

// IDirectInputDevice8 vtable: GetProperty is 5, GetDeviceInfo is 15.
template<int Slot> struct DeviceHooks {
    using GetPropertyFn = HRESULT(STDMETHODCALLTYPE*)(void*, const GUID&, void*);
    using GetInfoFn = HRESULT(STDMETHODCALLTYPE*)(void*, void*);
    static inline void* OriginalProperty = nullptr;
    static inline void* OriginalInfo = nullptr;
    static HRESULT STDMETHODCALLTYPE Property(void* self, const GUID& property, void* header) {
        const HRESULT result = reinterpret_cast<GetPropertyFn>(OriginalProperty)(self, property, header);
        // DIPROP_VIDPID is MAKEDIPROP(24); the value is a DIPROPDWORD.
        if (SUCCEEDED(result) && header && reinterpret_cast<ULONG_PTR>(&property) == 24) {
            auto value = reinterpret_cast<DWORD*>(static_cast<unsigned char*>(header) + 16);
            *value = Reported(*value);
        }
        return result;
    }
    static HRESULT STDMETHODCALLTYPE Info(void* self, void* instance) {
        const HRESULT result = reinterpret_cast<GetInfoFn>(OriginalInfo)(self, instance);
        auto prefix = static_cast<InstancePrefix*>(instance);
        if (SUCCEEDED(result) && prefix && prefix->dwSize >= sizeof(InstancePrefix))
            prefix->guidProduct.Data1 = Reported(prefix->guidProduct.Data1);
        return result;
    }
};

// IDirectInput8 vtable: CreateDevice is 3, EnumDevices is 4.
template<int Slot> struct InterfaceHooks {
    using CreateDeviceFn = HRESULT(STDMETHODCALLTYPE*)(void*, const GUID&, void**, void*);
    using EnumFn = HRESULT(STDMETHODCALLTYPE*)(void*, DWORD, EnumCallback, void*, DWORD);
    static inline void* OriginalCreate = nullptr;
    static inline void* OriginalEnum = nullptr;
    static HRESULT STDMETHODCALLTYPE CreateDevice(void* self, const GUID& instance, void** device, void* outer) {
        const HRESULT result = reinterpret_cast<CreateDeviceFn>(OriginalCreate)(self, instance, device, outer);
        if (SUCCEEDED(result) && device && *device) {
            void** table = *static_cast<void***>(*device);
            HookOnce(table[5], reinterpret_cast<void*>(&DeviceHooks<Slot>::Property), &DeviceHooks<Slot>::OriginalProperty);
            HookOnce(table[15], reinterpret_cast<void*>(&DeviceHooks<Slot>::Info), &DeviceHooks<Slot>::OriginalInfo);
        }
        return result;
    }
    static HRESULT STDMETHODCALLTYPE Enum(void* self, DWORD type, EnumCallback callback, void* reference, DWORD flags) {
        if (!callback) return reinterpret_cast<EnumFn>(OriginalEnum)(self, type, callback, reference, flags);
        Scan();  // Controllers can be attached between enumerations.
        EnumContext context{callback, reference};
        return reinterpret_cast<EnumFn>(OriginalEnum)(self, type, &ReportDevice, &context, flags);
    }
};

// IID_IDirectInput8W {BF798031-483A-4DA2-AA99-5D64ED369700}
inline bool Unicode(const GUID& riid) {
    static const GUID wide{0xbf798031, 0x483a, 0x4da2, {0xaa, 0x99, 0x5d, 0x64, 0xed, 0x36, 0x97, 0x00}};
    return IsEqualGUID(riid, wide) != FALSE;
}
template<int Slot> inline void HookInterface(void* object) {
    void** table = *static_cast<void***>(object);
    HookOnce(table[3], reinterpret_cast<void*>(&InterfaceHooks<Slot>::CreateDevice), &InterfaceHooks<Slot>::OriginalCreate);
    HookOnce(table[4], reinterpret_cast<void*>(&InterfaceHooks<Slot>::Enum), &InterfaceHooks<Slot>::OriginalEnum);
}
using DirectInputCreateFn = HRESULT(WINAPI*)(HINSTANCE, DWORD, const GUID&, void**, void*);
inline DirectInputCreateFn OriginalDirectInputCreate = nullptr;
inline HRESULT WINAPI DirectInputCreate(HINSTANCE instance, DWORD version, const GUID& riid, void** object, void* outer) {
    const HRESULT result = OriginalDirectInputCreate(instance, version, riid, object, outer);
    if (SUCCEEDED(result) && object && *object) {
        if (Unicode(riid)) HookInterface<1>(*object); else HookInterface<0>(*object);
    }
    return result;
}

// Slot order, refreshed at most once a second: a disconnected slot is slow to query.
using GetStateFn = DWORD(WINAPI*)(DWORD, void*);
using SetStateFn = DWORD(WINAPI*)(DWORD, void*);
using GetCapabilitiesFn = DWORD(WINAPI*)(DWORD, DWORD, void*);
inline GetStateFn OriginalGetState = nullptr;
inline SetStateFn OriginalSetState = nullptr;
inline GetCapabilitiesFn OriginalGetCapabilities = nullptr;
inline volatile LONG FirstSlot = 0;
inline volatile LONGLONG SlotCheckedAt = 0;
inline DWORD FirstConnected() {
    const ULONGLONG now = GetTickCount64();
    const LONGLONG checked = InterlockedCompareExchange64(&SlotCheckedAt, 0, 0);
    if (checked && now - ULONGLONG(checked) < 1000) return DWORD(FirstSlot);
    InterlockedExchange64(&SlotCheckedAt, LONGLONG(now ? now : 1));
    DWORD first = 0;
    unsigned char state[16]{};  // XINPUT_STATE
    for (DWORD slot = 0; slot < Slots; ++slot)
        if (OriginalGetState(slot, state) == ERROR_SUCCESS) { first = slot; break; }
    InterlockedExchange(&FirstSlot, LONG(first));
    return first;
}
inline DWORD WINAPI GetState(DWORD index, void* state) { return OriginalGetState(MappedSlot(index, FirstConnected()), state); }
inline DWORD WINAPI SetState(DWORD index, void* vibration) { return OriginalSetState(MappedSlot(index, FirstConnected()), vibration); }
inline DWORD WINAPI GetCapabilities(DWORD index, DWORD flags, void* capabilities) {
    return OriginalGetCapabilities(MappedSlot(index, FirstConnected()), flags, capabilities);
}

// Call before MH_EnableHook(MH_ALL_HOOKS). A failure leaves controller input as in the
// original game and does not stop the launch.
inline bool Arm() {
    InitializeCriticalSection(&HookLock);
    if (!LoadLibraryW(L"dinput8.dll")) { Error = "directInputMissing"; return false; }
    if (MH_CreateHookApi(L"dinput8.dll", "DirectInput8Create", reinterpret_cast<void*>(DirectInputCreate),
            reinterpret_cast<void**>(&OriginalDirectInputCreate)) != MH_OK) {
        Error = "directInputHookFailed"; return false;
    }
    if (!LoadLibraryW(L"xinput1_3.dll")) { Verified = true; Error = "xinputMissing"; return true; }
    const bool state = MH_CreateHookApi(L"xinput1_3.dll", "XInputGetState", reinterpret_cast<void*>(GetState),
        reinterpret_cast<void**>(&OriginalGetState)) == MH_OK;
    const bool vibration = state && MH_CreateHookApi(L"xinput1_3.dll", "XInputSetState", reinterpret_cast<void*>(SetState),
        reinterpret_cast<void**>(&OriginalSetState)) == MH_OK;
    const bool capabilities = vibration && MH_CreateHookApi(L"xinput1_3.dll", "XInputGetCapabilities",
        reinterpret_cast<void*>(GetCapabilities), reinterpret_cast<void**>(&OriginalGetCapabilities)) == MH_OK;
    if (!capabilities) {
        // Slot order must change for every XInput call or for none.
        if (vibration) MH_RemoveHook(reinterpret_cast<void*>(GetProcAddress(GetModuleHandleW(L"xinput1_3.dll"), "XInputSetState")));
        if (state) MH_RemoveHook(reinterpret_cast<void*>(GetProcAddress(GetModuleHandleW(L"xinput1_3.dll"), "XInputGetState")));
        Verified = true; Error = "slotOrderUnavailable"; return true;
    }
    Verified = true;
    Error = "ok";
    return true;
}
}
