#include "../../tools/companion/launch_options.h"
#include <cstdio>

struct Probe{int x=0,y=0,fullscreen=-1;};
static void __fastcall FakeResize(void* value,void*,int x,int y,int fullscreen){
    auto probe=static_cast<Probe*>(value);probe->x=x;probe->y=y;probe->fullscreen=fullscreen;
}
static unsigned OriginalChecks=0;
static int __fastcall FakeComplete(void*,void*){++OriginalChecks;return 0;}
int main(int,char**){
    bool ok=LaunchOptions::IntroCaller(0x4c58bb)&&LaunchOptions::IntroCaller(0x4c59fe)&&LaunchOptions::IntroCaller(0x4c5cb9)&&!LaunchOptions::IntroCaller(0x4c5b96);
    LaunchOptions::OriginalComplete=reinterpret_cast<LaunchOptions::Complete>(FakeComplete);
    void* group=reinterpret_cast<void*>(1);
    LaunchOptions::SkipIntros=true;
    LaunchOptions::RememberGroup(group,0x4c58bb);
    ok=ok&&LaunchOptions::GroupComplete(group,nullptr)==1&&OriginalChecks==0;
    ok=ok&&LaunchOptions::GroupComplete(group,nullptr)==0&&OriginalChecks==1;
    LaunchOptions::RememberGroup(group,0x4c5b96);
    ok=ok&&LaunchOptions::GroupComplete(group,nullptr)==0&&OriginalChecks==2;
    LaunchOptions::SkipIntros=false;
    LaunchOptions::RememberGroup(group,0x4c59fe);
    ok=ok&&LaunchOptions::GroupComplete(group,nullptr)==0&&OriginalChecks==3;
    Probe probe;LaunchOptions::OriginalResize=reinterpret_cast<LaunchOptions::Resize>(FakeResize);
    LaunchOptions::Windowed=true;LaunchOptions::ResizeWindow(&probe,nullptr,1280,720,1);
    ok=ok&&probe.x==1280&&probe.y==720&&probe.fullscreen==0;
    LaunchOptions::Windowed=false;LaunchOptions::ResizeWindow(&probe,nullptr,1920,1080,1);
    ok=ok&&probe.x==1920&&probe.y==1080&&probe.fullscreen==1;
    D3DPRESENT_PARAMETERS parameters{};parameters.BackBufferWidth=2560;parameters.BackBufferHeight=1440;
    parameters.BackBufferFormat=D3DFMT_A8R8G8B8;parameters.MultiSampleType=D3DMULTISAMPLE_8_SAMPLES;
    parameters.MultiSampleQuality=2;parameters.SwapEffect=D3DSWAPEFFECT_DISCARD;
    parameters.FullScreen_RefreshRateInHz=144;parameters.PresentationInterval=D3DPRESENT_INTERVAL_ONE;
    auto expected=parameters;LaunchOptions::Present(&parameters);ok=ok&&!memcmp(&parameters,&expected,sizeof(parameters));
    LaunchOptions::Windowed=true;LaunchOptions::Present(&parameters);expected.Windowed=TRUE;expected.FullScreen_RefreshRateInHz=0;
    ok=ok&&!memcmp(&parameters,&expected,sizeof(parameters));
    puts(ok?"PASS: native fullscreen/D3D controls; startup group completion skips once; loading and disabled option unchanged":"FAIL: launch options");
    return ok?0:1;
}
