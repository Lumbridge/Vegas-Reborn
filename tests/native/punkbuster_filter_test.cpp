// The Vegas 1 PunkBuster filter removal shares the filters init with the
// ranked access module, which appends the game type filter after it.
#include <windows.h>
#include <cstdio>
#include "../../tools/companion/native_punkbuster.h"

static int Failures=0;
static void Expect(bool condition,const char* message){
    if(!condition){std::printf("FAIL: %s\n",message);++Failures;}
}

int main(){
    BYTE* image=static_cast<BYTE*>(VirtualAlloc(nullptr,0x1800000,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
    if(!image)return 2;
    NativePunkBuster::Image=image;
    void* punkBusterTable=image+NativePunkBuster::FilterVtableRva;
    void* otherTable=image+0x1793b00;
    void* punkBuster[2]{punkBusterTable,nullptr};
    void* first[2]{otherTable,nullptr};
    void* gameType[2]{otherTable,nullptr};
    void* shown[4]{first,punkBuster,gameType,nullptr};
    BYTE manager[0x28]{};
    *reinterpret_cast<void***>(manager+NativePunkBuster::FilterShownList)=shown;
    int& count=*reinterpret_cast<int*>(manager+NativePunkBuster::FilterShownCount);

    // Quick Match (mode 0) keeps its filters.
    count=3;
    NativePunkBuster::RemoveShownFilter(manager,0);
    Expect(count==3,"mode 0 is unchanged");

    // Custom Match: ranked access appended the game type after PunkBuster.
    NativePunkBuster::RemoveShownFilter(manager,int(NativePunkBuster::FilterShownMode));
    Expect(count==2&&shown[0]==first&&shown[1]==gameType,"PunkBuster removed before the appended game type");

    // Without ranked access, PunkBuster is last.
    shown[0]=first;shown[1]=punkBuster;count=2;
    NativePunkBuster::RemoveShownFilter(manager,int(NativePunkBuster::FilterShownMode));
    Expect(count==1&&shown[0]==first,"trailing PunkBuster removed");

    // No PunkBuster entry: nothing removed.
    NativePunkBuster::RemoveShownFilter(manager,int(NativePunkBuster::FilterShownMode));
    Expect(count==1&&shown[0]==first,"list without PunkBuster is unchanged");

    VirtualFree(image,0,MEM_RELEASE);
    if(Failures)return 1;
    std::printf("punkbuster_filter_test passed\n");
    return 0;
}
