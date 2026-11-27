// Rainbow Six Vegas 2 adapter. It isolates the companion's game copy and
// profile, routes the original Agora bootstrap and STUN hosts to the
// configured realm and, once Steam has unpacked the client, installs the
// grounded game hooks: fixed-rate head and face animation, Discord sign-in,
// the claimed player name, one-player multiplayer matches, full online XP and
// the startup intro sequence, the Windowed and Borderless display modes,
// controller identification and slot order, the stop of other players' stuck
// automatic fire sounds and the network send rate of a hosting player.
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <winternl.h>
#include <shlobj.h>
#include <winhttp.h>
#include <share.h>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <string>
#include "MinHook.h"

#include "companion_core.h"
#include "native_readiness.h"
#include "native_procedural_animation_vegas2.h"
#include "native_login_vegas2.h"
#include "native_claimed_name_vegas2.h"
#include "native_solo_match_vegas2.h"
#include "native_xp_modifier_vegas2.h"
#include "native_skip_intros_vegas2.h"
#include "native_display_vegas2.h"
#include "native_punkbuster_vegas2.h"
#include "native_gamepad.h"
#include "native_field_of_view.h"
#include "native_fire_sound_loop_vegas2.h"
#include "native_host_network_rate_vegas2.h"
#include "native_player_limit_vegas2.h"

static int FieldOfViewRequested=0;

static bool RedirectedHost(const char* name){
    // Static: RainbowSixVegas2_SADS.exe bootstrap host (file offset 0x1664f10)
    // and STUN hosts referenced at VA 0x10f7fcc9-0x10f7fd2c.
    if(!name)return false;
    return !_stricmp(name,"gconnect.ubi.com")||
      !_stricmp(name,"stun.apac.ubi.com")||
      !_stricmp(name,"stun.emea.ubi.com")||
      !_stricmp(name,"stun.ncsa.ubi.com");
}

// Spike diagnostics: host names, socket destinations, failed game-file opens
// and the game's debug strings go to <output>\spike-trace.log. No account
// data, credentials or packet contents are recorded.
static CRITICAL_SECTION TraceLock;
static FILE* TraceFile=nullptr;
static thread_local bool InTrace=false;
static void Trace(const char* format,...){
    if(InTrace||!TraceFile)return;
    InTrace=true;
    EnterCriticalSection(&TraceLock);
    SYSTEMTIME now;GetLocalTime(&now);
    fprintf(TraceFile,"%02u:%02u:%02u.%03u [%lu] ",now.wHour,now.wMinute,now.wSecond,now.wMilliseconds,GetCurrentThreadId());
    va_list args;va_start(args,format);vfprintf(TraceFile,format,args);va_end(args);
    fputc('\n',TraceFile);fflush(TraceFile);
    LeaveCriticalSection(&TraceLock);
    InTrace=false;
}
static std::string Endpoint(const sockaddr* address,int bytes){
    char text[64]="?";
    if(address&&bytes>=int(sizeof(sockaddr_in))&&address->sa_family==AF_INET){
        auto in=reinterpret_cast<const sockaddr_in*>(address);char ip[32]{};
        InetNtopA(AF_INET,&in->sin_addr,ip,sizeof(ip));sprintf_s(text,"%s:%u",ip,ntohs(in->sin_port));
    }
    return text;
}
static hostent* WSAAPI TraceResolveHost(const char* name){
    hostent* result=CompanionResolveHost(name);
    Trace("gethostbyname %s%s -> %s",name?name:"(null)",RedirectedHost(name)?" [redirected]":"",result?"ok":"failed");
    return result;
}
static int WSAAPI TraceResolveAddr(PCSTR name,PCSTR service,const ADDRINFOA* hints,PADDRINFOA* result){
    int status=CompanionResolveAddr(name,service,hints,result);
    Trace("getaddrinfo %s:%s%s -> %d",name?name:"(null)",service?service:"",RedirectedHost(name)?" [redirected]":"",status);
    return status;
}
static int WSAAPI TraceConnect(SOCKET socket,const sockaddr* address,int bytes){
    int status=CompanionConnect(socket,address,bytes);
    int error=status?WSAGetLastError():0;
    Trace("connect %s -> %d (wsa %d)",Endpoint(address,bytes).c_str(),status,error);
    if(status)WSASetLastError(error);
    return status;
}
static std::string Hex(const char* data,int length){
    std::string text;char byte[4];
    for(int i=0;data&&i<length&&i<32;i++){sprintf_s(byte,"%02x",unsigned(static_cast<unsigned char>(data[i])));text+=byte;}
    return text;
}
static std::string LocalEndpoint(SOCKET socket){
    sockaddr_in local{};int size=sizeof(local);
    return getsockname(socket,reinterpret_cast<sockaddr*>(&local),&size)?"?":Endpoint(reinterpret_cast<sockaddr*>(&local),size);
}
// The client sends retail v2 IP/NAT discovery to UDP 3074 and 3075 of the
// resolved STUN hosts. The Vegas 2 master shares a host with the Vegas 1
// master, so its discovery listens on its own TCP port and that port + 1.
// Map the destination there and map reply sources back so the client's
// reply-port checks see the original ports.
static bool RouteDiscovery(sockaddr_in& endpoint,bool outbound){
    if(endpoint.sin_family!=AF_INET||endpoint.sin_addr.s_addr!=RouteIp.s_addr||RouteTcpPort==3074)return false;
    const unsigned port=ntohs(endpoint.sin_port);
    const unsigned from=outbound?3074:RouteTcpPort,to=outbound?RouteTcpPort:3074;
    if(port!=from&&port!=from+1)return false;
    endpoint.sin_port=htons(u_short(to+(port-from)));
    return true;
}
using SendTo=decltype(&sendto);static SendTo OriginalSendTo;
static int WSAAPI TraceSendTo(SOCKET socket,const char* data,int length,int flags,const sockaddr* address,int bytes){
    static volatile LONG count=0;
    sockaddr_in mapped{};
    if(address&&bytes>=int(sizeof(sockaddr_in))&&address->sa_family==AF_INET){
        mapped=*reinterpret_cast<const sockaddr_in*>(address);
        if(RouteDiscovery(mapped,true)){address=reinterpret_cast<const sockaddr*>(&mapped);bytes=sizeof(mapped);}
    }
    int status=OriginalSendTo(socket,data,length,flags,address,bytes);
    int error=status<0?WSAGetLastError():0;
    if(InterlockedIncrement(&count)<=400)Trace("sendto %s -> %s %d bytes %s = %d (wsa %d)",LocalEndpoint(socket).c_str(),Endpoint(address,bytes).c_str(),length,Hex(data,length).c_str(),status,error);
    if(status<0)WSASetLastError(error);
    return status;
}
using RecvFrom=decltype(&recvfrom);static RecvFrom OriginalRecvFrom;
static int WSAAPI TraceRecvFrom(SOCKET socket,char* data,int length,int flags,sockaddr* address,int* bytes){
    static volatile LONG count=0;
    int status=OriginalRecvFrom(socket,data,length,flags,address,bytes);
    int error=status<0?WSAGetLastError():0;
    if(status>=0&&address&&bytes&&*bytes>=int(sizeof(sockaddr_in)))RouteDiscovery(*reinterpret_cast<sockaddr_in*>(address),false);
    if(error!=WSAEWOULDBLOCK&&InterlockedIncrement(&count)<=400)
        Trace("recvfrom %s <- %s = %d %s (wsa %d)",LocalEndpoint(socket).c_str(),Endpoint(address,bytes?*bytes:0).c_str(),status,status>0?Hex(data,status).c_str():"",error);
    if(status<0)WSASetLastError(error);
    return status;
}
static HANDLE WINAPI TraceFileW(LPCWSTR name,DWORD access,DWORD sharing,LPSECURITY_ATTRIBUTES security,DWORD disposition,DWORD attributes,HANDLE templateFile){
    HANDLE file=PrivateFile(name,access,sharing,security,disposition,attributes,templateFile);
    if(file==INVALID_HANDLE_VALUE){DWORD error=GetLastError();Trace("CreateFileW failed %lu: %ls",error,name?name:L"(null)");SetLastError(error);}
    return file;
}
static HANDLE WINAPI TraceFileA(LPCSTR name,DWORD access,DWORD sharing,LPSECURITY_ATTRIBUTES security,DWORD disposition,DWORD attributes,HANDLE templateFile){
    HANDLE file=PrivateFileA(name,access,sharing,security,disposition,attributes,templateFile);
    if(file==INVALID_HANDLE_VALUE){DWORD error=GetLastError();Trace("CreateFileA failed %lu: %s",error,name?name:"(null)");SetLastError(error);}
    return file;
}
using DebugA=decltype(&OutputDebugStringA);static DebugA OriginalDebugA;
using DebugW=decltype(&OutputDebugStringW);static DebugW OriginalDebugW;
static void WINAPI TraceDebugA(LPCSTR text){Trace("debug %s",text?text:"");OriginalDebugA(text);}
static void WINAPI TraceDebugW(LPCWSTR text){Trace("debugw %ls",text?text:L"");OriginalDebugW(text);}

// Vegas 2 finds Documents through SHGetSpecialFolderPathW, not SHGetFolderPathW.
using SpecialFolderFn=decltype(&SHGetSpecialFolderPathW);static SpecialFolderFn OriginalSpecialFolder;
static BOOL WINAPI PrivateSpecialFolder(HWND window,LPWSTR output,int folder,BOOL create){
    const int kind=folder&0xff;
    if((kind==CSIDL_PERSONAL||kind==CSIDL_APPDATA||kind==CSIDL_LOCAL_APPDATA)&&!ProfileDirectory.empty()){
        BOOL ok=SUCCEEDED(PrivateFolder(window,folder,nullptr,0,output));
        Trace("SHGetSpecialFolderPathW %d -> %ls",folder,ok?output:L"(failed)");
        return ok;
    }
    return OriginalSpecialFolder(window,output,folder,create);
}

static void WriteStatus(bool ready,unsigned gameHooks){
    FILE* status=nullptr;_wfopen_s(&status,(Output+L"\\compat-status.tmp").c_str(),L"wb");
    if(!status)return;
    fprintf(status,"{\"ready\":%s,\"title\":\"vegas2\",\"adapter\":\"vegas2\",\"nativeVersion\":1,\"gameHooks\":%u,"
        "\"proceduralAnimationVerified\":%s,\"proceduralAnimationStatus\":\"%s\","
        "\"discordSignInVerified\":%s,\"discordSignInStatus\":\"%s\","
        "\"claimedNameVerified\":%s,\"claimedNameStatus\":\"%s\","
        "\"soloMatchVerified\":%s,\"soloMatchStatus\":\"%s\","
        "\"fullXpVerified\":%s,\"fullXpStatus\":\"%s\","
        "\"skipIntrosRequested\":%s,\"skipIntrosVerified\":%s,\"skipIntrosStatus\":\"%s\","
        "\"punkBusterRemovedVerified\":%s,\"punkBusterRemovedStatus\":\"%s\","
        "\"gamepadVerified\":%s,\"gamepadStatus\":\"%s\","
        "\"fireSoundLoopVerified\":%s,\"fireSoundLoopStatus\":\"%s\","
        "\"hostNetworkRateVerified\":%s,\"hostNetworkRateStatus\":\"%s\","
        "\"playerLimitVerified\":%s,\"playerLimitStatus\":\"%s\","
        "\"fieldOfViewRequested\":%d,\"fieldOfViewVerified\":%s,\"fieldOfViewStatus\":\"%s\","
        "\"windowedRequested\":%s,\"borderlessRequested\":%s,\"displayViewportVerified\":%s,\"displayStyleVerified\":%s,\"displayStatus\":\"%s\"}\n",
        ready?"true":"false",gameHooks,
        NativeProceduralAnimationVegas2::Verified?"true":"false",NativeProceduralAnimationVegas2::Error,
        NativeLoginVegas2::Verified?"true":"false",NativeLoginVegas2::Error,
        NativeClaimedNameVegas2::Verified?"true":"false",NativeClaimedNameVegas2::Error,
        NativeSoloMatchVegas2::Verified?"true":"false",NativeSoloMatchVegas2::Error,
        NativeXpModifierVegas2::Verified?"true":"false",NativeXpModifierVegas2::Error,
        NativeSkipIntrosVegas2::Requested?"true":"false",NativeSkipIntrosVegas2::Verified?"true":"false",NativeSkipIntrosVegas2::Error,
        NativePunkBusterVegas2::Verified?"true":"false",NativePunkBusterVegas2::Error,
        NativeGamepad::Verified?"true":"false",NativeGamepad::Error,
        NativeFireSoundLoopVegas2::Verified?"true":"false",NativeFireSoundLoopVegas2::Error,
        NativeHostNetworkRateVegas2::Verified?"true":"false",NativeHostNetworkRateVegas2::Error,
        NativePlayerLimitVegas2::Verified?"true":"false",NativePlayerLimitVegas2::Error,
        FieldOfViewRequested,NativeFieldOfView::Verified?"true":"false",NativeFieldOfView::Error,
        NativeDisplayVegas2::Windowed?"true":"false",NativeDisplayVegas2::Borderless?"true":"false",
        NativeDisplayVegas2::ViewportArmed?"true":"false",NativeDisplayVegas2::StyleVerified?"true":"false",NativeDisplayVegas2::Error);
    fclose(status);
    MoveFileExW((Output+L"\\compat-status.tmp").c_str(),(Output+L"\\compat-status.json").c_str(),MOVEFILE_REPLACE_EXISTING);
}

// Steam's loader unpacks .text after the suspended startup gate. Install only
// against the recovered plaintext, before the user reaches the online menus.
// Readiness is reported once, after this attempt, as for Vegas 1.
static DWORD WINAPI NativeInitialize(LPVOID){
    auto base=reinterpret_cast<unsigned char*>(GetModuleHandleW(nullptr));
    bool codeReady=false;
    for(unsigned attempt=0;attempt<600&&!codeReady;++attempt){
        codeReady=NativeProceduralAnimationVegas2::CodeReady(base)&&NativeLoginVegas2::CodeReady(base)&&
            NativeClaimedNameVegas2::CodeReady(base)&&NativeSoloMatchVegas2::CodeReady(base)&&
            NativeXpModifierVegas2::CodeReady(base)&&NativePunkBusterVegas2::CodeReady(base)&&
            NativeReadiness::Executable(base+(NativeLoginVegas2::LoginAddress-NativeLoginVegas2::Preferred))&&
            NativeReadiness::Executable(base+(NativeProceduralAnimationVegas2::InitAddress-NativeProceduralAnimationVegas2::Preferred));
        if(!codeReady)Sleep(100);
    }
    if(!codeReady){Trace("game code not ready; game hooks not installed");WriteStatus(false,0);return 0;}
    const bool animation=NativeProceduralAnimationVegas2::Install(base);
    const bool login=NativeLoginVegas2::Install(base,NativeAlias,Credentials);
    const bool name=NativeClaimedNameVegas2::Install(base,Output);
    const bool solo=NativeSoloMatchVegas2::Install(base);
    const bool xp=NativeXpModifierVegas2::Install(base);
    const bool intros=NativeSkipIntrosVegas2::Install(base,Output);
    const bool display=NativeDisplayVegas2::Install(base);
    const bool punkBuster=NativePunkBusterVegas2::Install(base);
    const bool fireSound=NativeFireSoundLoopVegas2::Install(base);
    const bool playerLimit=NativePlayerLimitVegas2::Install(base);
    // A host network rate mismatch keeps the original rate and does not stop the launch.
    NativeHostNetworkRateVegas2::Install(base);
    // A requested field of view is reported on its own; the launcher checks it.
    NativeFieldOfView::Install(base,NativeFieldOfView::Vegas2::Game,FieldOfViewRequested);
    Trace("game hooks: animation %s, sign-in %s, name %s, solo %s, xp %s, intros %s, display %s, punkbuster %s, fire sound %s, player limit %s, host rate %s, field of view %s",NativeProceduralAnimationVegas2::Error,
        NativeLoginVegas2::Error,NativeClaimedNameVegas2::Error,NativeSoloMatchVegas2::Error,NativeXpModifierVegas2::Error,
        NativeSkipIntrosVegas2::Error,NativeDisplayVegas2::Error,NativePunkBusterVegas2::Error,NativeFireSoundLoopVegas2::Error,
        NativePlayerLimitVegas2::Error,NativeHostNetworkRateVegas2::Error,NativeFieldOfView::Error);
    const bool ready=animation&&login&&name&&solo&&xp&&intros&&display&&punkBuster&&fireSound&&playerLimit;
    WriteStatus(ready,(animation?NativeProceduralAnimationVegas2::MethodCount+1:0)+(login?2:0)+
        (NativeClaimedNameVegas2::Verified?NativeClaimedNameVegas2::Count:0)+(solo?NativeSoloMatchVegas2::Count:0)+(xp?2:0)+
        (NativeSkipIntrosVegas2::Verified?2:0)+(NativeDisplayVegas2::ViewportArmed?1:0)+(NativeDisplayVegas2::StyleVerified?1:0)+
        (punkBuster?NativePunkBusterVegas2::HookCount:0)+(fireSound?2:0)+(playerLimit?2:0)+(NativeHostNetworkRateVegas2::Verified?1:0));
    return ready?1:0;
}

extern "C" DWORD WINAPI StartCompanion(LPVOID configuration){
    std::wstring all=static_cast<const wchar_t*>(configuration);size_t a=all.find(L'\n'),b=all.find(L'\n',a+1);
    if(a==std::wstring::npos||b==std::wstring::npos)return 0;
    Output=all.substr(0,a);WorkingRoot=all.substr(a+1,b-a-1);SourceRoot=all.substr(b+1);
    RegisteredExecutable=SourceRoot+L"\\Binaries\\R6Vegas2_Game.exe";ProfileDirectory=Output+L"\\profile";
    FILE* settings=nullptr;_wfopen_s(&settings,(Output+L"\\native-route.txt").c_str(),L"rb");
    if(!settings)return 0;
    char address[64]{},capability[65]{},alias[16]{};unsigned port=0,ipc=0;
    bool valid=fscanf_s(settings,"%63s %u %u %64s %15s",address,unsigned(sizeof(address)),&port,&ipc,capability,unsigned(sizeof(capability)),alias,unsigned(sizeof(alias)))==5;
    fclose(settings);
    if(!valid||!port||port>65535||!ipc||ipc>65535||strlen(capability)!=64||strlen(alias)!=15||InetPtonA(AF_INET,address,&RouteIp)!=1)return 0;
    RouteAddress=address;RouteTcpPort=port;IpcPort=ipc;NativeAlias=alias;IpcCapability.assign(capability,capability+64);
    FieldOfViewRequested=NativeFieldOfView::Read(Output);
    if(FieldOfViewRequested<0)return 0;
    CreateDirectoryW(ProfileDirectory.c_str(),nullptr);CreateDirectoryW((Output+L"\\agora-session").c_str(),nullptr);
    InitializeCriticalSection(&TraceLock);
    // Shared read access so the trace can be inspected while the game runs.
    TraceFile=_wfsopen((Output+L"\\spike-trace.log").c_str(),L"w",_SH_DENYNO);
    if(MH_Initialize()!=MH_OK)return 0;
    // Vegas 2 imports WSOCK32, which forwards these exports to ws2_32.
    LoadLibraryW(L"shell32.dll");LoadLibraryW(L"ws2_32.dll");LoadLibraryW(L"wsock32.dll");bool ready=true;
#define HOOK(module,name,fn,original) ready&=HookAPI(L##module,name,reinterpret_cast<void*>(fn),reinterpret_cast<void**>(&original))
    HOOK("kernel32.dll","GetModuleFileNameW",RegisteredModuleNameW,OriginalModuleNameW);
    HOOK("kernel32.dll","GetModuleFileNameA",RegisteredModuleNameA,OriginalModuleNameA);
    HOOK("shell32.dll","SHGetFolderPathW",PrivateFolder,OriginalFolder);
    HOOK("shell32.dll","SHGetSpecialFolderPathW",PrivateSpecialFolder,OriginalSpecialFolder);
    HOOK("kernel32.dll","CreateFileW",TraceFileW,OriginalFileOpen);
    HOOK("kernel32.dll","CreateFileA",TraceFileA,OriginalFileOpenA);
    HOOK("kernel32.dll","OutputDebugStringA",TraceDebugA,OriginalDebugA);
    HOOK("kernel32.dll","OutputDebugStringW",TraceDebugW,OriginalDebugW);
    HOOK("ws2_32.dll","sendto",TraceSendTo,OriginalSendTo);
    // WSOCK32 implements recvfrom itself (over WSARecvFrom) instead of forwarding it.
    HOOK("wsock32.dll","recvfrom",TraceRecvFrom,OriginalRecvFrom);
    HOOK("kernel32.dll","SetCurrentDirectoryW",PrivateSetCwdW,OriginalSetCwdW);
    HOOK("kernel32.dll","SetCurrentDirectoryA",PrivateSetCwdA,OriginalSetCwdA);
    HOOK("kernel32.dll","CreateDirectoryW",PrivateDirectory,OriginalDirectoryCreate);
    HOOK("ntdll.dll","NtCreateFile",PrivateNativeFile,OriginalNativeFileOpen);
    HOOK("ws2_32.dll","gethostbyname",TraceResolveHost,OriginalResolveHost);
    HOOK("ws2_32.dll","getaddrinfo",TraceResolveAddr,OriginalResolveAddr);
    HOOK("ws2_32.dll","connect",TraceConnect,OriginalConnectSocket);
#undef HOOK
    // A failed display setup is reported with the game hooks, as for the others.
    NativeDisplayVegas2::Arm(Output);
    // A controller setup failure keeps the original input and does not stop the launch.
    NativeGamepad::Arm();
    if(!ready||MH_EnableHook(MH_ALL_HOOKS)!=MH_OK){MH_Uninitialize();WriteStatus(false,0);return 0;}
    Trace("adapter ready; route %s:%u",RouteAddress.c_str(),RouteTcpPort);
    HANDLE thread=CreateThread(nullptr,0,NativeInitialize,nullptr,0,nullptr);
    if(!thread){WriteStatus(false,0);return 0;}
    CloseHandle(thread);
    return 1;
}
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH)DisableThreadLibraryCalls(instance);return TRUE;}
