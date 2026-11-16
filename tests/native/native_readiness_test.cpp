#include "../../tools/companion/native_readiness.h"
#include "MinHook.h"
#include <cstring>
#include <cstdio>

static bool __stdcall Replacement(unsigned,unsigned){return false;}
int main(){
    BYTE* memory=static_cast<BYTE*>(VirtualAlloc(nullptr,4096,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
    if(!memory)return 1;
    // Exact five-byte original unlock facade, not a generic long prologue.
    const BYTE original[]{0xb0,0x01,0xc2,0x08,0x00};
    memcpy(memory,original,sizeof(original));
    void* trampoline=nullptr;
    bool ok=MH_Initialize()==MH_OK&&!NativeReadiness::Executable(memory);
    ok=ok&&MH_CreateHook(memory,reinterpret_cast<void*>(Replacement),&trampoline)==MH_ERROR_NOT_EXECUTABLE;
    DWORD previous=0;
    ok=ok&&VirtualProtect(memory,4096,PAGE_EXECUTE_READ,&previous)&&NativeReadiness::Executable(memory);
    using Function=bool(__stdcall*)(unsigned,unsigned);
    auto function=reinterpret_cast<Function>(memory);
    ok=ok&&function(0,0)&&MH_CreateHook(memory,reinterpret_cast<void*>(Replacement),&trampoline)==MH_OK;
    ok=ok&&MH_EnableHook(memory)==MH_OK&&!function(0,0);
    ok=ok&&MH_RemoveHook(memory)==MH_OK&&function(0,0)&&!memcmp(memory,original,sizeof(original));
    MH_Uninitialize();
    VirtualFree(memory,0,MEM_RELEASE);
    puts(ok?"PASS: readable plaintext waits for executable protection; exact retail stub hooks and restores":"FAIL: native readiness");
    return ok?0:1;
}
