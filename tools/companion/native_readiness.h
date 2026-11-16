#pragma once
#include <windows.h>

namespace NativeReadiness {
inline bool Executable(const void* address,DWORD* protection=nullptr){
    MEMORY_BASIC_INFORMATION memory{};
    if(!VirtualQuery(address,&memory,sizeof(memory)))return false;
    if(protection)*protection=memory.Protect;
    return memory.State==MEM_COMMIT&&!(memory.Protect&(PAGE_GUARD|PAGE_NOACCESS))&&
        (memory.Protect&(PAGE_EXECUTE|PAGE_EXECUTE_READ|PAGE_EXECUTE_READWRITE|PAGE_EXECUTE_WRITECOPY));
}
}
