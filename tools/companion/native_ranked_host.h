#pragma once

// Original PC matchmaking option mapper forces Player for dedicated hosting.
// Use its existing GameType option-3 serialization for that one property too.
// No session field, packet, saved setting, or selected GameType is fabricated.
#include <windows.h>
#include <cstdint>
#include <cstring>

namespace NativeRankedHost {
static_assert(sizeof(void*)==4,"Original retail images are x86");
static bool Installed=false,Verified=false;
static volatile LONG ErrorCode=0;
static constexpr uintptr_t Preferred=0x10900000;
static const char* LastError(){
    switch(InterlockedCompareExchange(&ErrorCode,0,0)){
        case 0:return "none";
        case 1:return "imageIdentityMismatch";
        case 2:return "nativeSignatureMismatch";
        case 3:return "nativeCodeWriteFailed";
        case 4:return "nativeCodeReadbackFailed";
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
static bool Install(BYTE* base,bool dedicated){
    if(Installed)return Verified;
    if(!Readable(base,0x1000)){InterlockedExchange(&ErrorCode,1);return false;}
    auto dos=reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if(dos->e_magic!=IMAGE_DOS_SIGNATURE||dos->e_lfanew<=0||dos->e_lfanew>0x800){InterlockedExchange(&ErrorCode,1);return false;}
    auto pe=reinterpret_cast<IMAGE_NT_HEADERS32*>(base+dos->e_lfanew);
    if(pe->Signature!=IMAGE_NT_SIGNATURE||pe->FileHeader.Machine!=IMAGE_FILE_MACHINE_I386||
       pe->OptionalHeader.ImageBase!=Preferred||
       pe->FileHeader.TimeDateStamp!=(dedicated?0x473ebf76u:0x47106421u)||
       pe->OptionalHeader.SizeOfImage!=(dedicated?0x1f53000u:0x1f56000u)){
        InterlockedExchange(&ErrorCode,1);return false;
    }
    const unsigned functionRva=dedicated?0x580230u:0x581460u;
    const unsigned propertyRva=dedicated?0x580302u:0x581532u;
    const unsigned blockRva=dedicated?0x580338u:0x581568u;
    BYTE entry[]{0x55,0x8b,0xec,0x83,0xe4,0xf8,0x6a,0xff,0x68,0,0,0,0,
                 0x64,0xa1,0,0,0,0,0x50,0x64,0x89,0x25,0,0,0,0};
    DWORD handler=DWORD(reinterpret_cast<uintptr_t>(base+(dedicated?0x14a8ea0u:0x14a9c00u)));
    memcpy(entry+9,&handler,4);
    // SUB EAX,0x800a; JZ property-specific branch (AGORA_gameType).
    const BYTE property[]{0x2d,0x0a,0x80,0,0,0x74,0x13};
    // CMP EAX,1; JNZ option3; PUSH EAX; JMP literal serializer;
    // option3: MOV ESI,3; JMP original generic option serializer.
    const BYTE original[]{0x83,0xf8,0x01,0x75,0x06,0x50,0xe9,0xe6,0,0,0,
                          0xbe,0x03,0,0,0,0xe9,0x16,0x01,0,0};
    if(!Readable(base+functionRva,sizeof(entry))||!Readable(base+propertyRva,sizeof(property))||
       !Readable(base+blockRva,sizeof(original))||memcmp(base+functionRva,entry,sizeof(entry))||
       memcmp(base+propertyRva,property,sizeof(property))||memcmp(base+blockRva,original,sizeof(original))){
        InterlockedExchange(&ErrorCode,2);return false;
    }
    BYTE* branch=base+blockRva+3;
    DWORD oldProtection=0;
    if(!VirtualProtect(branch,1,PAGE_EXECUTE_READWRITE,&oldProtection)){
        InterlockedExchange(&ErrorCode,3);return false;
    }
    // One-byte instruction change is atomic. Installed reference files remain
    // read-only; the companion modifies only the identified process image.
    *reinterpret_cast<volatile BYTE*>(branch)=0xeb;
    bool flushed=FlushInstructionCache(GetCurrentProcess(),branch,1)!=FALSE;
    DWORD ignored=0;
    bool protectedAgain=VirtualProtect(branch,1,oldProtection,&ignored)!=FALSE;
    BYTE expected[sizeof(original)];memcpy(expected,original,sizeof(expected));expected[3]=0xeb;
    Verified=flushed&&protectedAgain&&!memcmp(base+blockRva,expected,sizeof(expected))&&
        branch[0]==0xeb&&branch+2+static_cast<signed char>(branch[1])==base+blockRva+11;
    if(!Verified){
        DWORD rollbackProtection=0;
        if(VirtualProtect(branch,1,PAGE_EXECUTE_READWRITE,&rollbackProtection)){
            *reinterpret_cast<volatile BYTE*>(branch)=original[3];
            FlushInstructionCache(GetCurrentProcess(),branch,1);
            VirtualProtect(branch,1,oldProtection,&ignored);
        }
        InterlockedExchange(&ErrorCode,4);return false;
    }
    Installed=true;
    return true;
}
} // namespace NativeRankedHost
