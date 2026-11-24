#include "../../tools/companion/native_fire_sound_loop.h"
#include <cassert>
#include <cstdio>

using NativeFireSoundLoop::Decide;
using NativeFireSoundLoop::Decision;
using NativeFireSoundLoop::Track;

static Track Started(DWORD now, BYTE flashCount) {
    return Track{reinterpret_cast<BYTE*>(1), reinterpret_cast<BYTE*>(2), now, now, now, 0, flashCount, flashCount != 0, 0};
}

int main() {
    // A burst: FlashCount changes on every shot (100 ms apart), then release sets 0.
    Track burst = Started(1000, 1);
    for (DWORD t = 1100, count = 2; t <= 4000; t += 100, ++count)
        assert(Decide(burst, t, BYTE(count), false) == Decision::Wait);
    assert(Decide(burst, 4050, 0, false) == Decision::Wait);
    assert(Decide(burst, 4700, 0, false) == Decision::Wait);
    assert(Decide(burst, 4751, 0, false) == Decision::Stop);
    // Retries wait two seconds, then give up after the attempt limit.
    assert(Decide(burst, 5000, 0, false) == Decision::Wait);
    assert(Decide(burst, 6751, 0, false) == Decision::Stop);
    DWORD t = 6751;
    for (int attempt = 2; attempt < NativeFireSoundLoop::MaxAttempts; ++attempt)
        assert(Decide(burst, t += 2000, 0, false) == Decision::Stop);
    assert(Decide(burst, t += 2000, 0, false) == Decision::Forget);

    // A short network stall during fire is tolerated.
    Track stall = Started(0, 3);
    assert(Decide(stall, 100, 4, false) == Decision::Wait);
    assert(Decide(stall, 700, 4, false) == Decision::Wait);
    assert(Decide(stall, 800, 5, false) == Decision::Wait);

    // Loops arrived before any FlashCount update: wait a second for one.
    Track unseen = Started(0, 0);
    assert(Decide(unseen, 900, 0, false) == Decision::Wait);
    assert(Decide(unseen, 950, 7, false) == Decision::Wait);
    assert(Decide(unseen, 1700, 0, false) == Decision::Stop);

    // FlashCount frozen non-zero (no more replication): stop after two seconds.
    Track frozen = Started(0, 9);
    assert(Decide(frozen, 1999, 9, false) == Decision::Wait);
    assert(Decide(frozen, 2000, 9, false) == Decision::Stop);

    // A dead or torn-off shooter stops at once.
    Track dead = Started(0, 9);
    assert(Decide(dead, 10, 9, true) == Decision::Stop);

    // GetTickCount wraparound.
    Track wrap = Started(0xffffff00u, 1);
    assert(Decide(wrap, 0xffffff80u, 2, false) == Decision::Wait);
    assert(Decide(wrap, 0x00000200u, 0, false) == Decision::Wait);
    assert(Decide(wrap, 0x00000300u, 0, false) == Decision::Stop);
    assert(Decide(wrap, 0x00000400u, 0, false) == Decision::Wait);

    std::puts("PASS: release, stalls, unseen fire, frozen and dead shooters, retries and wraparound");
    return 0;
}
