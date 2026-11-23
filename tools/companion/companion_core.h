// Game-independent companion hooks shared by every title adapter: executable
// identity, per-account profile folders, installed-tree isolation, service
// host/port redirection and the loopback credential IPC.
// The including adapter defines RedirectedHost(name) after this header.
#pragma once

static std::wstring Output,ProfileDirectory,WorkingRoot,SourceRoot,RegisteredExecutable;
static std::string RouteAddress;
static unsigned RouteTcpPort=3074;
static in_addr RouteIp{};
static unsigned IpcPort=0;
static std::wstring IpcCapability;
static std::string NativeAlias;
static void Log(const char*) {} // No account data or packet logging in normal play.

using ModuleNameW=decltype(&GetModuleFileNameW);static ModuleNameW OriginalModuleNameW;
using ModuleNameA=decltype(&GetModuleFileNameA);static ModuleNameA OriginalModuleNameA;
static DWORD WINAPI RegisteredModuleNameW(HMODULE module,LPWSTR output,DWORD size){
    if(module&&module!=GetModuleHandleW(nullptr))return OriginalModuleNameW(module,output,size);
    if(size<=RegisteredExecutable.size()){SetLastError(ERROR_INSUFFICIENT_BUFFER);return size;}
    wcscpy_s(output,size,RegisteredExecutable.c_str());return DWORD(RegisteredExecutable.size());
}
static DWORD WINAPI RegisteredModuleNameA(HMODULE module,LPSTR output,DWORD size){
    if(module&&module!=GetModuleHandleW(nullptr))return OriginalModuleNameA(module,output,size);
    int required=WideCharToMultiByte(CP_ACP,0,RegisteredExecutable.c_str(),-1,nullptr,0,nullptr,nullptr);
    if(size<DWORD(required)){SetLastError(ERROR_INSUFFICIENT_BUFFER);return size;}
    WideCharToMultiByte(CP_ACP,0,RegisteredExecutable.c_str(),-1,output,int(size),nullptr,nullptr);return DWORD(required-1);
}

using ResolveHost=decltype(&gethostbyname);static ResolveHost OriginalResolveHost;
using ResolveAddr=decltype(&getaddrinfo);static ResolveAddr OriginalResolveAddr;
using ConnectSocket=decltype(&connect);static ConnectSocket OriginalConnectSocket;
static bool RedirectedHost(const char* name);
static hostent* WSAAPI CompanionResolveHost(const char* name){return OriginalResolveHost(RedirectedHost(name)?RouteAddress.c_str():name);}
static int WSAAPI CompanionResolveAddr(PCSTR name,PCSTR service,const ADDRINFOA* hints,PADDRINFOA* result){return OriginalResolveAddr(RedirectedHost(name)?RouteAddress.c_str():name,service,hints,result);}
static int WSAAPI CompanionConnect(SOCKET socket,const sockaddr* address,int bytes){
    if(address&&bytes>=sizeof(sockaddr_in)&&address->sa_family==AF_INET){
        auto original=reinterpret_cast<const sockaddr_in*>(address);
        // The original bootstrap can already target native TCP3074 before
        // loading downloaded configuration. Custom realms must route that
        // control endpoint as well as the original HTTP80 endpoint.
        const unsigned originalPort=ntohs(original->sin_port);
        if(original->sin_addr.s_addr==RouteIp.s_addr&&(originalPort==80||originalPort==3074)){
            sockaddr_in mapped=*original;mapped.sin_port=htons(u_short(RouteTcpPort));
            return OriginalConnectSocket(socket,reinterpret_cast<sockaddr*>(&mapped),sizeof(mapped));
        }
    }
    return OriginalConnectSocket(socket,address,bytes);
}

// Profile and installed-file protection hooks follow below.

using FolderFn=decltype(&SHGetFolderPathW);static FolderFn OriginalFolder;
static HRESULT WINAPI PrivateFolder(HWND window,int folder,HANDLE token,DWORD flags,LPWSTR output){
    if((folder&0xff)==CSIDL_PERSONAL&&!ProfileDirectory.empty()){
        wcscpy_s(output,MAX_PATH,ProfileDirectory.c_str());return S_OK;
    }
    if(((folder&0xff)==CSIDL_APPDATA||(folder&0xff)==CSIDL_LOCAL_APPDATA)&&!ProfileDirectory.empty()){
        const std::wstring root=ProfileDirectory+L"\\AppData";CreateDirectoryW(root.c_str(),nullptr);
        const std::wstring target=root+((folder&0xff)==CSIDL_APPDATA?L"\\Roaming":L"\\Local");CreateDirectoryW(target.c_str(),nullptr);
        wcscpy_s(output,MAX_PATH,target.c_str());return S_OK;
    }
    return OriginalFolder(window,folder,token,flags,output);
}

using FileOpen=decltype(&CreateFileW);static FileOpen OriginalFileOpen;
using FileOpenA=decltype(&CreateFileA);static FileOpenA OriginalFileOpenA;
using DirectoryCreate=decltype(&CreateDirectoryW);static DirectoryCreate OriginalDirectoryCreate;
using SetCwdW=decltype(&SetCurrentDirectoryW);static SetCwdW OriginalSetCwdW;
using SetCwdA=decltype(&SetCurrentDirectoryA);static SetCwdA OriginalSetCwdA;
using NativeFileOpen=NTSTATUS(NTAPI*)(PHANDLE,ACCESS_MASK,POBJECT_ATTRIBUTES,PIO_STATUS_BLOCK,PLARGE_INTEGER,ULONG,ULONG,ULONG,ULONG,PVOID,ULONG);
static NativeFileOpen OriginalNativeFileOpen;
static std::wstring Redirect(LPCWSTR path){
    if(!path||WorkingRoot.empty())return path?path:L"";
    wchar_t full[32768];DWORD size=GetFullPathNameW(path,32768,full,nullptr);
    if(!size||size>=32768)return path;
    std::wstring value=full;
    for(auto& c:value)if(c==L'/')c=L'\\';
    if(!_wcsicmp(value.c_str(),SourceRoot.c_str()))return WorkingRoot;
    if(value.size()>SourceRoot.size()&&!_wcsnicmp(value.c_str(),SourceRoot.c_str(),SourceRoot.size())&&value[SourceRoot.size()]==L'\\'){
        std::wstring rest=value.substr(SourceRoot.size()+1);
        if(!_wcsnicmp(rest.c_str(),L"KellerGame\\",11)||!_wcsnicmp(rest.c_str(),L"Engine\\",7)||
           (!_wcsnicmp(rest.c_str(),L"Binaries",8)&&(rest.size()==8||rest[8]==L'\\')))value=WorkingRoot+L"\\"+rest;
    }
    const std::wstring session=WorkingRoot+L"\\KellerGame\\Content\\Agora\\session";
    if(value.size()>=session.size()&&!_wcsnicmp(value.c_str(),session.c_str(),session.size())&&(value.size()==session.size()||value[session.size()]==L'\\'))
        return Output+L"\\agora-session"+value.substr(session.size());
    return value;
}
static std::wstring WidePath(LPCSTR path){
    if(!path)return L"";
    int count=MultiByteToWideChar(CP_ACP,0,path,-1,nullptr,0);
    std::wstring value(count,L'\0');MultiByteToWideChar(CP_ACP,0,path,-1,value.data(),count);
    if(!value.empty())value.pop_back();return value;
}
static BOOL WINAPI PrivateSetCwdW(LPCWSTR path){
    std::wstring target=Redirect(path);char line[1400];sprintf_s(line,"process current directory -> %ls",target.c_str());Log(line);
    return OriginalSetCwdW(target.c_str());
}
static BOOL WINAPI PrivateSetCwdA(LPCSTR path){return PrivateSetCwdW(WidePath(path).c_str());}
static HANDLE WINAPI PrivateFile(LPCWSTR name,DWORD access,DWORD sharing,LPSECURITY_ATTRIBUTES security,DWORD disposition,DWORD attributes,HANDLE templateFile){
    std::wstring target=Redirect(name);HANDLE file=OriginalFileOpen(target.c_str(),access,sharing,security,disposition,attributes,templateFile);
    return file;
}
static HANDLE WINAPI PrivateFileA(LPCSTR name,DWORD access,DWORD sharing,LPSECURITY_ATTRIBUTES security,DWORD disposition,DWORD attributes,HANDLE templateFile){
    return PrivateFile(WidePath(name).c_str(),access,sharing,security,disposition,attributes,templateFile);
}
static NTSTATUS NTAPI PrivateNativeFile(PHANDLE file,ACCESS_MASK access,POBJECT_ATTRIBUTES attributes,PIO_STATUS_BLOCK status,PLARGE_INTEGER allocation,ULONG flags,ULONG sharing,ULONG disposition,ULONG options,PVOID ea,ULONG eaBytes){
    if(attributes&&attributes->ObjectName){
        std::wstring name(attributes->ObjectName->Buffer,attributes->ObjectName->Length/sizeof(wchar_t));
        if(attributes->RootDirectory&&!name.empty()&&name[0]!=L'\\'){
            wchar_t root[32768];DWORD count=GetFinalPathNameByHandleW(attributes->RootDirectory,root,32768,0);
            if(count&&count<32768)name=std::wstring(root)+L"\\"+name;
        }
        if(name.size()>4&&(!name.compare(0,4,L"\\??\\")||!name.compare(0,4,L"\\\\?\\")))name.erase(0,4);
        const bool installed=name.size()>SourceRoot.size()&&!_wcsnicmp(name.c_str(),SourceRoot.c_str(),SourceRoot.size())&&name[SourceRoot.size()]==L'\\';
        const bool copied=name.size()>WorkingRoot.size()&&!_wcsnicmp(name.c_str(),WorkingRoot.c_str(),WorkingRoot.size())&&name[WorkingRoot.size()]==L'\\';
        if(installed||copied){
            std::wstring target=Redirect(name.c_str());
            if(_wcsicmp(target.c_str(),name.c_str())){
                target=L"\\??\\"+target;
                UNICODE_STRING unicode{USHORT(target.size()*2),USHORT((target.size()+1)*2),target.data()};
                OBJECT_ATTRIBUTES copy=*attributes;copy.RootDirectory=nullptr;copy.ObjectName=&unicode;
                return OriginalNativeFileOpen(file,access,&copy,status,allocation,flags,sharing,disposition,options,ea,eaBytes);
            }
            // Last-line guard for installed paths outside redirected game data.
            constexpr ACCESS_MASK writes=GENERIC_WRITE|GENERIC_ALL|MAXIMUM_ALLOWED|DELETE|WRITE_DAC|WRITE_OWNER|FILE_WRITE_DATA|FILE_APPEND_DATA|FILE_WRITE_EA|FILE_WRITE_ATTRIBUTES;
            if(installed&&((access&writes)||disposition!=1)){Log("blocked native write/open-create in installed game tree");return NTSTATUS(0xc0000022L);}
        }
    }
    return OriginalNativeFileOpen(file,access,attributes,status,allocation,flags,sharing,disposition,options,ea,eaBytes);
}
static BOOL WINAPI PrivateDirectory(LPCWSTR name,LPSECURITY_ATTRIBUTES security){std::wstring target=Redirect(name);return OriginalDirectoryCreate(target.c_str(),security);}

static bool Credentials(std::string& username,std::string& password){
    HINTERNET session=WinHttpOpen(L"VegasCompanion/1",WINHTTP_ACCESS_TYPE_NO_PROXY,nullptr,nullptr,0);
    if(!session)return false;
    WinHttpSetTimeouts(session,3000,3000,10000,10000);
    HINTERNET server=WinHttpConnect(session,L"127.0.0.1",INTERNET_PORT(IpcPort),0);
    HINTERNET request=server?WinHttpOpenRequest(server,L"POST",L"/native-credential",nullptr,WINHTTP_NO_REFERER,WINHTTP_DEFAULT_ACCEPT_TYPES,0):nullptr;
    bool valid=false;std::string body;
    if(request){
        std::wstring headers=L"Authorization: Bearer "+IpcCapability+L"\r\nContent-Length: 0\r\n";
        if(WinHttpSendRequest(request,headers.c_str(),DWORD(headers.size()),nullptr,0,0,0)&&WinHttpReceiveResponse(request,nullptr)){
            DWORD status=0,bytes=sizeof(status);
            if(WinHttpQueryHeaders(request,WINHTTP_QUERY_STATUS_CODE|WINHTTP_QUERY_FLAG_NUMBER,nullptr,&status,&bytes,nullptr)&&status==200){
                char chunk[128];DWORD count=0;
                while(WinHttpReadData(request,chunk,sizeof(chunk),&count)&&count){body.append(chunk,count);if(body.size()>128)break;}
                size_t split=body.find('\n'),end=body.find('\n',split+1);
                if(split!=std::string::npos&&end==body.size()-1){
                    username=body.substr(0,split);password=body.substr(split+1,end-split-1);
                    valid=username==NativeAlias&&!password.empty()&&password.size()<=15;
                    for(char c:password)if(!((c>='A'&&c<='Z')||(c>='a'&&c<='z')||(c>='0'&&c<='9')))valid=false;
                }
            }
        }
        WinHttpCloseHandle(request);
    }
    if(server)WinHttpCloseHandle(server);WinHttpCloseHandle(session);
    if(!body.empty())SecureZeroMemory(body.data(),body.size());
    return valid;
}

static bool HookAPI(const wchar_t* module,const char* name,void* replacement,void** original){return MH_CreateHookApi(module,name,replacement,original)==MH_OK;}
