#include "../../tools/companion/native_procedural_animation.h"
#include <initializer_list>
#include <cassert>
#include <cmath>

// A frame-counted native spring/random-target surrogate: input poses still
// change each frame, while replay must reproduce one history step per 1/60s.
static unsigned Simulate(unsigned fps) {
    ProceduralClock::Replay replay;
    int history = 0, calls = 0, nativeEvaluations = 0;
    for (unsigned frame=0;frame<fps;++frame) {
        const auto step = ProceduralClock::Step(frame, fps);
        replay.Begin(step,&history,sizeof(history));
        const int random = replay.Next([&] { return ++calls; });
        ++history;
        ++nativeEvaluations;
        assert(random == history);
    }
    assert(nativeEvaluations == int(fps));
    assert(calls == history);
    return history;
}
static int RandomCalls = 0;
static int __cdecl FakeRandom() { return ++RandomCalls; }
static void __fastcall FakeEye(void* object, void*) {
    auto node = static_cast<unsigned char*>(object);
    auto& history = *reinterpret_cast<int*>(node+0x144);
    auto& counter = *reinterpret_cast<int*>(node+0x1c4);
    const int random = NativeProceduralAnimation::ReplayRandom();
    history += random;
    ++counter;
    *reinterpret_cast<int*>(node+0x40) = history + *reinterpret_cast<int*>(node+0x44);
}
static void __fastcall FakeInit(void*,void*) {}
int main() {
    using namespace NativeProceduralAnimation;
    alignas(16) unsigned char node[0x240]{};
    OriginalRandom = FakeRandom;
    Originals[0] = reinterpret_cast<Method>(FakeEye);
    OriginalInit = reinterpret_cast<Method>(FakeInit);
    *reinterpret_cast<int*>(node+0x44) = 100;
    Evaluate<0>(node,1);
    assert(*reinterpret_cast<int*>(node+0x144) == 1);
    assert(*reinterpret_cast<int*>(node+0x1c4) == 1);
    *reinterpret_cast<int*>(node+0x44) = 200;
    Evaluate<0>(node,1);
    assert(*reinterpret_cast<int*>(node+0x144) == 1);
    assert(*reinterpret_cast<int*>(node+0x1c4) == 1);
    assert(*reinterpret_cast<int*>(node+0x40) == 201); // Current pose evaluated again.
    assert(RandomCalls == 1);
    assert(ActiveReplay == nullptr);
    assert(ReplayRandom() == 2); // Outside these methods, ordinary CRT behavior.
    Evaluate<0>(node,2);
    assert(*reinterpret_cast<int*>(node+0x144) == 4);
    assert(*reinterpret_cast<int*>(node+0x1c4) == 2);
    Initialize(node+0x34,nullptr);
    *reinterpret_cast<int*>(node+0x144) = 20;
    Evaluate<0>(node,2);
    assert(*reinterpret_cast<int*>(node+0x144) == 24);
    // All six histories are bounded and disjoint; unrelated pose/target fields
    // remain live across render evaluations. Invalid executable is rejected.
    unsigned char invalid[0x1000]{};
    assert(!Install(invalid));

    for (unsigned fps : {60u,120u,144u,240u,1000u}) assert(Simulate(fps) == 60);
    assert(Simulate(30) == 30); // No catch-up burst on low frame rates.
    ProceduralClock::Replay replay;
    int state=7, calls=0;
    replay.Begin(10,&state,sizeof(state));
    const int a = replay.Next([&] { return ++calls; });
    const int b = replay.Next([&] { return ++calls; });
    state=99;
    replay.Begin(10,&state,sizeof(state));
    assert(state == 7);
    assert(replay.Next([&] { return ++calls; }) == a);
    assert(replay.Next([&] { return ++calls; }) == b);
    assert(calls == 2);
    state=12;
    replay.Begin(11,&state,sizeof(state));
    assert(state == 12);
    assert(replay.Next([&] { return ++calls; }) == 3);
    replay = {};
    state=37;
    replay.Begin(11,&state,sizeof(state));
    assert(state == 37); // Node reinitialization never restores old history.
    constexpr std::uint64_t frequency=10000000;
    assert(ProceduralClock::Step(frequency*60*60*24*365,frequency) == 60ull*60*60*24*365);
}
