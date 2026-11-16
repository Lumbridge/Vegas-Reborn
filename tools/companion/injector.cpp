#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <cstdio>
#include <cstdint>
#include <string>

static DWORD Call(HANDLE process,LPTHREAD_START_ROUTINE function,LPVOID argument){
    HANDLE thread=CreateRemoteThread(process,nullptr,0,function,argument,0,nullptr);
    if(!thread){fprintf(stderr,"CreateRemoteThread: %lu\n",GetLastError());return 0;}
    DWORD result=0;
    if(WaitForSingleObject(thread,15000)==WAIT_OBJECT_0)GetExitCodeThread(thread,&result);
    else fprintf(stderr,"Remote setup deadline\n");
    CloseHandle(thread);return result;
}
static LPVOID Copy(HANDLE process,const std::wstring& text){
    SIZE_T bytes=(text.size()+1)*sizeof(wchar_t),written=0;
    LPVOID memory=VirtualAllocEx(process,nullptr,bytes,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE);
    if(!memory||!WriteProcessMemory(process,memory,text.c_str(),bytes,&written)||written!=bytes)return nullptr;
    return memory;
}
static uintptr_t Module(DWORD pid,const wchar_t* name){
    HANDLE snapshot=INVALID_HANDLE_VALUE;
    for(int attempt=0;attempt<50;++attempt){
        snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPMODULE|TH32CS_SNAPMODULE32,pid);
        if(snapshot!=INVALID_HANDLE_VALUE||GetLastError()!=ERROR_BAD_LENGTH)break;
        Sleep(10);
    }
    if(snapshot==INVALID_HANDLE_VALUE)return 0;
    MODULEENTRY32W item{};item.dwSize=sizeof(item);uintptr_t result=0;
    if(Module32FirstW(snapshot,&item))do{if(!_wcsicmp(item.szModule,name)||!_wcsicmp(item.szExePath,name)){result=reinterpret_cast<uintptr_t>(item.modBaseAddr);break;}}while(Module32NextW(snapshot,&item));
    CloseHandle(snapshot);return result;
}
static LPTHREAD_START_ROUTINE SystemFunction(DWORD pid,const char* name){
    auto address=GetProcAddress(GetModuleHandleW(L"kernel32.dll"),name);HMODULE owner=nullptr;
    if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,reinterpret_cast<LPCWSTR>(address),&owner))return nullptr;
    wchar_t path[MAX_PATH];GetModuleFileNameW(owner,path,MAX_PATH);auto base=Module(pid,wcsrchr(path,L'\\')+1);
    // A CREATE_SUSPENDED client has not loaded kernel32/kernelbase yet. The
    // remote thread runs loader initialization before this system entry point.
    // Same-bitness system DLLs share their image mapping on this Windows boot.
    return base?reinterpret_cast<LPTHREAD_START_ROUTINE>(base+(reinterpret_cast<uintptr_t>(address)-reinterpret_cast<uintptr_t>(owner))):reinterpret_cast<LPTHREAD_START_ROUTINE>(address);
}
static LPTHREAD_START_ROUTINE Export(uintptr_t remote,const wchar_t* path,const char* name){
    HMODULE local=LoadLibraryExW(path,nullptr,DONT_RESOLVE_DLL_REFERENCES);if(!local)return nullptr;
    auto address=GetProcAddress(local,name);auto target=address?reinterpret_cast<LPTHREAD_START_ROUTINE>(remote+(reinterpret_cast<uintptr_t>(address)-reinterpret_cast<uintptr_t>(local))):nullptr;
    FreeLibrary(local);return target;
}
int wmain(int argc,wchar_t** argv){
    bool configure=argc==6&&!wcscmp(argv[1],L"--configure"),stop=argc==5&&!wcscmp(argv[1],L"--stop-gate");
    if(argc!=7&&!configure&&!stop){fprintf(stderr,"Expected owned PID/TID/DLL/output/game/source, --configure PID DLL export config, or --stop-gate PID module DLL\n");return 2;}
    DWORD pid=wcstoul(argv[configure||stop?2:1],nullptr,10);
    HANDLE process=OpenProcess(PROCESS_CREATE_THREAD|PROCESS_QUERY_INFORMATION|PROCESS_VM_OPERATION|PROCESS_VM_WRITE|PROCESS_VM_READ,FALSE,pid);
    if(!process){fprintf(stderr,"OpenProcess: %lu\n",GetLastError());return 3;}
    const wchar_t* dllPath=argv[stop?4:3];uintptr_t module=0;
    if(stop){module=static_cast<uintptr_t>(_wcstoui64(argv[3],nullptr,10));}
    else{
        LPVOID dll=Copy(process,dllPath);if(!dll){CloseHandle(process);return 4;}
        DWORD loaded=Call(process,SystemFunction(pid,"LoadLibraryW"),dll);VirtualFreeEx(process,dll,0,MEM_RELEASE);
        // x86 LoadLibrary returns the full module address in the thread result.
        // A new WOW64 client's module snapshot can transiently be incomplete.
        module=sizeof(void*)==4?static_cast<uintptr_t>(loaded):Module(pid,dllPath);
        if(!module)fprintf(stderr,"LoadLibrary thread result=%lu; module lookup error=%lu\n",loaded,GetLastError());
    }
    if(!module){fprintf(stderr,"Remote DLL load/module lookup failed\n");CloseHandle(process);return 4;}
    std::string selectedExport;
    if(configure){
        for(const wchar_t* value=argv[4];*value;++value){
            if(selectedExport.size()>=64||!((*value>=L'A'&&*value<=L'Z')||(*value>=L'a'&&*value<=L'z')||(*value>=L'0'&&*value<=L'9')||*value==L'_')){CloseHandle(process);return 5;}
            selectedExport.push_back(char(*value));
        }
        if(selectedExport.empty()){CloseHandle(process);return 5;}
    }
    const char* name=stop?"StopGate":configure?selectedExport.c_str():"StartCompanion";
    auto function=Export(module,dllPath,name);if(!function){CloseHandle(process);return 5;}
    std::wstring settings;
    if(configure){
        FILE* file=nullptr;_wfopen_s(&file,argv[5],L"rb");if(!file){CloseHandle(process);return 5;}
        fseek(file,0,SEEK_END);long bytes=ftell(file);rewind(file);
        if(bytes<0||bytes%2){fclose(file);CloseHandle(process);return 5;}
        settings.resize(bytes/2);fread(settings.data(),1,bytes,file);fclose(file);
    }else if(!stop)settings=std::wstring(argv[4])+L"\n"+argv[5]+L"\n"+argv[6];
    LPVOID configuration=stop?nullptr:Copy(process,settings);
    DWORD ready=Call(process,function,configuration);
    if(configuration)VirtualFreeEx(process,configuration,0,MEM_RELEASE);
    if(ready!=1){fprintf(stderr,"Setup rejected: %lu\n",ready);CloseHandle(process);return 6;}
    if(stop){Call(process,SystemFunction(pid,"FreeLibrary"),reinterpret_cast<LPVOID>(module));}
    DWORD previous=0;
    if(!configure&&!stop){
        HANDLE thread=OpenThread(THREAD_SUSPEND_RESUME,FALSE,wcstoul(argv[2],nullptr,10));if(!thread){CloseHandle(process);return 7;}
        previous=ResumeThread(thread);CloseHandle(thread);if(previous==DWORD(-1)){CloseHandle(process);return 8;}
    }
    printf("{\"pid\":%lu,\"module\":%llu,\"previousSuspendCount\":%lu,\"ready\":true}\n",pid,static_cast<unsigned long long>(module),previous);
    CloseHandle(process);return 0;
}
