// Task-scoped Steam launch gate. Only the configured retail executable matches.
// No Steam files/settings are patched and all hooks are removed after setup.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <string>
#include "MinHook.h"

static std::wstring Expected,Working,Desktop,Output;
static DWORD OwnedPid=0,OwnedTid=0;
using CreateW=decltype(&CreateProcessW);static CreateW OriginalW;
using CreateA=decltype(&CreateProcessA);static CreateA OriginalA;
using Resume=decltype(&ResumeThread);static Resume OriginalResume;
using NtResume=LONG(WINAPI*)(HANDLE,PULONG);static NtResume OriginalNtResume;
using Foreground=decltype(&SetForegroundWindow);static Foreground OriginalForeground;
using Bring=decltype(&BringWindowToTop);static Bring OriginalBring;
using Switch=decltype(&SwitchToThisWindow);static Switch OriginalSwitch;
static bool OwnedWindow(HWND window){DWORD pid=0;GetWindowThreadProcessId(window,&pid);return OwnedPid&&pid==OwnedPid;}
static BOOL WINAPI GateForeground(HWND window){return OwnedWindow(window)?FALSE:OriginalForeground(window);}
static BOOL WINAPI GateBring(HWND window){return OwnedWindow(window)?FALSE:OriginalBring(window);}
static void WINAPI GateSwitch(HWND window,BOOL alt){if(!OwnedWindow(window))OriginalSwitch(window,alt);}

static std::wstring FirstToken(const std::wstring& command){if(command.empty())return {};if(command[0]==L'"')return command.substr(1,command.find(L'"',1)-1);return command.substr(0,command.find(L' '));}
static bool Match(LPCWSTR application,LPCWSTR command){std::wstring path=application?application:FirstToken(command?command:L"");for(auto& c:path)if(c==L'/')c=L'\\';return !_wcsicmp(path.c_str(),Expected.c_str());}
static void Created(const PROCESS_INFORMATION* info){
    OwnedPid=info->dwProcessId;OwnedTid=info->dwThreadId;
    FILE* file=nullptr;_wfopen_s(&file,(Output+L"/child.tmp").c_str(),L"wb");
    if(file){fprintf(file,"{\"pid\":%lu,\"tid\":%lu,\"gated\":true}\n",OwnedPid,OwnedTid);fclose(file);MoveFileExW((Output+L"/child.tmp").c_str(),(Output+L"/child.json").c_str(),MOVEFILE_REPLACE_EXISTING);}
}
static BOOL WINAPI GateW(LPCWSTR application,LPWSTR command,LPSECURITY_ATTRIBUTES processSecurity,LPSECURITY_ATTRIBUTES threadSecurity,BOOL inherit,DWORD flags,LPVOID environment,LPCWSTR cwd,LPSTARTUPINFOW startup,LPPROCESS_INFORMATION info){
    if(!Match(application,command))return OriginalW(application,command,processSecurity,threadSecurity,inherit,flags,environment,cwd,startup,info);
    if(OwnedPid){SetLastError(ERROR_BUSY);return FALSE;}
    LPWSTR oldDesktop=startup->lpDesktop;DWORD oldFlags=startup->dwFlags;WORD oldShow=startup->wShowWindow;
    startup->lpDesktop=const_cast<LPWSTR>(Desktop.c_str());
    std::wstring copied=Working+L"\\R6Vegas_Game.exe";
    std::wstring copiedCommand=L"\""+copied+L"\"";
    std::wstring originalCommand=command?command:L"";
    auto first=FirstToken(originalCommand);
    size_t consumed=first.size()+(originalCommand.size()&&originalCommand[0]==L'"'?2:0);
    copiedCommand+=originalCommand.substr(consumed);
    BOOL result=OriginalW(copied.c_str(),copiedCommand.data(),processSecurity,threadSecurity,inherit,flags|CREATE_SUSPENDED,environment,Working.c_str(),startup,info);
    startup->lpDesktop=oldDesktop;startup->dwFlags=oldFlags;startup->wShowWindow=oldShow;
    if(result)Created(info);return result;
}
static std::wstring Wide(LPCSTR text){if(!text)return {};int size=MultiByteToWideChar(CP_ACP,0,text,-1,nullptr,0);std::wstring result(size,L'\0');MultiByteToWideChar(CP_ACP,0,text,-1,result.data(),size);if(!result.empty())result.pop_back();return result;}
static std::string Narrow(const std::wstring& text){int size=WideCharToMultiByte(CP_ACP,0,text.c_str(),-1,nullptr,0,nullptr,nullptr);std::string result(size,'\0');WideCharToMultiByte(CP_ACP,0,text.c_str(),-1,result.data(),size,nullptr,nullptr);return result;}
static BOOL WINAPI GateA(LPCSTR application,LPSTR command,LPSECURITY_ATTRIBUTES processSecurity,LPSECURITY_ATTRIBUTES threadSecurity,BOOL inherit,DWORD flags,LPVOID environment,LPCSTR cwd,LPSTARTUPINFOA startup,LPPROCESS_INFORMATION info){
    auto app=Wide(application),cmd=Wide(command);
    if(!Match(application?app.c_str():nullptr,cmd.c_str()))return OriginalA(application,command,processSecurity,threadSecurity,inherit,flags,environment,cwd,startup,info);
    if(OwnedPid){SetLastError(ERROR_BUSY);return FALSE;}
    auto desktop=Narrow(Desktop),working=Narrow(Working);
    LPSTR oldDesktop=startup->lpDesktop;DWORD oldFlags=startup->dwFlags;WORD oldShow=startup->wShowWindow;
    startup->lpDesktop=desktop.data();
    auto copied=Narrow(Working+L"\\R6Vegas_Game.exe");
    std::string originalCommand=command?command:"";
    auto first=FirstToken(cmd);
    size_t consumed=Narrow(first).size()+(originalCommand.size()&&originalCommand[0]=='"'?2:0);
    std::string copiedCommand="\""+copied+"\""+originalCommand.substr(consumed);
    BOOL result=OriginalA(copied.c_str(),copiedCommand.data(),processSecurity,threadSecurity,inherit,flags|CREATE_SUSPENDED,environment,working.c_str(),startup,info);
    startup->lpDesktop=oldDesktop;startup->dwFlags=oldFlags;startup->wShowWindow=oldShow;
    if(result)Created(info);return result;
}
static bool IsOwnedThread(HANDLE thread){return OwnedTid&&GetThreadId(thread)==OwnedTid;}
static DWORD WINAPI GateResume(HANDLE thread){if(IsOwnedThread(thread))return 1;return OriginalResume(thread);}
static LONG WINAPI GateNtResume(HANDLE thread,PULONG previous){if(IsOwnedThread(thread)){if(previous)*previous=1;return 0;}return OriginalNtResume(thread,previous);}
static bool Hook(const wchar_t* module,const char* name,void* function,void** original){return MH_CreateHookApi(module,name,function,original)==MH_OK;}
extern "C" DWORD WINAPI StartGate(LPVOID configuration){
    std::wstring all=static_cast<LPCWSTR>(configuration);size_t a=all.find(L'\n'),b=all.find(L'\n',a+1),z=all.find(L'\n',b+1);
    if(a==std::wstring::npos||b==std::wstring::npos||z==std::wstring::npos)return 0;
    Expected=all.substr(0,a);Working=all.substr(a+1,b-a-1);Desktop=all.substr(b+1,z-b-1);Output=all.substr(z+1);
    if(MH_Initialize()!=MH_OK)return 0;
    bool ready=Hook(L"kernel32.dll","CreateProcessW",reinterpret_cast<void*>(GateW),reinterpret_cast<void**>(&OriginalW))&&Hook(L"kernel32.dll","CreateProcessA",reinterpret_cast<void*>(GateA),reinterpret_cast<void**>(&OriginalA))&&Hook(L"kernel32.dll","ResumeThread",reinterpret_cast<void*>(GateResume),reinterpret_cast<void**>(&OriginalResume))&&Hook(L"ntdll.dll","NtResumeThread",reinterpret_cast<void*>(GateNtResume),reinterpret_cast<void**>(&OriginalNtResume));
    if(!ready||MH_EnableHook(MH_ALL_HOOKS)!=MH_OK){MH_Uninitialize();return 0;}return 1;
}
extern "C" DWORD WINAPI StopGate(LPVOID){MH_DisableHook(MH_ALL_HOOKS);MH_Uninitialize();return 1;}
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH)DisableThreadLibraryCalls(instance);return TRUE;}
