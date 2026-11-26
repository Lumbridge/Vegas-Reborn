#pragma once

// Network send rate of a listen-server host (a player hosting from Create Match).
//
// Original engine behavior (docs/NATIVE_HOST_NETWORK_RATE.md):
//  - UGameEngine::GetMaxTickRate limits a dedicated server to the driver's
//    NetServerMaxTickRate (30 in the shipped ini, clamped 10-120) and a joining
//    client to its net speed / MoveRepSize (at most 90). A listen-server host gets
//    0: no limit, so it ticks at its full frame rate.
//  - UNetDriver::TickFlush ticks every client connection once per engine tick.
//    UNetConnection::Tick flushes a packet on every tick that sent a bunch or owes
//    an ack, and FlushNet charges each packet's 32-byte PacketOverhead (set by
//    UIpConnection) plus its payload against the connection's rate. An internet
//    connection's rate is ConfiguredInternetSpeed (10000 bytes per second).
//  - A host running at a few hundred frames per second therefore sends a small
//    packet to each client almost every frame. At 300 fps the per-packet overhead
//    alone is 9600 of each client's 10000 bytes per second, and movement, doors,
//    sounds and other replication are delayed or dropped for clients.
//
// This runs on the host only, when its network driver has client connections and
// no server connection. It lets UNetDriver::TickFlush run at most at the driver's
// own NetServerMaxTickRate, with the same 10-120 clamp the original applies to a
// dedicated server. Replication between flushes is batched into fuller packets by
// the original SendRawBunch, which flushes on its own when a packet is full.
// Rendering and the host's own game are not limited. Clients are unchanged.
#include <windows.h>
#include <cstdint>
#include <cstring>

namespace NativeHostNetworkRate {
static_assert(sizeof(void*)==4,"Original retail images are x86");
static constexpr unsigned TickFlushRva=0x3be3c0;        // UNetDriver::TickFlush
static constexpr unsigned TcpDriverSlotRva=0x167cd64;   // UTcpNetDriver vtable +0xe4
static constexpr unsigned DriverSlotRva=0x164c77c;      // UNetDriver vtable +0xe4
static constexpr unsigned ConnectionTickRva=0x3c5620;   // UNetConnection::Tick
static constexpr unsigned ConnectionTickSlotRva=0x167ce7c; // UIpConnection vtable +0xf4
static constexpr unsigned MaxTickRateRva=0x492110;      // UGameEngine::GetMaxTickRate
static constexpr unsigned MaxTickRateSlotRva=0x1667548; // UGameEngine vtable +0xe0
// UNetDriver fields, each read by the original code checked in Install.
static constexpr unsigned ClientConnections=0x38,ClientCount=0x3c,ServerConnection=0x44,Time=0x50,ServerTickRate=0x7c;
static constexpr int MinRate=10,MaxRate=120;
static BYTE* Image=nullptr;
static bool Installed=false,Verified=false;
static volatile LONG ErrorCode=6,Skipped=0,Flushed=0;
using TickFlushFn=void(__thiscall*)(void*);
static TickFlushFn OriginalTickFlush=nullptr;
static const char* LastError(){
    switch(InterlockedCompareExchange(&ErrorCode,0,0)){
        case 0:return "none";
        case 1:return "imageIdentityMismatch";
        case 2:return "nativeSignatureMismatch";
        case 5:return "nativeTableWriteFailed";
        case 6:return "notInstalled";
        default:return "unknown";
    }
}

// When the host's next flush is due, from the driver's own clock (seconds).
struct Gate {const void* driver;double next;};
static int Rate(int configured){return configured<MinRate?MinRate:configured>MaxRate?MaxRate:configured;}
// True when TickFlush should run at this driver time. A new driver, or a clock
// that went back (a new level), starts a new cadence with an immediate flush.
static bool Due(Gate& gate,const void* driver,double time,int configured){
    const double interval=1.0/Rate(configured);
    if(gate.driver!=driver||time<gate.next-interval){gate=Gate{driver,time+interval};return true;}
    if(time<gate.next)return false;
    gate.next+=interval;
    if(gate.next<=time)gate.next=time+interval;
    return true;
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

static Gate HostGate{};
static void __fastcall TickFlush(void* object,void*){
    auto driver=static_cast<BYTE*>(object);
    // Host side only: client connections and no connection to a server.
    if(Readable(driver,ServerTickRate+4)&&!At<void*>(driver,ServerConnection)&&At<int>(driver,ClientCount)>0&&
       !Due(HostGate,driver,At<double>(driver,Time),At<int>(driver,ServerTickRate))){
        InterlockedIncrement(&Skipped);
        return;
    }
    InterlockedIncrement(&Flushed);
    OriginalTickFlush(object);
}

static bool Matches(const BYTE* at,const BYTE* bytes,size_t size){return Readable(at,size)&&!memcmp(at,bytes,size);}
static bool Contains(const BYTE* at,size_t range,const BYTE* bytes,size_t size){
    if(!Readable(at,range+size))return false;
    for(size_t i=0;i<=range;++i)if(!memcmp(at+i,bytes,size))return true;
    return false;
}
static bool WriteSlot(BYTE* slot,const void* value){
    DWORD protection=0;
    if(!VirtualProtect(slot,4,PAGE_READWRITE,&protection))return false;
    InterlockedExchange(reinterpret_cast<volatile LONG*>(slot),LONG(reinterpret_cast<uintptr_t>(value)));
    DWORD ignored=0;
    return VirtualProtect(slot,4,protection,&ignored)!=FALSE;
}
static bool IdentityMatches(BYTE* base){
    if(!Readable(base,0x1000))return false;
    auto dos=reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if(dos->e_magic!=IMAGE_DOS_SIGNATURE||dos->e_lfanew<=0||dos->e_lfanew>0x800)return false;
    auto pe=reinterpret_cast<IMAGE_NT_HEADERS32*>(base+dos->e_lfanew);
    return pe->Signature==IMAGE_NT_SIGNATURE&&pe->FileHeader.Machine==IMAGE_FILE_MACHINE_I386&&
        pe->OptionalHeader.ImageBase==0x10900000u&&pe->OptionalHeader.SizeOfImage==0x1f56000u&&
        pe->FileHeader.TimeDateStamp==0x47106421u;
}
// UNetDriver::TickFlush: ServerConnection (+0x44)->Tick(), then each of
// ClientConnections (+0x38, count +0x3c)->Tick() (connection vtable +0xf4).
static const BYTE TickFlushCode[]{
    0x56,0x57,0x8b,0xf9,0x83,0x7f,0x44,0x00,0x74,0x0b,0x8b,0x4f,0x44,0x8b,0x01,0xff,0x90,0xf4,0x00,0x00,0x00,
    0x33,0xf6,0x39,0x77,0x3c,0x7e,0x1a,0x8d,0x64,0x24,0x00,0x8b,0x4f,0x38,0x8b,0x0c,0xb1,0x8b,0x11,0xff,0x92,
    0xf4,0x00,0x00,0x00,0x83,0xc6,0x01,0x3b,0x77,0x3c,0x7c,0xea,0x5f,0x5e,0xc3};
// End of UNetConnection::Tick: if TimeSensitive (+0x11c) or Driver->Time (+0x50)
// - LastSendTime (+0xe8) > Driver->KeepAliveTime (+0x64), FlushNet() (vtable +0xf0).
static const BYTE ConnectionFlush[]{
    0x39,0x9e,0x1c,0x01,0x00,0x00,0x75,0x17,0x8b,0x46,0x4c,0xdd,0x40,0x50,0xdc,0xa6,0xe8,0x00,0x00,0x00,
    0xd9,0x40,0x64,0xd9,0xc9,0xdf,0xf1,0xdd,0xd8,0x76,0x0a,0x8b,0x06,0x8b,0xce,0xff,0x90,0xf0,0x00,0x00,0x00};
// GetMaxTickRate's dedicated-server branch: Clamp(NetDriver->NetServerMaxTickRate (+0x7c), 10, 120).
static const BYTE DedicatedRate[]{0x8b,0x40,0x7c,0x83,0xf8,0x0a,0x7d};
static const BYTE DedicatedCap[]{0x83,0xf8,0x78};
static bool Install(BYTE* base){
    if(Installed)return Verified;
    Installed=true;
    if(!IdentityMatches(base)){InterlockedExchange(&ErrorCode,1);return false;}
    Image=base;
    BYTE* slot=base+TcpDriverSlotRva;
    if(Pointer(slot)!=base+TickFlushRva||Pointer(base+DriverSlotRva)!=base+TickFlushRva||
       !Matches(base+TickFlushRva,TickFlushCode,sizeof(TickFlushCode))||
       Pointer(base+ConnectionTickSlotRva)!=base+ConnectionTickRva||
       !Contains(base+ConnectionTickRva,0x800,ConnectionFlush,sizeof(ConnectionFlush))||
       Pointer(base+MaxTickRateSlotRva)!=base+MaxTickRateRva||
       !Contains(base+MaxTickRateRva,0x100,DedicatedRate,sizeof(DedicatedRate))||
       !Contains(base+MaxTickRateRva,0x100,DedicatedCap,sizeof(DedicatedCap))){
        InterlockedExchange(&ErrorCode,2);return false;
    }
    // The network driver reaches TickFlush only through its vtable.
    OriginalTickFlush=reinterpret_cast<TickFlushFn>(base+TickFlushRva);
    if(!WriteSlot(slot,reinterpret_cast<void*>(TickFlush))||Pointer(slot)!=reinterpret_cast<BYTE*>(TickFlush)){
        WriteSlot(slot,base+TickFlushRva);
        InterlockedExchange(&ErrorCode,5);return false;
    }
    Verified=true;InterlockedExchange(&ErrorCode,0);
    return true;
}
} // namespace NativeHostNetworkRate
