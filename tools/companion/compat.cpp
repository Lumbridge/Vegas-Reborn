// Retail companion compatibility and requested display/intro controls only:
// no input hooks, captures, dumps, frame limits or gameplay replacements.
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <winternl.h>
#include <shlobj.h>
#include <winhttp.h>
#include <cstdio>
#include <cstring>
#include <string>
#include "MinHook.h"
#include "display_style.h"
#include "claimed_name.h"
#include "native_claimed_session_name.h"
#include "native_readiness.h"
#include "launch_options.h"
#include "native_rank.h"
#include "native_report_xp.h"
#include "native_ranked_access.h"
#include "native_host_statistics.h"
#include "native_ranked_host.h"
#include "native_solo_hunt.h"
#include "native_procedural_animation.h"
#include "native_unlock_policy.h"
#include "native_pec_defaults.h"
#include "native_player_limit.h"
#include "native_fire_sound_loop.h"

#include "companion_core.h"

static bool BorderlessRequested=false;
static bool WindowedRequested=false,SkipIntrosRequested=false;
#ifdef R6V_DEDICATED
static constexpr bool Dedicated=true;
#else
static constexpr bool Dedicated=false;
#endif

// Optional <output>\native-features.txt lists player-disabled toggle ids, one
// per line. Absent file: every feature is installed. Unknown and companion-side
// (ini) ids are ignored. Only the toggleable features below are affected.
static bool ProceduralAnimationDisabled=false,SoloHuntDisabled=false,ReportXpDisabled=false,
    RankDisabled=false,RankedAccessDisabled=false,PlayerLimitDisabled=false,FireSoundLoopDisabled=false;
static void ReadNativeFeatures(){
    FILE* features=nullptr;
    if(_wfopen_s(&features,(Output+L"\\native-features.txt").c_str(),L"rb")||!features)return;
    char id[64]{};
    while(fscanf_s(features,"%63s",id,unsigned(sizeof(id)))==1){
        if(!strcmp(id,"procedural-animation"))ProceduralAnimationDisabled=true;
        else if(!strcmp(id,"solo-terrorist-hunt"))SoloHuntDisabled=true;
        else if(!strcmp(id,"report-xp"))ReportXpDisabled=true;
        else if(!strcmp(id,"player-rank"))RankDisabled=true;
        else if(!strcmp(id,"ranked-access"))RankedAccessDisabled=true;
        else if(!strcmp(id,"sixteen-players"))PlayerLimitDisabled=true;
        else if(!strcmp(id,"fire-sound-loop"))FireSoundLoopDisabled=true;
    }
    fclose(features);
}
static std::string DisabledFeatures(){
    std::string list;
    const struct {bool disabled;const char* id;} features[]{
        {ProceduralAnimationDisabled,"procedural-animation"},{SoloHuntDisabled,"solo-terrorist-hunt"},
        {ReportXpDisabled,"report-xp"},{RankDisabled,"player-rank"},{RankedAccessDisabled,"ranked-access"},
        {PlayerLimitDisabled,"sixteen-players"},{FireSoundLoopDisabled,"fire-sound-loop"}};
    for(const auto& feature:features){
        if(!feature.disabled)continue;
        if(!list.empty())list+=',';
        list+='"';list+=feature.id;list+='"';
    }
    return list;
}

static bool RedirectedHost(const char* name){
    if(!name)return false;
    return !_stricmp(name,"gconnect.ubi.com")||
      !_stricmp(name,"rainbow6vegas-pc.stun.eu.ubi.com")||
      !_stricmp(name,"rainbow6vegas-pc.stun.us.ubi.com")||
      !_stricmp(name,"rainbow6vegas-pc.stun.jp.ubi.com")||
      !_stricmp(name,"rainbow6vegas-pc.stun.au.ubi.com");
}

using TaskStart=void(__thiscall*)(void*);static TaskStart OriginalTaskStart;
struct EngineString {const wchar_t* data;int length;int capacity;};
using StringAssign=void*(__thiscall*)(void*,const EngineString*);
using LoadLogin=void(__thiscall*)(void*,void*,void*);static LoadLogin OriginalLoadLogin;
static void __fastcall DiscordLoginFields(void* form,void*,void* username,void* password){
    OriginalLoadLogin(form,username,password);
    auto assign=reinterpret_cast<StringAssign>(reinterpret_cast<BYTE*>(GetModuleHandleW(nullptr))+0x20540);
    std::wstring alias(NativeAlias.begin(),NativeAlias.end());
    EngineString name{alias.c_str(),int(alias.size()+1),int(alias.size()+1)};
    EngineString placeholder{L"UseDiscord",11,11};
    assign(username,&name);assign(password,&placeholder);
}
static void __fastcall DiscordTaskStart(void* task,void*){
    NativeHostStatistics::ObserveLogin(task);
    // Grounded in TaskLogin1185eba0 and its completion1185efb0. Both
    // authentication and profile-ID derivation consume this SAME UserInfo.
    auto user=*reinterpret_cast<BYTE**>(static_cast<BYTE*>(task)+0x30);
    std::string username,password;
    if(!Credentials(username,password)){username=NativeAlias;password="Expired";}
    if(user){
        std::wstring alias(username.begin(),username.end()),secret(password.begin(),password.end());
        wcscpy_s(reinterpret_cast<wchar_t*>(user+8),21,alias.c_str());
        wcscpy_s(reinterpret_cast<wchar_t*>(user+0xdc),16,secret.c_str());
        OriginalTaskStart(task);
        SecureZeroMemory(secret.data(),secret.size()*sizeof(wchar_t));
        // The native starter already copied the proof input into its auth
        // operation. Do not retain the one-use secret in remembered UI settings.
        wcscpy_s(reinterpret_cast<wchar_t*>(user+0xdc),16,L"Discord");
    }else OriginalTaskStart(task);
    if(!password.empty())SecureZeroMemory(password.data(),password.size());
}


static void* OriginalUnlockFacade;
static bool __fastcall NativeUnlockEligibility(void* controller,void*,unsigned profileHandle,unsigned itemId){
    // The facade receives an item ID. Preserve the original item -> group ->
    // bundle availability path and named rank ordinal, including starter gear.
    return NativeUnlockPolicy::Available(controller,profileHandle,itemId);
}
static bool LocalPhysics(){
    // The pinned original ships the correct PhysX 2.5.1 core, but its loader
    // otherwise requires an obsolete AGEIA registry installation. Enable its
    // existing adjacent-Core attempt; retain the original version/SDK checks
    // and registry fallback. Only this private process's mapping is changed.
    HMODULE loader=LoadLibraryW((WorkingRoot+L"\\Binaries\\PhysXLoader.dll").c_str());
    if(!loader)return false;
    BYTE* branch=reinterpret_cast<BYTE*>(loader)+0x11e5;
    if(branch[0]==0x90&&branch[1]==0x90)return true;
    if(branch[0]!=0x74||branch[1]!=0x0f)return false;
    DWORD protection=0;
    if(!VirtualProtect(branch,2,PAGE_EXECUTE_READWRITE,&protection))return false;
    branch[0]=0x90;branch[1]=0x90;FlushInstructionCache(GetCurrentProcess(),branch,2);
    DWORD ignored;VirtualProtect(branch,2,protection,&ignored);return true;
}
static DWORD WINAPI NativeInitialize(LPVOID){
    auto base=reinterpret_cast<BYTE*>(GetModuleHandleW(nullptr));
    BYTE prefill[]{0x6a,0xff,0x68,0xec,0x6e,0xd7,0x11,0x64,0xa1,0,0,0,0,0x50,0x64,0x89,0x25,0,0,0,0,0x81,0xec,0x8c};
    DWORD exceptionHandler=DWORD(reinterpret_cast<ULONG_PTR>(base+0x1476eec));memcpy(prefill+3,&exceptionHandler,4);
    const BYTE rankStub[]{0xb8,0x80,0x84,0x1e,0,0xc3};
    const BYTE rawGetter[]{0x8b,0x41,0x10,0xc3};
    const BYTE loginPrefix[]{0x55,0x8b,0xec,0x83,0xe4,0xf8,0x6a,0xff};
    const BYTE assignmentPrefix[]{0x55,0x8b,0x6c,0x24,0x08,0x56,0x8b,0xf1};
    const BYTE unlockStub[]{0xb0,0x01,0xc2,0x08,0x00};
    const BYTE profileLookup[]{0x8b,0x44,0x24,0x04,0x50,0xe8,0xd6,0xfb,0xff,0xff,0x8b,0x51,0x14,0x83,0xc4,0x04};
    const BYTE infoLookup[]{0x8b,0x41,0x04,0xc3};
    const BYTE groupLookup[]{0x8b,0x51,0x0c,0x53,0x56,0x33,0xc0,0x85,0xd2,0x57,0x76,0x18,0x8b,0x71,0x04,0x8b};
    const BYTE groupRequirements[]{0x53,0x55,0x56,0x57,0x8b,0xf9,0x8b,0x47,0x24,0x33,0xf6,0x85,0xc0,0x76,0x21,0x8b};
    const BYTE rankRequirement[]{0x8b,0x44,0x24,0x04,0x56,0x8b,0xf1,0x8b,0x4c,0x24,0x0c,0x50,0xe8,0x8f,0x42,0xff,0xff};
    const BYTE profileIndex[]{0x33,0xc0,0xc3};
    const BYTE rankCalculation[]{0x56,0x8b,0xf1,0x8b,0x4c,0x24,0x08,0xe8,0x84,0xd1,0xff,0xff,0x50,0x8d,0x4e,0x20};
    auto dos=reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    auto pe=reinterpret_cast<const IMAGE_NT_HEADERS32*>(base+dos->e_lfanew);
    const bool clientIdentity=dos->e_magic==IMAGE_DOS_SIGNATURE&&pe->Signature==IMAGE_NT_SIGNATURE&&
        pe->FileHeader.Machine==IMAGE_FILE_MACHINE_I386&&pe->FileHeader.TimeDateStamp==0x47106421&&
        pe->OptionalHeader.SizeOfImage==0x1f56000&&pe->OptionalHeader.ImageBase==0x10900000;
    bool ready=false,unlockVerified=false,displayStyleVerified=false,executablePagesReady=false;
    DWORD unlockTargetProtection=0,unlockDetourProtection=0;
    unsigned signatures=0;int taskStatus=-1,prefillStatus=-1,unlockStatus=-1;
    const unsigned stubRva=Dedicated?0x565db0:0x566fe0;
    const unsigned getterRva=Dedicated?0x5650f0:0x566320;
    const unsigned loginRva=Dedicated?0xf5dd30:0xf5eba0;
    // Steam's loader unpacks .text after the suspended startup gate. Install
    // only against recovered plaintext, before the user opens the login form.
    for(unsigned attempt=0;attempt<600;++attempt){
        signatures=(Dedicated||!memcmp(base+0xb7230,prefill,sizeof(prefill))?1:0)|
           (!memcmp(base+stubRva,rankStub,sizeof(rankStub))?2:0)|
           (!memcmp(base+getterRva,rawGetter,sizeof(rawGetter))?4:0)|
           (!memcmp(base+loginRva,loginPrefix,sizeof(loginPrefix))?8:0)|
           (Dedicated||!memcmp(base+0x20540,assignmentPrefix,sizeof(assignmentPrefix))?16:0);
        if(!Dedicated){
            signatures|=(!memcmp(base+0x563150,unlockStub,sizeof(unlockStub))?32:0)|
                (!memcmp(base+0x5656c0,profileLookup,sizeof(profileLookup))?64:0)|
                (!memcmp(base+0x565380,infoLookup,sizeof(infoLookup))?128:0)|
                (!memcmp(base+0x56db30,groupLookup,sizeof(groupLookup))?256:0)|
                (!memcmp(base+0x5729d0,groupRequirements,sizeof(groupRequirements))?512:0)|
                (!memcmp(base+0x575bb0,rankRequirement,sizeof(rankRequirement))?1024:0)|
                (!memcmp(base+0x5652a0,profileIndex,sizeof(profileIndex))?2048:0)|
                (!memcmp(base+0x569e50,rankCalculation,sizeof(rankCalculation))?4096:0)|
                (clientIdentity?8192:0);
        }
        if(signatures==(Dedicated?31u:16383u)){
            // Steam can expose plaintext before restoring executable page
            // protection. Matching bytes alone is not hook readiness.
            executablePagesReady=NativeReadiness::Executable(base+loginRva)&&
                NativeReadiness::Executable(reinterpret_cast<const void*>(DiscordTaskStart));
            if(!Dedicated){
                const bool targetReady=NativeReadiness::Executable(base+0x563150,&unlockTargetProtection);
                const bool detourReady=NativeReadiness::Executable(reinterpret_cast<const void*>(NativeUnlockEligibility),&unlockDetourProtection);
                executablePagesReady=executablePagesReady&&targetReady&&detourReady&&
                    NativeReadiness::Executable(base+0xb7230)&&
                    NativeReadiness::Executable(reinterpret_cast<const void*>(DiscordLoginFields));
                if(WindowedRequested)executablePagesReady=executablePagesReady&&
                    NativeReadiness::Executable(base+LaunchOptions::ResizeRva)&&LaunchOptions::ResizeMatches(base);
            }
            if(SkipIntrosRequested)executablePagesReady=executablePagesReady&&NativeReadiness::Executable(base+LaunchOptions::AppendRva)&&NativeReadiness::Executable(base+LaunchOptions::CompleteRva)&&LaunchOptions::SequenceMatches(base);
            // New online hooks must obey the same plaintext-versus-executable
            // distinction as the existing startup hooks before installation.
            const unsigned onlineTargets[]{
                NativeHostStatistics::Rva(Dedicated?0x10d95b20:0x10924720),
                NativeHostStatistics::Rva(Dedicated?0x111a1540:0x111a23d0),
                NativeHostStatistics::Rva(Dedicated?0x10ce5000:0x10ce6270),
                Dedicated?0x8a6130u:0x8a6fc0u};
            for(unsigned rva:onlineTargets)executablePagesReady=executablePagesReady&&NativeReadiness::Executable(base+rva);
            if(!Dedicated)executablePagesReady=executablePagesReady&&
                NativeReadiness::Executable(base+NativeRank::PopulateRva)&&NativeReadiness::Executable(base+NativeRank::SelectionRva)&&
                NativeReadiness::Executable(base+NativeRankedAccess::CreateRva)&&NativeReadiness::Executable(base+NativeRankedAccess::FiltersRva)&&
                NativeReadiness::Executable(base+0x56a300)&&
                NativeReadiness::Executable(base+NativeReportXp::CalculateRva)&&
                NativeReadiness::Executable(base+NativeReportXp::CoopRva)&&NativeReadiness::Executable(base+NativeReportXp::VersusRva)&&
                NativeReadiness::Executable(base+NativePlayerLimit::MaxPlayersRva)&&NativeReadiness::Executable(base+NativePlayerLimit::ConstructorRva)&&
                NativeReadiness::Executable(base+NativeFireSoundLoop::HandlerRva)&&NativeReadiness::Executable(base+NativeFireSoundLoop::TickRva);
            if(!Dedicated){
                for(unsigned rva:NativeClaimedSessionName::Sites)
                    executablePagesReady=executablePagesReady&&NativeReadiness::Executable(base+rva);
                executablePagesReady=executablePagesReady&&NativeReadiness::Executable(base+0xfc32e0);
                const unsigned pecTargets[]{
                    0x10ce4680,0x11175f90,0x109f6ee0,0x109d4e40,0x10ccfb60,0x10cd0da0,
                    0x10e642d0,0x10e62d50,0x10ce44f0,0x10e62db0};
                for(unsigned address:pecTargets)
                    executablePagesReady=executablePagesReady&&NativeReadiness::Executable(base+NativeHostStatistics::Rva(address));
            }
            if(!executablePagesReady){Sleep(100);continue;}
            taskStatus=prefillStatus=unlockStatus=-1;
            if(BorderlessRequested){
                // Vegas reasserts its own style on every viewport resize.
                // Keep its native windowed path, but use WS_VISIBLE|WS_POPUP.
                // No executable file is changed.
                displayStyleVerified=clientIdentity&&VegasDisplay::Apply(base+VegasDisplay::StyleRva);
                if(!displayStyleVerified)break;
            }
            if(!Dedicated){
                if(!NativeUnlockPolicy::Install(base))break;
                unlockStatus=int(MH_CreateHook(base+0x563150,reinterpret_cast<void*>(NativeUnlockEligibility),&OriginalUnlockFacade));
                if(unlockStatus==MH_ERROR_NOT_EXECUTABLE){executablePagesReady=false;Sleep(100);continue;}
                if(unlockStatus!=MH_OK)break;
            }
            taskStatus=int(MH_CreateHook(base+loginRva,reinterpret_cast<void*>(DiscordTaskStart),reinterpret_cast<void**>(&OriginalTaskStart)));
            ready=taskStatus==MH_OK;
            if(ready&&!Dedicated){prefillStatus=int(MH_CreateHook(base+0xb7230,reinterpret_cast<void*>(DiscordLoginFields),reinterpret_cast<void**>(&OriginalLoadLogin)));ready=prefillStatus==MH_OK;}
            if(!ready){
                // No native hook in this batch is enabled yet. Remove only
                // this batch before retrying a protection transition.
                if(taskStatus==MH_OK)MH_RemoveHook(base+loginRva);
                if(!Dedicated&&unlockStatus==MH_OK)MH_RemoveHook(base+0x563150);
                if(taskStatus==MH_ERROR_NOT_EXECUTABLE||prefillStatus==MH_ERROR_NOT_EXECUTABLE){
                    executablePagesReady=false;Sleep(100);continue;
                }
                break;
            }
            if(ready)ready=MH_EnableHook(base+loginRva)==MH_OK;
            if(ready&&!Dedicated)ready=MH_EnableHook(base+0xb7230)==MH_OK;
            if(ready&&!Dedicated){
                // Replacement progression policy: use durable raw XP in the
                // shipped rank/award paths instead of their fixed-2M stub.
                // Every recovered caller supplies this same profile in ECX.
                BYTE jump[]{0xe9,0,0,0,0,0x90};const LONG delta=LONG(getterRva)-LONG(stubRva)-5;memcpy(jump+1,&delta,4);DWORD protection=0;
                ready=VirtualProtect(base+stubRva,sizeof(jump),PAGE_EXECUTE_READWRITE,&protection)!=FALSE;
                if(ready){memcpy(base+stubRva,jump,sizeof(jump));FlushInstructionCache(GetCurrentProcess(),base+stubRva,sizeof(jump));DWORD ignored;VirtualProtect(base+stubRva,sizeof(jump),protection,&ignored);}
            }
            if(ready&&!Dedicated){
                ready=MH_EnableHook(base+0x563150)==MH_OK;
                BYTE expected[]{0xe9,0,0,0,0};
                const LONG delta=LONG(reinterpret_cast<ULONG_PTR>(NativeUnlockEligibility)-reinterpret_cast<ULONG_PTR>(base+0x563150)-5);
                memcpy(expected+1,&delta,4);
                unlockVerified=ready&&!memcmp(base+0x563150,expected,sizeof(expected));
                ready=unlockVerified;
            }
            if(ready&&!Dedicated)ready=ClaimedName::Install(base,Output);
            if(ready&&!Dedicated)ready=NativeClaimedSessionName::Install(base);
            if(ready&&!Dedicated)ready=NativePecDefaults::Install(base,Output);
            if(ready&&!Dedicated)ready=LaunchOptions::InstallNative(base);
            if(ready&&!Dedicated&&!RankDisabled)ready=NativeRank::Install(base);
            if(ready&&!Dedicated&&!ReportXpDisabled)ready=NativeReportXp::Install(base);
            if(ready&&!Dedicated&&!RankedAccessDisabled)ready=NativeRankedAccess::Install(base);
            if(ready)ready=NativeHostStatistics::Install(base,Dedicated);
            if(ready)ready=NativeRankedHost::Install(base,Dedicated);
            if(ready&&!SoloHuntDisabled)ready=NativeSoloHunt::Install(base,Dedicated);
            if(ready&&!Dedicated&&!ProceduralAnimationDisabled)ready=NativeProceduralAnimation::Install(base);
            if(ready&&!Dedicated&&!PlayerLimitDisabled)ready=NativePlayerLimit::Install(base);
            if(ready&&!Dedicated&&!FireSoundLoopDisabled)ready=NativeFireSoundLoop::Install(base);
            break;
        }
        Sleep(100);
    }
    FILE* status=nullptr;_wfopen_s(&status,(Output+L"\\compat-status.tmp").c_str(),L"wb");
    if(status){fprintf(status,"{\"ready\":%s,\"executablePagesReady\":%s,\"unlockTargetProtection\":%lu,\"unlockDetourProtection\":%lu,\"displayStyleVerified\":%s,\"claimedNameVerified\":%s,\"claimedNameInstalled\":%s,\"claimedNameStatus\":\"%s\",\"claimedSessionNameVerified\":%s,\"claimedSessionNameStatus\":\"%s\",\"reportXpVerified\":%s,\"reportXpStatus\":\"%s\",\"nativeRankVerified\":%s,\"remotePipelineRecognized\":%s,\"nativeRankStatus\":\"%s\",\"rankedAccessVerified\":%s,\"rankedAccessStatus\":\"%s\",\"nativeSoloHuntVerified\":%s,\"nativeSoloHuntStatus\":\"%s\",\"nativeStatisticsVerified\":%s,\"nativeRankedHostVerified\":%s,\"nativeRankedHostStatus\":\"%s\",\"nativePecDefaultsVerified\":%s,\"nativePecDefaultsStatus\":\"%s\",\"proceduralAnimationVerified\":%s,\"proceduralAnimationStatus\":\"%s\",\"playerLimitVerified\":%s,\"playerLimitStatus\":\"%s\",\"fireSoundLoopVerified\":%s,\"fireSoundLoopStatus\":\"%s\",\"nativeVersion\":%u,\"signatures\":%u,\"taskHookStatus\":%d,\"prefillHookStatus\":%d,\"unlockHookStatus\":%d,\"nativeUnlockPolicyVerified\":%s,\"nativeUnlockPolicyStatus\":\"%s\",\"unlockVerified\":%s,\"moduleBase\":%lu,\"progression\":\"%s\",\"unlockPolicy\":\"%s\",\"disabledFeatures\":[%s]}\n",ready?"true":"false",executablePagesReady?"true":"false",unlockTargetProtection,unlockDetourProtection,displayStyleVerified?"true":"false",ClaimedName::Verified?"true":"false",ClaimedName::Installed?"true":"false",Dedicated?"notApplicable":ClaimedName::Error,NativeClaimedSessionName::Verified?"true":"false",Dedicated?"notApplicable":NativeClaimedSessionName::LastError(),NativeReportXp::Verified?"true":"false",Dedicated?"notApplicable":NativeReportXp::LastError(),NativeRank::Verified?"true":"false",NativeRank::RemotePipelineRecognized?"true":"false",Dedicated?"notApplicable":NativeRank::LastError(),NativeRankedAccess::Verified?"true":"false",Dedicated?"notApplicable":NativeRankedAccess::LastError(),NativeSoloHunt::Verified?"true":"false",NativeSoloHunt::LastError(),NativeHostStatistics::Verified?"true":"false",NativeRankedHost::Verified?"true":"false",NativeRankedHost::LastError(),NativePecDefaults::Verified?"true":"false",Dedicated?"notApplicable":NativePecDefaults::LastError(),NativeProceduralAnimation::Verified?"true":"false",Dedicated?"notApplicable":NativeProceduralAnimation::Error,NativePlayerLimit::Verified?"true":"false",Dedicated?"notApplicable":NativePlayerLimit::LastError(),NativeFireSoundLoop::Verified?"true":"false",Dedicated?"notApplicable":NativeFireSoundLoop::LastError(),Dedicated?4u:14u,signatures,taskStatus,prefillStatus,unlockStatus,NativeUnlockPolicy::Verified?"true":"false",Dedicated?"notApplicable":NativeUnlockPolicy::LastError(),unlockVerified?"true":"false",DWORD(reinterpret_cast<ULONG_PTR>(base)),Dedicated?"nativeDedicatedDefaults":"nativeRawXp",Dedicated?"nativeDedicatedDefaults":"nativeItemBundleAvailability",DisabledFeatures().c_str());fclose(status);MoveFileExW((Output+L"\\compat-status.tmp").c_str(),(Output+L"\\compat-status.json").c_str(),MOVEFILE_REPLACE_EXISTING);}
    return ready?1:0;
}
extern "C" DWORD WINAPI StartCompanion(LPVOID configuration){
    std::wstring all=static_cast<const wchar_t*>(configuration);size_t a=all.find(L'\n'),b=all.find(L'\n',a+1);
    if(a==std::wstring::npos||b==std::wstring::npos)return 0;
    Output=all.substr(0,a);WorkingRoot=all.substr(a+1,b-a-1);SourceRoot=all.substr(b+1);
    RegisteredExecutable=SourceRoot+(Dedicated?L"\\Binaries\\R6VegasServer.exe":L"\\Binaries\\R6Vegas_Game.exe");ProfileDirectory=Output+L"\\profile";
    if(!Dedicated){
        FILE* display=nullptr;
        if(!_wfopen_s(&display,(Output+L"\\display-mode.txt").c_str(),L"rb")&&display){
            char mode[16]{};bool validMode=fscanf_s(display,"%15s",mode,unsigned(sizeof(mode)))==1;
            fclose(display);
            if(!validMode||(strcmp(mode,"borderless")&&strcmp(mode,"windowed")&&strcmp(mode,"native")))return 0;
            BorderlessRequested=!strcmp(mode,"borderless");
            WindowedRequested=BorderlessRequested||!strcmp(mode,"windowed");
        }
        FILE* intros=nullptr;
        if(!_wfopen_s(&intros,(Output+L"\\skip-intros.txt").c_str(),L"rb")&&intros){
            char value[16]{};const bool valid=fscanf_s(intros,"%15s",value,unsigned(sizeof(value)))==1;
            fclose(intros);if(!valid||(strcmp(value,"skip")&&strcmp(value,"play")))return 0;
            SkipIntrosRequested=!strcmp(value,"skip");
        }
        ReadNativeFeatures();
    }
    if(!LocalPhysics())return 0;
    FILE* settings=nullptr;_wfopen_s(&settings,(Output+L"\\native-route.txt").c_str(),L"rb");
    if(!settings)return 0;
    char address[64]{},capability[65]{},alias[16]{};unsigned port=0,ipc=0;
    bool valid=fscanf_s(settings,"%63s %u %u %64s %15s",address,unsigned(sizeof(address)),&port,&ipc,capability,unsigned(sizeof(capability)),alias,unsigned(sizeof(alias)))==5;
    fclose(settings);
    if(!valid||!port||port>65535||!ipc||ipc>65535||strlen(capability)!=64||strlen(alias)!=15||InetPtonA(AF_INET,address,&RouteIp)!=1)return 0;
    RouteAddress=address;RouteTcpPort=port;IpcPort=ipc;NativeAlias=alias;IpcCapability.assign(capability,capability+64);
    CreateDirectoryW(ProfileDirectory.c_str(),nullptr);CreateDirectoryW((Output+L"\\agora-session").c_str(),nullptr);
    if(MH_Initialize()!=MH_OK)return 0;
    LoadLibraryW(L"shell32.dll");LoadLibraryW(L"ws2_32.dll");bool ready=true;
#define HOOK(module,name,fn,original) ready&=HookAPI(L##module,name,reinterpret_cast<void*>(fn),reinterpret_cast<void**>(&original))
    HOOK("kernel32.dll","GetModuleFileNameW",RegisteredModuleNameW,OriginalModuleNameW);
    HOOK("kernel32.dll","GetModuleFileNameA",RegisteredModuleNameA,OriginalModuleNameA);
    HOOK("shell32.dll","SHGetFolderPathW",PrivateFolder,OriginalFolder);
    HOOK("kernel32.dll","CreateFileW",PrivateFile,OriginalFileOpen);
    HOOK("kernel32.dll","CreateFileA",PrivateFileA,OriginalFileOpenA);
    HOOK("kernel32.dll","SetCurrentDirectoryW",PrivateSetCwdW,OriginalSetCwdW);
    HOOK("kernel32.dll","SetCurrentDirectoryA",PrivateSetCwdA,OriginalSetCwdA);
    HOOK("kernel32.dll","CreateDirectoryW",PrivateDirectory,OriginalDirectoryCreate);
    HOOK("ntdll.dll","NtCreateFile",PrivateNativeFile,OriginalNativeFileOpen);
    HOOK("ws2_32.dll","gethostbyname",CompanionResolveHost,OriginalResolveHost);
    HOOK("ws2_32.dll","getaddrinfo",CompanionResolveAddr,OriginalResolveAddr);
    HOOK("ws2_32.dll","connect",CompanionConnect,OriginalConnectSocket);
#undef HOOK
    if(!Dedicated)ready=ready&&LaunchOptions::Arm(Output,WindowedRequested,SkipIntrosRequested);
    if(!ready||MH_EnableHook(MH_ALL_HOOKS)!=MH_OK){MH_Uninitialize();return 0;}
    HANDLE thread=CreateThread(nullptr,0,NativeInitialize,nullptr,0,nullptr);
    if(!thread)return 0;CloseHandle(thread);return 1;
}
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH)DisableThreadLibraryCalls(instance);return TRUE;}
