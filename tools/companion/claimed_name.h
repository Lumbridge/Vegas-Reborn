#pragma once

// Original PC client only. Include in the companion translation unit and call
// Install before the launcher permits player travel. These are five-byte CALL
// replacements, not a safe hot-update mechanism for a running match.
#include <windows.h>
#include <cstdio>
#include <cstring>
#include <string>

namespace ClaimedName {
static bool Installed=false,Verified=false,HostVerified=false,PlayerVerified=false;
static const char* Error="notInstalled";
static std::wstring Claim;
static BYTE* Image=nullptr;
static DWORD OriginalHostProtection=0,OriginalPlayerProtection=0;
static bool OriginalProtectionsKnown=false;
static constexpr unsigned HostCallRva=0x58486a,PlayerCallRva=0x3e55f0;
static const BYTE HostOriginal[5]={0xe8,0xc1,0xfc,0xff,0xff};
static const BYTE PlayerOriginal[5]={0xe8,0x3b,0xbb,0x19,0x00};
static BYTE HostReplacement[5]{},PlayerReplacement[5]{};

struct FStringView {const wchar_t* data;int length;int capacity;};
using Assign=void*(__thiscall*)(void*,const FStringView*);
using HostGetter=void*(__thiscall*)(void*,void*,unsigned,unsigned);
using PlayerGetter=void*(__thiscall*)(void*,void*,unsigned);
static Assign NativeAssign=nullptr;
static HostGetter NativeHostGetter=nullptr;
static PlayerGetter NativePlayerGetter=nullptr;

static void Apply(void* destination){
    const FStringView value{Claim.c_str(),int(Claim.size()+1),int(Claim.size()+1)};
    NativeAssign(destination,&value);
}
static void* __fastcall HostName(void* owner,void*,void* destination,unsigned playerIndex,unsigned mode){
    void* result=NativeHostGetter(owner,destination,playerIndex,mode);
    // Keep the original routine's ownership/return convention. Assignment uses
    // the engine allocator, and does not alter UserInfo or its stable alias.
    Apply(destination);return result;
}
static void* __fastcall PlayerName(void* owner,void*,void* destination,unsigned playerIndex){
    void* result=NativePlayerGetter(owner,destination,playerIndex);
    Apply(destination);return result;
}

// Return 0 for an absent optional file, 1 for a valid authoritative claim,
// -1 for any explicitly present but malformed/unreadable file. Never log text.
static int ReadClaim(const std::wstring& output){
    const std::wstring path=output+L"\\claimed-name.txt";
    DWORD attributes=GetFileAttributesW(path.c_str());
    if(attributes==INVALID_FILE_ATTRIBUTES){
        const DWORD code=GetLastError();
        if(code==ERROR_FILE_NOT_FOUND||code==ERROR_PATH_NOT_FOUND)return 0;
        Error="claimFileUnavailable";return -1;
    }
    if(attributes&FILE_ATTRIBUTE_DIRECTORY){Error="claimFileInvalid";return -1;}
    FILE* file=nullptr;
    if(_wfopen_s(&file,path.c_str(),L"rb")||!file){Error="claimFileUnavailable";return -1;}
    char bytes[19]{};size_t count=fread(bytes,1,sizeof(bytes),file);
    bool bad=ferror(file)!=0;fclose(file);
    if(bad||count==sizeof(bytes)){Error="claimFileInvalid";return -1;}
    if(count&&bytes[count-1]=='\n'){--count;if(count&&bytes[count-1]=='\r')--count;}
    if(count<3||count>16){Error="claimFileInvalid";return -1;}
    auto alnum=[](unsigned char c){return (c>='a'&&c<='z')||(c>='A'&&c<='Z')||(c>='0'&&c<='9');};
    if(!alnum(static_cast<unsigned char>(bytes[0]))){Error="claimFileInvalid";return -1;}
    for(size_t i=0;i<count;++i){
        unsigned char c=static_cast<unsigned char>(bytes[i]);
        if(!alnum(c)&&c!='_'&&c!='.'&&c!='-'){Error="claimFileInvalid";return -1;}
    }
    Claim.assign(bytes,bytes+count);return 1;
}

static void MakeCall(BYTE* output,BYTE* site,const void* function){
    output[0]=0xe8;
    LONG distance=LONG(reinterpret_cast<ULONG_PTR>(function)-reinterpret_cast<ULONG_PTR>(site)-5);
    memcpy(output+1,&distance,4);
}
static bool Identity(BYTE* base){
    const auto dos=reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    if(dos->e_magic!=IMAGE_DOS_SIGNATURE||dos->e_lfanew<=0||dos->e_lfanew>0x1000)return false;
    const auto pe=reinterpret_cast<const IMAGE_NT_HEADERS32*>(base+dos->e_lfanew);
    return pe->Signature==IMAGE_NT_SIGNATURE&&pe->FileHeader.Machine==IMAGE_FILE_MACHINE_I386&&
        pe->FileHeader.TimeDateStamp==0x47106421&&pe->OptionalHeader.Magic==IMAGE_NT_OPTIONAL_HDR32_MAGIC&&
        pe->OptionalHeader.ImageBase==0x10900000&&pe->OptionalHeader.SizeOfImage==0x1f56000;
}
static bool WritePair(const BYTE* host,const BYTE* player){
    DWORD hostProtection=0,playerProtection=0,ignored=0;
    if(!VirtualProtect(Image+HostCallRva,5,PAGE_EXECUTE_READWRITE,&hostProtection))return false;
    if(!VirtualProtect(Image+PlayerCallRva,5,PAGE_EXECUTE_READWRITE,&playerProtection)){
        VirtualProtect(Image+HostCallRva,5,hostProtection,&ignored);return false;
    }
    if(!OriginalProtectionsKnown){
        OriginalHostProtection=hostProtection;OriginalPlayerProtection=playerProtection;
        OriginalProtectionsKnown=true;
    }
    memcpy(Image+HostCallRva,host,5);memcpy(Image+PlayerCallRva,player,5);
    const bool flushedHost=FlushInstructionCache(GetCurrentProcess(),Image+HostCallRva,5)!=FALSE;
    const bool flushedPlayer=FlushInstructionCache(GetCurrentProcess(),Image+PlayerCallRva,5)!=FALSE;
    const bool restoredHost=VirtualProtect(Image+HostCallRva,5,OriginalHostProtection,&ignored)!=FALSE;
    const bool restoredPlayer=VirtualProtect(Image+PlayerCallRva,5,OriginalPlayerProtection,&ignored)!=FALSE;
    return flushedHost&&flushedPlayer&&restoredHost&&restoredPlayer;
}
static bool Rollback(){
    if(!Installed)return true;
    // Never overwrite a subsequent, unknown modification at either call site.
    if(memcmp(Image+HostCallRva,HostReplacement,5)||memcmp(Image+PlayerCallRva,PlayerReplacement,5)){
        Error="rollbackSignatureMismatch";Verified=false;return false;
    }
    bool success=WritePair(HostOriginal,PlayerOriginal);
    success=success&&!memcmp(Image+HostCallRva,HostOriginal,5)&&!memcmp(Image+PlayerCallRva,PlayerOriginal,5);
    if(!success){Error="rollbackFailed";Verified=false;return false;}
    Installed=false;Verified=false;HostVerified=false;PlayerVerified=false;Error="rolledBack";return true;
}
static bool Install(BYTE* base,const std::wstring& output){
    if(Installed){Error="alreadyInstalled";return false;}
    const int present=ReadClaim(output);
    if(present<0)return false;
    if(!present){Error="claimFileAbsent";return true;}
    if(!base||!Identity(base)){Error="clientIdentityMismatch";return false;}
    const BYTE assignPrefix[]{0x55,0x8b,0x6c,0x24,0x08,0x56,0x8b,0xf1};
    const BYTE hostPrefix[]{0x51,0x8b,0x44,0x24,0x10,0x53,0x33,0xdb};
    const BYTE playerPrefix[]{0x51,0x8b,0x44,0x24,0x0c,0x8b,0x49,0x04};
    if(memcmp(base+HostCallRva,HostOriginal,5)||memcmp(base+PlayerCallRva,PlayerOriginal,5)||
        memcmp(base+0x20540,assignPrefix,sizeof(assignPrefix))||
        memcmp(base+0x584530,hostPrefix,sizeof(hostPrefix))||
        memcmp(base+0x581130,playerPrefix,sizeof(playerPrefix))){Error="nativeSignatureMismatch";return false;}
    Image=base;
    NativeAssign=reinterpret_cast<Assign>(base+0x20540);
    NativeHostGetter=reinterpret_cast<HostGetter>(base+0x584530);
    NativePlayerGetter=reinterpret_cast<PlayerGetter>(base+0x581130);
    MakeCall(HostReplacement,base+HostCallRva,reinterpret_cast<const void*>(HostName));
    MakeCall(PlayerReplacement,base+PlayerCallRva,reinterpret_cast<const void*>(PlayerName));
    bool changed=WritePair(HostReplacement,PlayerReplacement);
    HostVerified=!memcmp(base+HostCallRva,HostReplacement,5);
    PlayerVerified=!memcmp(base+PlayerCallRva,PlayerReplacement,5);
    Installed=HostVerified&&PlayerVerified;
    Verified=changed&&Installed;
    if(!Verified){
        // WritePair writes both or neither before verification. Restore both
        // originals on a protection/cache failure after the write occurred.
        if(Installed){if(Rollback())Error="installationFailedRolledBack";}
        else Error="installationFailed";
        return false;
    }
    Error="none";return true;
}
} // namespace ClaimedName
