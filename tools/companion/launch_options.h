#pragma once
#include <windows.h>
#include <d3d9.h>
#include <cstdio>
#include <cstring>
#include <string>
#include <intrin.h>
#include "MinHook.h"

namespace LaunchOptions {
static bool Windowed=false,SkipIntros=false,FactoryArmed=false,SequenceArmed=false,ViewportArmed=false,HookFailed=false;
static bool DeviceVerified=false,ActualWindowed=false;
static unsigned ResizeCalls=0;
static int RequestedFullscreen=-1,AppliedFullscreen=-1;
static void* IntroGroups[8]{};
static unsigned GroupsRegistered=0,GroupsSkipped=0;
static BYTE* Image=nullptr;
static std::wstring Output;
static SRWLOCK Lock=SRWLOCK_INIT;
constexpr unsigned ResizeRva=0x579350;
constexpr unsigned AppendRva=0x4c5470,CompleteRva=0x4c3270;
inline bool IntroCaller(unsigned rva){return rva==0x4c58bb||rva==0x4c59fe||rva==0x4c5cb9;}
inline bool SequenceMatches(BYTE* base){
    const BYTE append[]{0x53,0x8b,0x5c,0x24,0x08,0x56,0x8b,0xf1,0x8b,0x4e,0x04};
    const BYTE complete[]{0x56,0x57,0x8b,0xf9,0x33,0xf6,0x39,0x77,0x08};
    if(memcmp(base+AppendRva,append,sizeof(append))||memcmp(base+CompleteRva,complete,sizeof(complete)))return false;
    for(unsigned rva:{0x4c58bbu,0x4c59feu,0x4c5cb9u,0x4c5b96u}){
        auto call=base+rva-5;LONG delta=0;memcpy(&delta,call+1,4);
        if(call[0]!=0xe8||call+5+delta!=base+AppendRva)return false;
    }
    return true;
}
inline void Present(D3DPRESENT_PARAMETERS* parameters){
    if(Windowed&&parameters){parameters->Windowed=TRUE;parameters->FullScreen_RefreshRateInHz=0;}
}
inline void WriteLocked(){
    if(Output.empty())return;
    FILE* file=nullptr;
    if(_wfopen_s(&file,(Output+L"\\launch-options-status.tmp").c_str(),L"wb")||!file)return;
    fprintf(file,"{\"windowedRequested\":%s,\"skipIntrosRequested\":%s,\"factoryHookInstalled\":%s,\"introSequenceHookInstalled\":%s,\"viewportHookInstalled\":%s,\"hookFailed\":%s,\"deviceVerified\":%s,\"actualWindowed\":%s,\"resizeCalls\":%u,\"requestedFullscreen\":%d,\"appliedFullscreen\":%d,\"introGroupsRegistered\":%u,\"introGroupsSkipped\":%u}\n",
        Windowed?"true":"false",SkipIntros?"true":"false",FactoryArmed?"true":"false",SequenceArmed?"true":"false",ViewportArmed?"true":"false",HookFailed?"true":"false",DeviceVerified?"true":"false",ActualWindowed?"true":"false",ResizeCalls,RequestedFullscreen,AppliedFullscreen,GroupsRegistered,GroupsSkipped);
    fclose(file);MoveFileExW((Output+L"\\launch-options-status.tmp").c_str(),(Output+L"\\launch-options-status.json").c_str(),MOVEFILE_REPLACE_EXISTING);
}
inline void Report(){AcquireSRWLockExclusive(&Lock);WriteLocked();ReleaseSRWLockExclusive(&Lock);}
using Resize=void(__thiscall*)(void*,int,int,int);
static Resize OriginalResize=nullptr;
static void __fastcall ResizeWindow(void* viewport,void*,int x,int y,int fullscreen){
    OriginalResize(viewport,x,y,Windowed?0:fullscreen);
    AcquireSRWLockExclusive(&Lock);
    ++ResizeCalls;RequestedFullscreen=fullscreen;AppliedFullscreen=Windowed?0:fullscreen;WriteLocked();
    ReleaseSRWLockExclusive(&Lock);
}
inline bool ResizeMatches(BYTE* base){
    BYTE expected[]{0x6a,0xff,0x68,0,0,0,0,0x64,0xa1,0,0,0,0};
    DWORD handler=DWORD(reinterpret_cast<ULONG_PTR>(base+0x14a9572));memcpy(expected+3,&handler,4);
    return !memcmp(base+ResizeRva,expected,sizeof(expected));
}
using Append=int(__thiscall*)(void*,void**);
using Complete=int(__thiscall*)(void*);
static Append OriginalAppend=nullptr;
static Complete OriginalComplete=nullptr;
inline void RememberGroup(void* group,unsigned caller){
    if(!SkipIntros||!group||!IntroCaller(caller))return;
    AcquireSRWLockExclusive(&Lock);
    bool stored=false;
    for(auto& slot:IntroGroups)if(slot==group){stored=true;break;}
    if(!stored)for(auto& slot:IntroGroups)if(!slot){slot=group;++GroupsRegistered;stored=true;break;}
    if(!stored)HookFailed=true;
    WriteLocked();ReleaseSRWLockExclusive(&Lock);
}
static int __fastcall AppendGroup(void* array,void*,void** group){
    const auto caller=reinterpret_cast<ULONG_PTR>(_ReturnAddress());
    RememberGroup(group?*group:nullptr,unsigned(caller-reinterpret_cast<ULONG_PTR>(Image)));
    return OriginalAppend(array,group);
}
static int __fastcall GroupComplete(void* group,void*){
    bool skip=false;
    AcquireSRWLockExclusive(&Lock);
    if(SkipIntros)for(auto& slot:IntroGroups)if(slot==group&&slot){slot=nullptr;++GroupsSkipped;skip=true;break;}
    if(skip)WriteLocked();ReleaseSRWLockExclusive(&Lock);
    return skip?1:OriginalComplete(group);
}
inline bool InstallNative(BYTE* base){
    Image=base;
    if(Windowed){
        if(!ResizeMatches(base)||MH_CreateHook(base+ResizeRva,reinterpret_cast<void*>(ResizeWindow),reinterpret_cast<void**>(&OriginalResize))!=MH_OK)return false;
        ViewportArmed=MH_EnableHook(base+ResizeRva)==MH_OK;if(!ViewportArmed)return false;
    }
    if(SkipIntros){
        if(!SequenceMatches(base)||MH_CreateHook(base+AppendRva,reinterpret_cast<void*>(AppendGroup),reinterpret_cast<void**>(&OriginalAppend))!=MH_OK||
            MH_CreateHook(base+CompleteRva,reinterpret_cast<void*>(GroupComplete),reinterpret_cast<void**>(&OriginalComplete))!=MH_OK)return false;
        SequenceArmed=MH_EnableHook(base+AppendRva)==MH_OK&&MH_EnableHook(base+CompleteRva)==MH_OK;
        if(!SequenceArmed)return false;
    }
    Report();return true;
}
inline bool Hook(void* target,void* detour,void** original){
    const auto result=MH_CreateHook(target,detour,original);
    if(result!=MH_OK&&result!=MH_ERROR_ALREADY_CREATED)return false;
    const auto enabled=MH_EnableHook(target);
    return enabled==MH_OK||enabled==MH_ERROR_ENABLED;
}
inline void VerifyDevice(IDirect3DDevice9* device){
    IDirect3DSwapChain9* chain=nullptr;D3DPRESENT_PARAMETERS actual{};
    const bool verified=device&&SUCCEEDED(device->GetSwapChain(0,&chain))&&chain&&SUCCEEDED(chain->GetPresentParameters(&actual));
    if(chain)chain->Release();
    AcquireSRWLockExclusive(&Lock);DeviceVerified=verified;ActualWindowed=verified&&actual.Windowed;WriteLocked();ReleaseSRWLockExclusive(&Lock);
}
using Reset=HRESULT(WINAPI*)(IDirect3DDevice9*,D3DPRESENT_PARAMETERS*);
using Additional=HRESULT(WINAPI*)(IDirect3DDevice9*,D3DPRESENT_PARAMETERS*,IDirect3DSwapChain9**);
using CreateDevice=HRESULT(WINAPI*)(IDirect3D9*,UINT,D3DDEVTYPE,HWND,DWORD,D3DPRESENT_PARAMETERS*,IDirect3DDevice9**);
using Factory=IDirect3D9*(WINAPI*)(UINT);
static Reset OriginalReset=nullptr;
static Additional OriginalAdditional=nullptr;
static CreateDevice OriginalCreateDevice=nullptr;
static Factory OriginalFactory=nullptr;
static HRESULT WINAPI ResetDevice(IDirect3DDevice9* device,D3DPRESENT_PARAMETERS* parameters){
    Present(parameters);HRESULT result=OriginalReset(device,parameters);if(SUCCEEDED(result))VerifyDevice(device);return result;
}
static HRESULT WINAPI AdditionalChain(IDirect3DDevice9* device,D3DPRESENT_PARAMETERS* parameters,IDirect3DSwapChain9** chain){
    Present(parameters);return OriginalAdditional(device,parameters,chain);
}
static HRESULT WINAPI NewDevice(IDirect3D9* factory,UINT adapter,D3DDEVTYPE type,HWND window,DWORD behavior,D3DPRESENT_PARAMETERS* parameters,IDirect3DDevice9** output){
    Present(parameters);HRESULT result=OriginalCreateDevice(factory,adapter,type,window,behavior,parameters,output);
    if(SUCCEEDED(result)&&output&&*output){
        auto table=*reinterpret_cast<void***>(*output);
        AcquireSRWLockExclusive(&Lock);
        const bool reset=Hook(table[16],reinterpret_cast<void*>(ResetDevice),reinterpret_cast<void**>(&OriginalReset));
        const bool additional=Hook(table[13],reinterpret_cast<void*>(AdditionalChain),reinterpret_cast<void**>(&OriginalAdditional));
        HookFailed=HookFailed||!reset||!additional;ReleaseSRWLockExclusive(&Lock);
        VerifyDevice(*output);
    }
    return result;
}
static IDirect3D9* WINAPI NewFactory(UINT version){
    IDirect3D9* factory=OriginalFactory(version);
    if(factory){
        auto table=*reinterpret_cast<void***>(factory);
        AcquireSRWLockExclusive(&Lock);
        HookFailed=HookFailed||!Hook(table[16],reinterpret_cast<void*>(NewDevice),reinterpret_cast<void**>(&OriginalCreateDevice));
        WriteLocked();ReleaseSRWLockExclusive(&Lock);
    }
    return factory;
}
inline bool Arm(const std::wstring& output,bool windowed,bool skip){
    Output=output;Windowed=windowed;SkipIntros=skip;
    if(Windowed){
        if(!LoadLibraryW(L"d3d9.dll"))return false;
        FactoryArmed=MH_CreateHookApi(L"d3d9.dll","Direct3DCreate9",reinterpret_cast<void*>(NewFactory),reinterpret_cast<void**>(&OriginalFactory))==MH_OK;
        if(!FactoryArmed)return false;
    }
    Report();return true;
}
}
