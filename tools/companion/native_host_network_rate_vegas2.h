#pragma once

// Rainbow Six Vegas 2 network send rate of a listen-server host (a player
// hosting from Create Match). Same engine behavior as Vegas 1
// (docs/NATIVE_HOST_NETWORK_RATE.md); Vegas 2 addresses and layout are read from
// the unpacked R6Vegas2_Game.exe (docs/NATIVE_HOST_NETWORK_RATE_VEGAS2.md):
//  - UGameEngine::GetMaxTickRate 0x113d1b70 (UGameEngine vtable 0x11ed4d64 +0xdc)
//    limits a dedicated server to Clamp(NetDriver->NetServerMaxTickRate (+0x78),
//    10, 120) and a client to its net speed; a listen-server host has a driver
//    with no ServerConnection (+0x40) and gets 0: no limit.
//  - UNetDriver::TickFlush 0x112fb880 (UNetDriver vtable 0x11e3363c +0xe0 and
//    UTcpNetDriver vtable 0x11e3373c +0xe0) ticks ServerConnection (+0x40) and each
//    of ClientConnections (+0x34, count +0x38) through connection vtable +0xf0.
//    It first counts down the timeout grace counter 0x124c1700 (set to 30 by
//    0x10acf46a; the connection tick skips its timeout check while it is not 0).
//  - UNetConnection::Tick 0x11301420 (UTcpipConnection vtable 0x11e33974 +0xf0)
//    ends with: if TimeSensitive (+0x11c) or Driver (+0x48)->Time (+0x4c) -
//    LastSendTime (+0xe4) > Driver->KeepAliveTime (+0x60), FlushNet (vtable +0xec).
//    UTcpipConnection sets a 32-byte packet overhead (+0xa4, 0x10f99562) and
//    ConfiguredInternetSpeed is 10000 in PCKellerEngine.ini.
// This runs on the host only: TickFlush runs at most at the driver's own
// NetServerMaxTickRate with the original 10-120 clamp. The grace counter keeps its
// per-frame countdown on skipped ticks. Rendering and clients are unchanged.
#include <windows.h>
#include <cstring>
#include "native_host_network_rate.h"

namespace NativeHostNetworkRateVegas2 {
static_assert(sizeof(void*)==4,"Original retail images are x86");
constexpr unsigned Preferred=0x10900000;
constexpr unsigned TickFlushAddress=0x112fb880;
constexpr unsigned TcpDriverSlot=0x11e3381c,DriverSlot=0x11e3371c;
constexpr unsigned ConnectionTickAddress=0x11301420,ConnectionTickSlot=0x11e33a64;
constexpr unsigned MaxTickRateAddress=0x113d1b70,MaxTickRateSlot=0x11ed4e40;
constexpr unsigned GraceCounter=0x124c1700;
constexpr unsigned ClientCount=0x38,ServerConnection=0x40,Time=0x4c,ServerTickRate=0x78;

inline unsigned char* Base=nullptr;
inline bool Verified=false;
inline const char* Error="notInstalled";
inline volatile LONG Skipped=0,Flushed=0;
using TickFlushFn=void(__thiscall*)(void*);
inline TickFlushFn OriginalTickFlush=nullptr;
inline NativeHostNetworkRate::Gate HostGate{};

inline unsigned char* At(unsigned address){return Base+(address-Preferred);}
inline unsigned Relocated(unsigned address){return unsigned(reinterpret_cast<uintptr_t>(At(address)));}

inline void __fastcall TickFlush(void* object,void*){
    auto driver=static_cast<BYTE*>(object);
    using NativeHostNetworkRate::At;
    if(NativeHostNetworkRate::Readable(driver,ServerTickRate+4)&&!At<void*>(driver,ServerConnection)&&
       At<int>(driver,ClientCount)>0&&
       !NativeHostNetworkRate::Due(HostGate,driver,At<double>(driver,Time),At<int>(driver,ServerTickRate))){
        // The original counts this down once per engine tick.
        auto grace=reinterpret_cast<volatile LONG*>(Base+(GraceCounter-Preferred));
        if(*grace)--*grace;
        InterlockedIncrement(&Skipped);
        return;
    }
    InterlockedIncrement(&Flushed);
    OriginalTickFlush(object);
}

// UNetDriver::TickFlush, with the grace counter's address relocated.
inline bool TickFlushMatches(){
    unsigned char code[]{
        0x83,0x3d,0,0,0,0,0x00,0x56,0x57,0x8b,0xf9,0x74,0x07,0x83,0x2d,0,0,0,0,0x01,
        0x83,0x7f,0x40,0x00,0x74,0x0d,0x8b,0x4f,0x40,0x8b,0x01,0x8b,0x90,0xf0,0x00,0x00,0x00,0xff,0xd2,
        0x33,0xf6,0x39,0x77,0x38,0x7e,0x1a,0x8b,0xff,0x8b,0x47,0x34,0x8b,0x0c,0xb0,0x8b,0x11,
        0x8b,0x82,0xf0,0x00,0x00,0x00,0xff,0xd0,0x83,0xc6,0x01,0x3b,0x77,0x38,0x7c,0xe8,0x5f,0x5e,0xc3};
    const unsigned grace=Relocated(GraceCounter);
    memcpy(code+2,&grace,4);memcpy(code+15,&grace,4);
    return NativeHostNetworkRate::Matches(At(TickFlushAddress),code,sizeof(code));
}
// End of UNetConnection::Tick: TimeSensitive or keep-alive, then FlushNet.
constexpr unsigned char ConnectionFlush[]{
    0x39,0x9e,0x1c,0x01,0x00,0x00,0x75,0x17,0x8b,0x46,0x48,0xdd,0x40,0x4c,0xdc,0xa6,0xe4,0x00,0x00,0x00,
    0xd9,0x40,0x60,0xd9,0xc9,0xdf,0xf1,0xdd,0xd8,0x76,0x0c,0x8b,0x16,0x8b,0x82,0xec,0x00,0x00,0x00,0x8b,0xce,0xff,0xd0};
// GetMaxTickRate's dedicated-server branch: Clamp(NetDriver->NetServerMaxTickRate (+0x78), 10, 120).
constexpr unsigned char DedicatedRate[]{0x8b,0x40,0x78,0x83,0xf8,0x0a,0x7d};
constexpr unsigned char DedicatedCap[]{0x83,0xf8,0x78};

inline bool ImageMatches(unsigned char* base){
    if(!NativeHostNetworkRate::Readable(base,0x1000))return false;
    auto dos=reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if(dos->e_magic!=IMAGE_DOS_SIGNATURE||dos->e_lfanew<=0||dos->e_lfanew>0x800)return false;
    auto pe=reinterpret_cast<IMAGE_NT_HEADERS32*>(base+dos->e_lfanew);
    return pe->Signature==IMAGE_NT_SIGNATURE&&pe->FileHeader.Machine==IMAGE_FILE_MACHINE_I386&&
        pe->FileHeader.TimeDateStamp==0x484efa91u&&pe->OptionalHeader.SizeOfImage==0x1edb000u&&
        pe->OptionalHeader.ImageBase==Preferred;
}

inline bool Install(unsigned char* base){
    if(Verified)return true;
    if(!ImageMatches(base)){Error="unsupportedImage";return false;}
    Base=base;
    using NativeHostNetworkRate::Pointer;
    using NativeHostNetworkRate::Contains;
    if(Pointer(At(TcpDriverSlot))!=At(TickFlushAddress)||Pointer(At(DriverSlot))!=At(TickFlushAddress)||
       !TickFlushMatches()||
       Pointer(At(ConnectionTickSlot))!=At(ConnectionTickAddress)||
       !Contains(At(ConnectionTickAddress),0x800,ConnectionFlush,sizeof(ConnectionFlush))||
       Pointer(At(MaxTickRateSlot))!=At(MaxTickRateAddress)||
       !Contains(At(MaxTickRateAddress),0x100,DedicatedRate,sizeof(DedicatedRate))||
       !Contains(At(MaxTickRateAddress),0x100,DedicatedCap,sizeof(DedicatedCap))){
        Error="codeMismatch";return false;
    }
    // The network driver reaches TickFlush only through its vtable.
    OriginalTickFlush=reinterpret_cast<TickFlushFn>(At(TickFlushAddress));
    BYTE* slot=At(TcpDriverSlot);
    if(!NativeHostNetworkRate::WriteSlot(slot,reinterpret_cast<void*>(TickFlush))||
       Pointer(slot)!=reinterpret_cast<BYTE*>(TickFlush)){
        NativeHostNetworkRate::WriteSlot(slot,At(TickFlushAddress));
        Error="nativeTableWriteFailed";return false;
    }
    Verified=true;Error="ok";
    return true;
}
} // namespace NativeHostNetworkRateVegas2
