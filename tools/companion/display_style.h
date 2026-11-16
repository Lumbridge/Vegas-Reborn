// Original PC 1.06.2.15 FWindowsViewport::Resize windowed-style instruction.
// Researched by Lumbridge/R6V_Camera_Tool (camera-test.py, a6131583).
// Call only after validating the client image and unpacked instruction.
#pragma once
#include <windows.h>
#include <cstring>

namespace VegasDisplay {
constexpr unsigned StyleRva = 0x57941a;
constexpr DWORD Framed = 0x10cf0000, Borderless = 0x90000000;
inline bool Matches(const BYTE* instruction) {
    const BYTE opcode[]{0xc7,0x44,0x24,0x10};
    DWORD value=0;memcpy(&value,instruction+4,sizeof(value));
    return !memcmp(instruction,opcode,sizeof(opcode))&&(value==Framed||value==Borderless);
}
inline bool Apply(BYTE* instruction) {
    if(!Matches(instruction))return false;
    DWORD value=0;memcpy(&value,instruction+4,sizeof(value));
    if(value==Borderless)return true;
    DWORD protection=0;
    if(!VirtualProtect(instruction,8,PAGE_EXECUTE_READWRITE,&protection))return false;
    value=Borderless;memcpy(instruction+4,&value,sizeof(value));
    bool flushed=FlushInstructionCache(GetCurrentProcess(),instruction,8)!=FALSE;
    DWORD ignored=0;
    bool restored=VirtualProtect(instruction,8,protection,&ignored)!=FALSE;
    return flushed&&restored&&Matches(instruction);
}
}
