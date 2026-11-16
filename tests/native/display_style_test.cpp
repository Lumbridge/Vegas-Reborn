#include "../../tools/companion/display_style.h"
#include <cstdio>

int main(){
    auto memory=static_cast<BYTE*>(VirtualAlloc(nullptr,4096,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
    if(!memory)return 1;
    BYTE* instruction=memory+2; // Same unaligned immediate as the retail RVA.
    const BYTE original[]{0xc7,0x44,0x24,0x10,0,0,0xcf,0x10};
    memcpy(instruction,original,8);
    bool ok=VegasDisplay::Matches(instruction)&&VegasDisplay::Apply(instruction);
    DWORD value=0;memcpy(&value,instruction+4,4);
    ok=ok&&value==VegasDisplay::Borderless&&VegasDisplay::Apply(instruction);
    memcpy(instruction,original,8);instruction[0]=0x90;
    BYTE before[8];memcpy(before,instruction,8);
    ok=ok&&!VegasDisplay::Apply(instruction)&&!memcmp(before,instruction,8);
    memcpy(instruction,original,8);instruction[7]=0x20;memcpy(before,instruction,8);
    ok=ok&&!VegasDisplay::Apply(instruction)&&!memcmp(before,instruction,8);
    VirtualFree(memory,0,MEM_RELEASE);
    puts(ok?"PASS: original instruction patched; repeat safe; mismatched opcode/style rejected unchanged":"FAIL");
    return ok?0:2;
}
