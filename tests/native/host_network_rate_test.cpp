#include "../../tools/companion/native_host_network_rate.h"
#include <cassert>
#include <cstdio>
#include <initializer_list>

using NativeHostNetworkRate::Due;
using NativeHostNetworkRate::Gate;

// Flushes in one second of driver time at a fixed frame rate.
static int FlushesPerSecond(double fps, int configured, double start = 10.0) {
    Gate gate{};
    int driver = 0, flushes = 0;
    const double frame = 1.0 / fps;
    for (int i = 0; i < int(fps); ++i)
        if (Due(gate, &driver, start + i * frame, configured)) ++flushes;
    return flushes;
}

int main() {
    // A fast host flushes at the driver's NetServerMaxTickRate, not its frame rate.
    for (double fps : {144.0, 240.0, 300.0, 500.0, 1000.0}) {
        const int flushes = FlushesPerSecond(fps, 30);
        assert(flushes >= 29 && flushes <= 31);
    }
    // A host at or below the rate flushes on every frame, as in the original.
    assert(FlushesPerSecond(30.0, 30) == 30);
    assert(FlushesPerSecond(20.0, 30) == 20);

    // The original dedicated-server clamp of 10-120 applies to the configured rate.
    assert(FlushesPerSecond(1000.0, 0) <= 11);
    assert(FlushesPerSecond(1000.0, 5000) >= 119 && FlushesPerSecond(1000.0, 5000) <= 121);
    assert(NativeHostNetworkRate::Rate(35) == 35);

    // The first tick of a driver flushes at once; a new driver or a clock that went
    // back (a new level) starts again.
    Gate gate{};
    int first = 0, second = 0;
    assert(Due(gate, &first, 100.0, 30));
    assert(!Due(gate, &first, 100.01, 30));
    assert(Due(gate, &second, 100.011, 30));
    assert(Due(gate, &second, 2.0, 30));
    assert(!Due(gate, &second, 2.01, 30));
    assert(Due(gate, &second, 2.034, 30));

    // A long frame does not cause a burst of catch-up flushes.
    Gate stall{};
    assert(Due(stall, &first, 0.0, 30));
    assert(Due(stall, &first, 1.0, 30));
    assert(!Due(stall, &first, 1.01, 30));

    // Installation rejects an image that is not the pinned original client.
    auto blank = static_cast<BYTE*>(VirtualAlloc(nullptr, 0x2000, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    assert(blank);
    assert(!NativeHostNetworkRate::Install(blank));
    assert(!NativeHostNetworkRate::Verified);
    assert(!strcmp(NativeHostNetworkRate::LastError(), "imageIdentityMismatch"));
    VirtualFree(blank, 0, MEM_RELEASE);

    std::puts("PASS: host flush rate, slow hosts, clamp, new drivers and levels, stalls and image identity");
    return 0;
}
