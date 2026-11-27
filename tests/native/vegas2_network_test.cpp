#include "../../tools/companion/native_fire_sound_loop_vegas2.h"
#include "../../tools/companion/native_host_network_rate_vegas2.h"
#include "../../tools/companion/native_player_limit_vegas2.h"
#include <cassert>
#include <cstdio>
#include <cstring>

// A fake image region large enough for every Vegas 2 address used below. Only the
// adapters' own logic runs against it; synthetic tests do not establish retail
// gameplay interoperability.
static unsigned char* Region = nullptr;
static unsigned char* Fake(unsigned address) { return Region + (address - 0x10900000); }

static int Flushes = 0;
static void __fastcall CountingFlush(void*, void*) { ++Flushes; }

static void* HeardSound = nullptr;
static void* __fastcall StubHandler(void*, void*, void* sound, unsigned, void*, float, float, float, int, int, int, int) {
    HeardSound = sound;
    return nullptr;
}
static void __fastcall StubTick(void*, void*, float) {}

static void HostRate() {
    using namespace NativeHostNetworkRateVegas2;
    Base = Region;
    OriginalTickFlush = reinterpret_cast<TickFlushFn>(&CountingFlush);
    HostGate = {};
    alignas(8) unsigned char driver[0x100]{};
    int clients = 2, rate = 30;
    memcpy(driver + ClientCount, &clients, 4);
    memcpy(driver + ServerTickRate, &rate, 4);
    auto grace = reinterpret_cast<volatile LONG*>(Fake(GraceCounter));
    *grace = 30;

    // A host at 300 frames per second flushes at the driver's rate.
    Flushes = 0;
    for (int frame = 0; frame < 300; ++frame) {
        double time = 50.0 + frame / 300.0;
        memcpy(driver + Time, &time, 8);
        TickFlush(driver, nullptr);
    }
    assert(Flushes >= 29 && Flushes <= 31);
    // Skipped ticks keep the original per-tick countdown of the timeout grace counter.
    assert(*grace == 0);

    // A joining client (it has a server connection) flushes on every tick.
    void* server = driver;
    memcpy(driver + ServerConnection, &server, 4);
    Flushes = 0;
    for (int frame = 0; frame < 100; ++frame) {
        double time = 60.0 + frame / 300.0;
        memcpy(driver + Time, &time, 8);
        TickFlush(driver, nullptr);
    }
    assert(Flushes == 100);

    // A driver with no client connections is not a host.
    void* none = nullptr;
    memcpy(driver + ServerConnection, &none, 4);
    clients = 0;
    memcpy(driver + ClientCount, &clients, 4);
    Flushes = 0;
    for (int frame = 0; frame < 10; ++frame) TickFlush(driver, nullptr);
    assert(Flushes == 10);
}

static void FireSound() {
    using namespace NativeFireSoundLoopVegas2;
    Base = Region;
    OriginalHandler = reinterpret_cast<HandlerFn>(&StubHandler);
    OriginalTick = reinterpret_cast<TickFn>(&StubTick);
    // PlayWeaponSound stand-in: records this and the sound (thiscall, ret 4).
    static volatile DWORD played = 0, playedOn = 0;
    unsigned char* play = Fake(PlayAddress);
    const DWORD soundAt = DWORD(reinterpret_cast<uintptr_t>(&played)), objectAt = DWORD(reinterpret_cast<uintptr_t>(&playedOn));
    unsigned char stub[]{0x8b, 0x44, 0x24, 0x04, 0xa3, 0, 0, 0, 0, 0x89, 0x0d, 0, 0, 0, 0, 0xc2, 0x04, 0x00};
    memcpy(stub + 5, &soundAt, 4);
    memcpy(stub + 11, &objectAt, 4);
    memcpy(play, stub, sizeof(stub));
    FlushInstructionCache(GetCurrentProcess(), play, sizeof(stub));

    alignas(4) static unsigned char sri[0x300]{}, pawn[0x400]{}, cues[0x300]{}, controller[16]{};
    static char loopsCue, endsCue, forceCue;
    void* vtable = Fake(SriVtable);
    memcpy(sri, &vtable, 4);
    sri[SriRole] = 1;  // simulated proxy on a client
    void* p = pawn; memcpy(sri + SriPawn, &p, 4);
    void* c = cues; memcpy(sri + SriCues, &c, 4);
    void* cue = &loopsCue; memcpy(cues + CueLoops, &cue, 4);
    cue = &endsCue; memcpy(cues + CueEnds, &cue, 4);
    cue = &forceCue; memcpy(cues + CueForceStop, &cue, 4);
    int health = 100; memcpy(pawn + PawnHealth, &health, 4);
    pawn[PawnFlashCount] = 3;

    // A remote pawn's loop cue starts a track; its end cue clears it.
    Handler(nullptr, nullptr, &loopsCue, 5, sri, 0, 0, 0, 0, 0, 0, 0);
    assert(HeardSound == &loopsCue && Find(sri));
    Handler(nullptr, nullptr, &endsCue, 5, sri, 0, 0, 0, 0, 0, 0, 0);
    assert(!Find(sri));

    // The player's own pawn (it has a Controller) and the host's objects are ignored.
    void* own = controller; memcpy(pawn + PawnController, &own, 4);
    Handler(nullptr, nullptr, &loopsCue, 5, sri, 0, 0, 0, 0, 0, 0, 0);
    assert(!Find(sri));
    void* noController = nullptr; memcpy(pawn + PawnController, &noController, 4);
    sri[SriRole] = RoleAuthority;
    Handler(nullptr, nullptr, &loopsCue, 5, sri, 0, 0, 0, 0, 0, 0, 0);
    assert(!Find(sri));
    sri[SriRole] = 1;

    // A shooter still firing keeps the loop.
    Handler(nullptr, nullptr, &loopsCue, 5, sri, 0, 0, 0, 0, 0, 0, 0);
    Tick(sri, nullptr, 0.016f);
    assert(played == 0 && Find(sri));

    // A dead shooter with no end cue gets the forced-stop cue (13) on its own object.
    health = 0; memcpy(pawn + PawnHealth, &health, 4);
    Tick(sri, nullptr, 0.016f);
    assert(played == SoundForceStop && playedOn == DWORD(reinterpret_cast<uintptr_t>(sri)));
    Handler(nullptr, nullptr, &forceCue, 5, sri, 0, 0, 0, 0, 0, 0, 0);
    assert(!Find(sri));

    // A torn-off pawn counts as dead; without a forced-stop cue the release cue (10) plays.
    health = 100; memcpy(pawn + PawnHealth, &health, 4);
    DWORD flags = TearOffBit; memcpy(pawn + PawnTearOff, &flags, 4);
    cue = nullptr; memcpy(cues + CueForceStop, &cue, 4);
    played = 0;
    Handler(nullptr, nullptr, &loopsCue, 5, sri, 0, 0, 0, 0, 0, 0, 0);
    Tick(sri, nullptr, 0.016f);
    assert(played == SoundEnds);
}

static int OriginalLimit = 0;
static int __fastcall StubMax(void*, void*, unsigned) { return OriginalLimit; }
static void __cdecl StubConstruct(void*) {}

static void PlayerLimit() {
    using namespace NativePlayerLimitVegas2;
    Base = Region;
    OriginalMax = reinterpret_cast<MaxFn>(&StubMax);
    OriginalConstruct = &StubConstruct;

    // Only Terrorist Hunt (mode 8) with the original co-op default of 4 is raised.
    OriginalLimit = 4;
    assert(MaxPlayers(nullptr, nullptr, TerroristHunt) == Raised);
    assert(MaxPlayers(nullptr, nullptr, 7) == 4);
    OriginalLimit = 6;
    assert(MaxPlayers(nullptr, nullptr, TerroristHunt) == 6);
    OriginalLimit = 16;
    assert(MaxPlayers(nullptr, nullptr, 0) == 16);

    // A constructed Terrorist Hunt game gets team size 16; other classes and values are kept.
    alignas(4) unsigned char game[MaxPlayersAllowed + 4]{};
    void* vtable = Fake(HuntVtable);
    memcpy(game, &vtable, 4);
    int value = 4; memcpy(game + MaxPlayersAllowed, &value, 4);
    Construct(game);
    memcpy(&value, game + MaxPlayersAllowed, 4);
    assert(value == Raised);
    value = 8; memcpy(game + MaxPlayersAllowed, &value, 4);
    Construct(game);
    memcpy(&value, game + MaxPlayersAllowed, 4);
    assert(value == 8);
    void* other = Fake(0x11db2454);
    memcpy(game, &other, 4);
    value = 4; memcpy(game + MaxPlayersAllowed, &value, 4);
    Construct(game);
    memcpy(&value, game + MaxPlayersAllowed, 4);
    assert(value == 4);
}

int main() {
    Region = static_cast<unsigned char*>(VirtualAlloc(nullptr, 0x1c00000, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE));
    assert(Region);
    HostRate();
    FireSound();
    PlayerLimit();

    // Installation rejects an image that is not the pinned Vegas 2 client.
    auto blank = static_cast<unsigned char*>(VirtualAlloc(nullptr, 0x2000, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    assert(blank);
    NativeHostNetworkRateVegas2::Verified = false;
    assert(!NativeHostNetworkRateVegas2::Install(blank));
    assert(!strcmp(NativeHostNetworkRateVegas2::Error, "unsupportedImage"));
    assert(MH_Initialize() == MH_OK);
    assert(!NativeFireSoundLoopVegas2::Install(blank));
    assert(!strcmp(NativeFireSoundLoopVegas2::Error, "unsupportedImage"));
    assert(!NativePlayerLimitVegas2::Install(blank));
    assert(!strcmp(NativePlayerLimitVegas2::Error, "unsupportedImage"));
    MH_Uninitialize();
    VirtualFree(blank, 0, MEM_RELEASE);
    VirtualFree(Region, 0, MEM_RELEASE);

    std::puts("PASS: Vegas 2 host flush rate and grace countdown, fire sound tracking, stop cues, player limits and image identity");
    return 0;
}
