#pragma once

// Stuck full-auto firing sound on online clients.
//
// Each pawn has an always-relevant AR6SoundReplicationInfo (SRI). When a player
// starts full-auto fire the host plays the weapon's Loops cue in sound slot 5
// (AR6SoundReplicationInfo::PlayWeaponSound(6)); on release it plays the Ends
// cue in the same slot (10), and on weapon detach/destroy the ForceStop cue (13).
// Remote clients receive each cue only through PlayerController.ClientHearSound,
// which is not reliable and is distance-culled on the host when it is sent. A
// lost or culled Ends leaves the Loops cue playing until the pawn is destroyed.
//
// This runs only on clients, for pawns they do not control. It notes the Loops
// and Ends/ForceStop cues as they arrive. When the shooter's replicated FlashCount
// (forced into the next net update by Increment/ClearFlashCount on every shot and
// on release) shows firing has stopped, or the pawn died, and no Ends arrived,
// it calls the SRI's own StopWeaponSound/ForceStopWeaponSound. On a client those
// play the stop cue locally in slot 5 and send nothing.
#include <windows.h>
#include <cstdint>
#include <cstring>
#include "MinHook.h"

namespace NativeFireSoundLoop {
static_assert(sizeof(void*)==4,"Original retail images are x86");
static constexpr unsigned HandlerRva=0x232980;     // APlayerController ClientHearSound handler (vt 0x6c0)
static constexpr unsigned SriVtableRva=0x177b720;  // AR6SoundReplicationInfo vtable
static constexpr unsigned TickSlotRva=0x177b920;   // its Tick slot (+0x200)
static constexpr unsigned TickRva=0x911e90;
static constexpr unsigned PlayRva=0x911f90;        // PlayWeaponSound(byte)
static constexpr unsigned StopRva=0x9123f0;        // StopWeaponSound: PlayWeaponSound(10)
static constexpr unsigned ForceStopRva=0x912410;   // ForceStopWeaponSound: PlayWeaponSound(13)
// Field offsets, each read by the original code checked in Install.
static constexpr unsigned SriRole=0x72,SriPawn=0x24c,SriCues=0x25c;
static constexpr unsigned CueLoops=0x26c,CueEnds=0x270,CueForceStop=0x274;
static constexpr unsigned PawnTearOff=0x48,PawnFlashCount=0x297,PawnController=0x258,PawnHealth=0x338;
static constexpr BYTE RoleAuthority=3;
static constexpr DWORD StoppedMs=750,FrozenMs=2000,UnseenMs=1000,RetryMs=2000;
static constexpr int MaxAttempts=15,Slots=64;
static BYTE* Image=nullptr;
static bool Installed=false,Verified=false;
static volatile LONG ErrorCode=0,Stops=0;
struct Track {BYTE* sri;BYTE* pawn;DWORD start,lastFire,lastChange,nextAttempt;BYTE flashCount;bool seenFire;int attempts;};
static Track Tracks[Slots]{};
using HandlerFn=void*(__thiscall*)(void*,void*,unsigned,void*,float,float,float,int,int,int,int);
using TickFn=void(__thiscall*)(void*,float);
using StopFn=void(__thiscall*)(void*);
static HandlerFn OriginalHandler=nullptr;
static TickFn OriginalTick=nullptr;
static const char* LastError(){
    switch(InterlockedCompareExchange(&ErrorCode,0,0)){
        case 0:return "none";
        case 1:return "imageIdentityMismatch";
        case 2:return "nativeSignatureMismatch";
        case 3:return "hookCreationFailed";
        case 4:return "hookActivationFailed";
        case 5:return "nativeTableWriteFailed";
        default:return "unknown";
    }
}
static bool Readable(const void* pointer,size_t bytes){
    MEMORY_BASIC_INFORMATION info{};
    if(!pointer||!bytes||!VirtualQuery(pointer,&info,sizeof(info))||info.State!=MEM_COMMIT||
       (info.Protect&(PAGE_GUARD|PAGE_NOACCESS)))return false;
    auto at=reinterpret_cast<uintptr_t>(pointer),first=reinterpret_cast<uintptr_t>(info.BaseAddress);
    return at>=first&&at-first<=info.RegionSize&&bytes<=info.RegionSize-(at-first);
}
template<class T>static T At(const BYTE* base,unsigned offset){T value{};memcpy(&value,base+offset,sizeof(T));return value;}
static BYTE* Pointer(const void* at){return Readable(at,4)?*reinterpret_cast<BYTE* const*>(at):nullptr;}
// An SRI on a client whose pawn is not controlled here (remote pawns have no
// Controller on clients). The host and the shooter hear their own local cues.
static BYTE* RemotePawn(BYTE* sri){
    if(!Readable(sri,SriCues+4)||Pointer(sri)!=Image+SriVtableRva||sri[SriRole]==RoleAuthority)return nullptr;
    BYTE* pawn=Pointer(sri+SriPawn);
    if(!pawn||!Readable(pawn,PawnHealth+4)||Pointer(pawn+PawnController))return nullptr;
    return pawn;
}
static Track* Find(BYTE* sri){
    for(auto& track:Tracks)if(track.sri==sri)return &track;
    return nullptr;
}
static void Begin(BYTE* sri,BYTE* pawn){
    Track* track=Find(sri);
    if(!track)for(auto& slot:Tracks)if(!slot.sri){track=&slot;break;}
    if(!track)return;
    const DWORD now=GetTickCount();
    const BYTE flashCount=pawn[PawnFlashCount];
    *track=Track{sri,pawn,now,now,now,0,flashCount,flashCount!=0,0};
}
static void* __fastcall Handler(void* controller,void*,void* sound,unsigned slot,void* source,float x,float y,float z,
                                int stopWithOwner,int useOwnerLocation,int triangle,int zone){
    void* result=OriginalHandler(controller,sound,slot,source,x,y,z,stopWithOwner,useOwnerLocation,triangle,zone);
    auto sri=static_cast<BYTE*>(source);
    BYTE* pawn=sound?RemotePawn(sri):nullptr;
    BYTE* cues=pawn?Pointer(sri+SriCues):nullptr;
    if(!cues||!Readable(cues,CueForceStop+4))return result;
    if(sound==Pointer(cues+CueLoops))Begin(sri,pawn);
    else if(sound==Pointer(cues+CueEnds)||sound==Pointer(cues+CueForceStop)){
        if(Track* track=Find(sri))*track=Track{};
    }
    return result;
}
enum class Decision {Wait,Stop,Forget};
// One observation of a tracked loop. Firing changes FlashCount on every shot and
// release sets it to 0; a value that stops changing means the shooter stopped.
static Decision Decide(Track& track,DWORD now,BYTE flashCount,bool dead){
    if(flashCount!=track.flashCount){track.flashCount=flashCount;track.lastChange=now;}
    if(flashCount){track.lastFire=now;track.seenFire=true;}
    const bool stopped=dead||
        (track.seenFire&&!flashCount&&now-track.lastFire>=StoppedMs)||
        (!track.seenFire&&!flashCount&&now-track.start>=UnseenMs)||
        (now-track.lastChange>=FrozenMs);
    if(!stopped||(track.attempts&&LONG(now-track.nextAttempt)<0))return Decision::Wait;
    if(track.attempts>=MaxAttempts)return Decision::Forget;
    ++track.attempts;track.nextAttempt=now+RetryMs;
    return Decision::Stop;
}
static void __fastcall Tick(void* object,void*,float delta){
    OriginalTick(object,delta);
    auto sri=static_cast<BYTE*>(object);
    Track* track=Find(sri);
    if(!track)return;
    BYTE* pawn=RemotePawn(sri);
    if(!pawn||pawn!=track->pawn){*track=Track{};return;}
    const bool dead=At<int>(pawn,PawnHealth)<=0||(At<DWORD>(pawn,PawnTearOff)&0x40);
    const Decision decision=Decide(*track,GetTickCount(),pawn[PawnFlashCount],dead);
    if(decision==Decision::Forget){*track=Track{};return;}
    if(decision==Decision::Wait)return;
    // The handler clears the track when the local stop cue is heard. A stop the
    // listener is too far away to hear is retried, as the loop is still playing.
    BYTE* cues=Pointer(sri+SriCues);
    const bool ends=cues&&Readable(cues,CueForceStop+4)&&Pointer(cues+CueEnds);
    reinterpret_cast<StopFn>(Image+(dead||!ends?ForceStopRva:StopRva))(sri);
    InterlockedIncrement(&Stops);
}
static bool RelativeTo(const BYTE* at,BYTE opcode,const void* target){
    if(!Readable(at,5)||at[0]!=opcode)return false;
    LONG relative=0;memcpy(&relative,at+1,4);return at+5+relative==target;
}
static bool WriteSlot(BYTE* slot,const void* value){
    DWORD protection=0;
    if(!VirtualProtect(slot,4,PAGE_READWRITE,&protection))return false;
    InterlockedExchange(reinterpret_cast<volatile LONG*>(slot),LONG(reinterpret_cast<uintptr_t>(value)));
    DWORD ignored=0;
    return VirtualProtect(slot,4,protection,&ignored)!=FALSE;
}
static bool Matches(BYTE* base,unsigned rva,const BYTE* bytes,size_t size){
    return Readable(base+rva,size)&&!memcmp(base+rva,bytes,size);
}
static bool Install(BYTE* base){
    if(Installed)return Verified;
    if(!Readable(base,0x1000)){InterlockedExchange(&ErrorCode,1);return false;}
    auto dos=reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if(dos->e_magic!=IMAGE_DOS_SIGNATURE||dos->e_lfanew<=0||dos->e_lfanew>0x800){InterlockedExchange(&ErrorCode,1);return false;}
    auto pe=reinterpret_cast<IMAGE_NT_HEADERS32*>(base+dos->e_lfanew);
    if(pe->Signature!=IMAGE_NT_SIGNATURE||pe->FileHeader.Machine!=IMAGE_FILE_MACHINE_I386||
       pe->OptionalHeader.ImageBase!=0x10900000u||pe->OptionalHeader.SizeOfImage!=0x1f56000u||
       pe->FileHeader.TimeDateStamp!=0x47106421u){
        InterlockedExchange(&ErrorCode,1);return false;
    }
    Image=base;
    const BYTE handler[]{0x83,0xec,0x0c,0x53,0x8b,0x5c,0x24,0x14,0x55,0x56,0x33,0xf6,0x85,0xdb,0x57,0x8b,0xe9};
    // Tick: Super::Tick(delta); pawn at +0x24c; Role at +0x72.
    const BYTE tick[]{0x8b,0x44,0x24,0x04,0x56,0x50,0x8b,0xf1,0xe8};
    const BYTE tickPawn[]{0x8b,0x86,0x4c,0x02,0,0};
    const BYTE tickRole[]{0x80,0x7e,0x72,0x03};
    // StopWeaponSound / ForceStopWeaponSound: cue table check, PlayWeaponSound(10 / 13).
    const BYTE stop[]{0x83,0xb9,0x5c,0x02,0,0,0,0x74,0x07,0x6a,0x0a};
    const BYTE forceStop[]{0x83,0xb9,0x5c,0x02,0,0,0,0x74,0x07,0x6a,0x0d};
    // PlayWeaponSound cases 6, 10, 13 read Loops, Ends and ForceStop from the cue table.
    const BYTE loops[]{0x8b,0x86,0x5c,0x02,0,0,0x8b,0x88,0x6c,0x02,0,0};
    const BYTE ends[]{0x8b,0x96,0x5c,0x02,0,0,0x8b,0x82,0x70,0x02,0,0};
    const BYTE forced[]{0x8b,0x96,0x5c,0x02,0,0,0x8b,0x82,0x74,0x02,0,0};
    // APawn::FlashCountUpdated reads FlashCount; replication reads Health and bTearOff;
    // IsLocallyControlled reads Controller.
    const BYTE flashCount[]{0x80,0xb9,0x97,0x02,0,0,0};
    const BYTE health[]{0x8b,0x83,0x38,0x03,0,0};
    const BYTE tearOff[]{0x8b,0x55,0x48,0x33,0x57,0x48,0xf6,0xc2,0x40};
    const BYTE controller[]{0x83,0xbe,0x58,0x02,0,0,0};
    BYTE* slot=base+TickSlotRva;
    if(!Matches(base,HandlerRva,handler,sizeof(handler))||!Matches(base,TickRva,tick,sizeof(tick))||
       !Matches(base,TickRva+0xd,tickPawn,sizeof(tickPawn))||!Matches(base,0x911ef5,tickRole,sizeof(tickRole))||
       !Matches(base,StopRva,stop,sizeof(stop))||!RelativeTo(base+StopRva+11,0xe8,base+PlayRva)||
       !Matches(base,ForceStopRva,forceStop,sizeof(forceStop))||!RelativeTo(base+ForceStopRva+11,0xe8,base+PlayRva)||
       !Matches(base,0x91225e,loops,sizeof(loops))||!Matches(base,0x9122f6,ends,sizeof(ends))||
       !Matches(base,0x912366,forced,sizeof(forced))||!Matches(base,0x26a8c0,flashCount,sizeof(flashCount))||
       !Matches(base,0x211c4b,health,sizeof(health))||!Matches(base,0x211812,tearOff,sizeof(tearOff))||
       !Matches(base,0x268f02,controller,sizeof(controller))||
       Pointer(slot)!=base+TickRva||Pointer(base+SriVtableRva+0x208)!=base+0x911f80){
        InterlockedExchange(&ErrorCode,2);return false;
    }
    if(MH_CreateHook(base+HandlerRva,reinterpret_cast<void*>(Handler),reinterpret_cast<void**>(&OriginalHandler))!=MH_OK){
        InterlockedExchange(&ErrorCode,3);return false;
    }
    if(MH_EnableHook(base+HandlerRva)!=MH_OK||!RelativeTo(base+HandlerRva,0xe9,reinterpret_cast<void*>(Handler))){
        MH_DisableHook(base+HandlerRva);MH_RemoveHook(base+HandlerRva);
        InterlockedExchange(&ErrorCode,4);return false;
    }
    // AR6SoundReplicationInfo reaches its Tick only through this vtable slot.
    OriginalTick=reinterpret_cast<TickFn>(base+TickRva);
    if(!WriteSlot(slot,reinterpret_cast<void*>(Tick))||Pointer(slot)!=reinterpret_cast<BYTE*>(Tick)){
        WriteSlot(slot,base+TickRva);
        MH_DisableHook(base+HandlerRva);MH_RemoveHook(base+HandlerRva);
        InterlockedExchange(&ErrorCode,5);return false;
    }
    Verified=true;Installed=true;
    return true;
}
} // namespace NativeFireSoundLoop
