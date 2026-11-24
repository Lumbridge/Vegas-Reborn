#include "../../tools/companion/native_gamepad.h"
#include <cassert>
#include <cstdio>

int main() {
    using namespace NativeGamepad;
    // Device paths Windows gives XInput-capable HID devices.
    assert(XInputPath(L"\\\\?\\HID#VID_045E&PID_0B12&IG_00#8&1a2b3c&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}"));
    assert(XInputPath(L"\\\\?\\hid#vid_28de&pid_11ff&ig_01#9&2b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}"));
    assert(!XInputPath(L"\\\\?\\HID#VID_054C&PID_09CC&MI_03#7&1a2b3c&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}"));
    assert(!XInputPath(L"IG"));
    assert(!XInputPath(nullptr));

    // Joysticks.ini UseXInput entry: VendorID=1118, ProductID=654.
    assert(Xbox360Ids == MAKELONG(1118, 654));
    const DWORD seriesController = MAKELONG(0x045e, 0x0b12), steamVirtual = MAKELONG(0x28de, 0x11ff);
    const DWORD dualShock = MAKELONG(0x054c, 0x09cc);
    const DWORD xinput[]{seriesController, steamVirtual};
    assert(ReportedIds(seriesController, xinput, 2) == Xbox360Ids);
    assert(ReportedIds(steamVirtual, xinput, 2) == Xbox360Ids);
    assert(ReportedIds(dualShock, xinput, 2) == dualShock);
    assert(ReportedIds(seriesController, xinput, 0) == seriesController);

    // Slot order is a swap of slot 0 and the first connected slot.
    for (DWORD first = 0; first < Slots; ++first) {
        bool seen[Slots]{};
        for (DWORD index = 0; index < Slots; ++index) {
            const DWORD mapped = MappedSlot(index, first);
            assert(mapped < Slots && !seen[mapped]);
            seen[mapped] = true;
        }
        assert(MappedSlot(0, first) == first);
    }
    assert(MappedSlot(2, 0) == 2);
    assert(MappedSlot(1, 2) == 1);
    assert(MappedSlot(2, 2) == 0);
    assert(MappedSlot(7, 2) == 7);
    assert(MappedSlot(0, 9) == 0);
    std::puts("gamepad_test ok");
    return 0;
}
