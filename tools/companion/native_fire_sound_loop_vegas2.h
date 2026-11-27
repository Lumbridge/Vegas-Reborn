#pragma once

// Rainbow Six Vegas 2 stuck full-auto firing sound on online clients. Same cause
// as Vegas 1 (docs/NATIVE_FIRE_SOUND_LOOP.md); Vegas 2 addresses and layout are
// read from the unpacked R6Vegas2_Game.exe and the cooked Engine.uppc
// (docs/NATIVE_FIRE_SOUND_LOOP_VEGAS2.md):
//  - AR6SoundReplicationInfo (vtable 0x11d7d97c, always relevant through +0x258)
//    has its pawn at +0x264 and its cue table at +0x274. PlayWeaponSound
//    (vtable +0x630, 0x10cc1ae0) plays the cue table's +0x284 when full-auto
//    fire starts (6), +0x288 on release (10) and +0x28c on a forced stop (13),
//    each through AActor::PlaySound 0x11352360 with replication on.
//  - On the host, PlaySound sends each cue to every client's PlayerController
//    through HearSound 0x11352520 (audibility checked when it is sent), which
//    calls ClientHearSound. Engine.uppc gives PlayerController.ClientHearSound
//    the flags 0x01020540: a net function that is not reliable.
//  - On a client, execClientHearSound 0x110ec230 calls the handler 0x11352630
//    (PlayerController vtable +0x79c) with (sound, slot, source, location, ...).
//    A sound played locally reaches the same handler through
//    eventClientHearSound 0x1134f510.
//  - The pawn's FlashCount (+0x2b3) is set by IncrementFlashCount 0x1116fbe0 and
//    ClearFlashCount 0x1116fc30, which force the next net update, and is sent to
//    every client but the owner by the pawn's replication list (0x1111ec1c).
//    Controller is +0x270 (IsLocallyControlled 0x1116e090), Health +0x354
//    (AR6Pawn::IsAlive 0x10a64ee0), bTearOff +0x44 bit 0x100 and Role +0x6e
//    (actor channel 0x112ffd6d; ROLE_Authority is 3).
//  - AActor::Tick 0x11367d.. calls the per-frame vtable +0x250 for every role;
//    the SRI's is 0x10cc18b0.
// The tracking and stop decision are Vegas 1's (NativeFireSoundLoop::Decide).
// A stop plays the release cue through the object's own PlayWeaponSound(10), or
// the forced-stop cue (13) for a dead pawn. On a client these play locally and
// send nothing: PlaySound's send loop covers only the client connections, of
// which a client has none.
#include <windows.h>
#include <cstring>
#include "MinHook.h"
#include "native_fire_sound_loop.h"

namespace NativeFireSoundLoopVegas2 {
static_assert(sizeof(void*)==4,"Original retail images are x86");
constexpr unsigned Preferred=0x10900000;
constexpr unsigned HandlerAddress=0x11352630,HandlerSlot=0x11d22728; // APlayerController vtable +0x79c
constexpr unsigned SriVtable=0x11d7d97c,TickSlot=0x11d7dbcc,TickAddress=0x10cc18b0,TickSuper=0x11367840;
constexpr unsigned PlaySlot=0x11d7dfac,PlayAddress=0x10cc1ae0,StopAddress=0x10cc2080;
constexpr unsigned SriRole=0x6e,SriPawn=0x264,SriCues=0x274;
constexpr unsigned CueLoops=0x284,CueEnds=0x288,CueForceStop=0x28c;
constexpr unsigned PawnTearOff=0x44,PawnFlashCount=0x2b3,PawnController=0x270,PawnHealth=0x354;
constexpr DWORD TearOffBit=0x100;
constexpr BYTE RoleAuthority=3,SoundEnds=10,SoundForceStop=13;
constexpr int Slots=64;

inline unsigned char* Base=nullptr;
inline bool Verified=false;
inline const char* Error="notInstalled";
inline volatile LONG Stops=0;
inline NativeFireSoundLoop::Track Tracks[Slots]{};
using HandlerFn=void*(__thiscall*)(void*,void*,unsigned,void*,float,float,float,int,int,int,int);
using TickFn=void(__thiscall*)(void*,float);
using PlayFn=int(__thiscall*)(void*,int);
inline HandlerFn OriginalHandler=nullptr;
inline TickFn OriginalTick=nullptr;

inline unsigned char* At(unsigned address){return Base+(address-Preferred);}
using NativeFireSoundLoop::Readable;
using NativeFireSoundLoop::Pointer;

// An SRI on a client whose pawn is not controlled here. The host and the shooter
// hear their own local cues.
inline BYTE* RemotePawn(BYTE* sri){
    if(!Readable(sri,SriCues+4)||Pointer(sri)!=At(SriVtable)||sri[SriRole]==RoleAuthority)return nullptr;
    BYTE* pawn=Pointer(sri+SriPawn);
    if(!pawn||!Readable(pawn,PawnHealth+4)||Pointer(pawn+PawnController))return nullptr;
    return pawn;
}
inline NativeFireSoundLoop::Track* Find(BYTE* sri){
    for(auto& track:Tracks)if(track.sri==sri)return &track;
    return nullptr;
}
inline void Begin(BYTE* sri,BYTE* pawn){
    auto* track=Find(sri);
    if(!track)for(auto& slot:Tracks)if(!slot.sri){track=&slot;break;}
    if(!track)return;
    const DWORD now=GetTickCount();
    const BYTE flashCount=pawn[PawnFlashCount];
    *track=NativeFireSoundLoop::Track{sri,pawn,now,now,now,0,flashCount,flashCount!=0,0};
}
inline void* __fastcall Handler(void* controller,void*,void* sound,unsigned slot,void* source,float x,float y,float z,
                                int a7,int a8,int a9,int a10){
    void* result=OriginalHandler(controller,sound,slot,source,x,y,z,a7,a8,a9,a10);
    auto sri=static_cast<BYTE*>(source);
    BYTE* pawn=sound?RemotePawn(sri):nullptr;
    BYTE* cues=pawn?Pointer(sri+SriCues):nullptr;
    if(!cues||!Readable(cues,CueForceStop+4))return result;
    if(sound==Pointer(cues+CueLoops))Begin(sri,pawn);
    else if(sound==Pointer(cues+CueEnds)||sound==Pointer(cues+CueForceStop)){
        if(auto* track=Find(sri))*track=NativeFireSoundLoop::Track{};
    }
    return result;
}
inline void __fastcall Tick(void* object,void*,float delta){
    OriginalTick(object,delta);
    auto sri=static_cast<BYTE*>(object);
    auto* track=Find(sri);
    if(!track)return;
    BYTE* pawn=RemotePawn(sri);
    if(!pawn||pawn!=track->pawn){*track=NativeFireSoundLoop::Track{};return;}
    DWORD flags=0;memcpy(&flags,pawn+PawnTearOff,4);
    int health=0;memcpy(&health,pawn+PawnHealth,4);
    const bool dead=health<=0||(flags&TearOffBit);
    using NativeFireSoundLoop::Decision;
    const Decision decision=NativeFireSoundLoop::Decide(*track,GetTickCount(),pawn[PawnFlashCount],dead);
    if(decision==Decision::Forget){*track=NativeFireSoundLoop::Track{};return;}
    if(decision==Decision::Wait)return;
    // The handler clears the track when the local stop cue is heard. A stop the
    // listener is too far away to hear is retried, as the loop is still playing.
    BYTE* cues=Pointer(sri+SriCues);
    const bool forced=dead&&cues&&Readable(cues,CueForceStop+4)&&Pointer(cues+CueForceStop);
    reinterpret_cast<PlayFn>(At(PlayAddress))(sri,forced?SoundForceStop:SoundEnds);
    InterlockedIncrement(&Stops);
}

inline bool Matches(unsigned address,const unsigned char* bytes,size_t size){
    return Readable(At(address),size)&&!memcmp(At(address),bytes,size);
}
inline bool CodeReady(unsigned char* base){
    Base=base;
    constexpr unsigned char handler[]{0x83,0xec,0x0c,0x53,0x8b,0x5c,0x24,0x14,0x55,0x56,0x33,0xf6,0x85,0xdb,0x57,0x8b,0xe9};
    return Matches(HandlerAddress,handler,sizeof(handler));
}
inline bool ImageMatches(unsigned char* base){
    if(!Readable(base,0x1000))return false;
    auto dos=reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if(dos->e_magic!=IMAGE_DOS_SIGNATURE||dos->e_lfanew<=0||dos->e_lfanew>0x800)return false;
    auto pe=reinterpret_cast<IMAGE_NT_HEADERS32*>(base+dos->e_lfanew);
    return pe->Signature==IMAGE_NT_SIGNATURE&&pe->FileHeader.Machine==IMAGE_FILE_MACHINE_I386&&
        pe->FileHeader.TimeDateStamp==0x484efa91u&&pe->OptionalHeader.SizeOfImage==0x1edb000u&&
        pe->OptionalHeader.ImageBase==Preferred;
}
inline bool RelativeTo(unsigned address,unsigned char opcode,const void* target){
    const unsigned char* at=At(address);
    if(!Readable(at,5)||at[0]!=opcode)return false;
    LONG relative=0;memcpy(&relative,at+1,4);return at+5+relative==target;
}

inline bool Install(unsigned char* base){
    if(Verified)return true;
    if(!ImageMatches(base)){Error="unsupportedImage";return false;}
    if(!CodeReady(base)){Error="codeMismatch";return false;}
    // Per-frame override: Super (float delta), pawn at +0x264, Role at +0x6e.
    constexpr unsigned char tick[]{0xd9,0x44,0x24,0x04,0x56,0x51,0xd9,0x1c,0x24,0x8b,0xf1,0xe8};
    constexpr unsigned char tickPawn[]{0x8b,0x86,0x64,0x02,0x00,0x00};
    constexpr unsigned char tickRole[]{0x80,0x7e,0x6e,0x03};
    // StopWeaponSound: cue table check, then PlayWeaponSound(10) through vtable +0x630.
    constexpr unsigned char stop[]{0x83,0xb9,0x74,0x02,0x00,0x00,0x00,0x74,0x0c,0x8b,0x01,0x8b,0x90,0x30,0x06,0x00,0x00,0x6a,0x0a,0xff,0xd2,0xc3};
    // PlayWeaponSound: cue table check, then cases 6, 10 and 13 read +0x284, +0x288 and +0x28c.
    constexpr unsigned char play[]{0x51,0x56,0x57,0x33,0xff,0x8b,0xf1,0x39,0xbe,0x74,0x02,0x00,0x00};
    constexpr unsigned char loops[]{0x8b,0x8e,0x74,0x02,0x00,0x00,0x8b,0x91,0x84,0x02,0x00,0x00};
    constexpr unsigned char ends[]{0x8b,0x8e,0x74,0x02,0x00,0x00,0x8b,0x81,0x88,0x02,0x00,0x00};
    constexpr unsigned char forced[]{0x8b,0x96,0x74,0x02,0x00,0x00,0x8b,0x82,0x8c,0x02,0x00,0x00};
    // Pawn replication sends FlashCount; IsLocallyControlled reads Controller;
    // AR6Pawn::IsAlive reads Health; the actor channel reads bTearOff.
    constexpr unsigned char flashCount[]{0x8a,0x95,0xb3,0x02,0x00,0x00,0x3a,0x93,0xb3,0x02,0x00,0x00};
    constexpr unsigned char controller[]{0x83,0xbe,0x70,0x02,0x00,0x00,0x00};
    constexpr unsigned char health[]{0x83,0xbe,0x54,0x03,0x00,0x00,0x00};
    constexpr unsigned char tearOff[]{0xf7,0x40,0x44,0x00,0x01,0x00,0x00};
    if(Pointer(At(HandlerSlot))!=At(HandlerAddress)||
       !Matches(TickAddress,tick,sizeof(tick))||!RelativeTo(TickAddress+11,0xe8,At(TickSuper))||
       !Matches(TickAddress+0x10,tickPawn,sizeof(tickPawn))||!Matches(0x10cc192d,tickRole,sizeof(tickRole))||
       Pointer(At(TickSlot))!=At(TickAddress)||Pointer(At(SriVtable))==nullptr||
       Pointer(At(PlaySlot))!=At(PlayAddress)||!Matches(PlayAddress,play,sizeof(play))||
       !Matches(StopAddress,stop,sizeof(stop))||
       !Matches(0x10cc1e7a,loops,sizeof(loops))||!Matches(0x10cc1f4f,ends,sizeof(ends))||
       !Matches(0x10cc1f5d,forced,sizeof(forced))||!Matches(0x1111ec1c,flashCount,sizeof(flashCount))||
       !Matches(0x1116e0a9,controller,sizeof(controller))||!Matches(0x10a64ee3,health,sizeof(health))||
       !Matches(0x112ffd6d,tearOff,sizeof(tearOff))){
        Error="codeMismatch";return false;
    }
    if(MH_CreateHook(At(HandlerAddress),reinterpret_cast<void*>(Handler),reinterpret_cast<void**>(&OriginalHandler))!=MH_OK){
        Error="hookCreationFailed";return false;
    }
    if(MH_EnableHook(At(HandlerAddress))!=MH_OK){
        MH_RemoveHook(At(HandlerAddress));
        Error="hookEnableFailed";return false;
    }
    // The SRI reaches this override only through its vtable slot.
    OriginalTick=reinterpret_cast<TickFn>(At(TickAddress));
    BYTE* slot=At(TickSlot);
    if(!NativeFireSoundLoop::WriteSlot(slot,reinterpret_cast<void*>(Tick))||Pointer(slot)!=reinterpret_cast<BYTE*>(Tick)){
        NativeFireSoundLoop::WriteSlot(slot,At(TickAddress));
        MH_DisableHook(At(HandlerAddress));MH_RemoveHook(At(HandlerAddress));
        Error="nativeTableWriteFailed";return false;
    }
    Verified=true;Error="ok";
    return true;
}
} // namespace NativeFireSoundLoopVegas2
