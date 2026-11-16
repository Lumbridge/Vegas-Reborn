#define WIN32_LEAN_AND_MEAN
#include "../../tools/companion/claimed_name.h"
#include <cassert>
#include <initializer_list>
static void write(const std::wstring& path,const std::string& value){FILE*f=nullptr;assert(!_wfopen_s(&f,path.c_str(),L"wb"));assert(f);fwrite(value.data(),1,value.size(),f);fclose(f);}
int wmain(int argc,wchar_t** argv){
 assert(argc==2);const std::wstring root=argv[1];const std::wstring file=root+L"\\claimed-name.txt";
 DeleteFileW(file.c_str());assert(ClaimedName::Install(nullptr,root)&&!ClaimedName::Installed);
 for(const std::string& s:{"A_b","Player.Test-1234","Abc\r\n"}){write(file,s);assert(ClaimedName::ReadClaim(root)==1);}
 for(const std::string& s:{"ab","abcdefghijklmnopq",".Abc","A?bc","A&bc","A bc","Abc\n\n","Abc/Def","Abc%20","Abc\r"}){write(file,s);assert(ClaimedName::ReadClaim(root)==-1);}
 write(file,"Test_Player");assert(!ClaimedName::Install(nullptr,root));
 BYTE* image=static_cast<BYTE*>(VirtualAlloc(nullptr,0x1f56000,MEM_RESERVE|MEM_COMMIT,PAGE_EXECUTE_READWRITE));assert(image);
 auto dos=reinterpret_cast<IMAGE_DOS_HEADER*>(image);dos->e_magic=IMAGE_DOS_SIGNATURE;dos->e_lfanew=0x100;
 auto pe=reinterpret_cast<IMAGE_NT_HEADERS32*>(image+0x100);pe->Signature=IMAGE_NT_SIGNATURE;pe->FileHeader.Machine=IMAGE_FILE_MACHINE_I386;pe->FileHeader.TimeDateStamp=0x47106421;pe->OptionalHeader.Magic=IMAGE_NT_OPTIONAL_HDR32_MAGIC;pe->OptionalHeader.ImageBase=0x10900000;pe->OptionalHeader.SizeOfImage=0x1f56000;
 assert(!ClaimedName::Install(image,root));assert(!ClaimedName::Installed);
 const BYTE ap[]={0x55,0x8b,0x6c,0x24,0x08,0x56,0x8b,0xf1},hp[]={0x51,0x8b,0x44,0x24,0x10,0x53,0x33,0xdb},pp[]={0x51,0x8b,0x44,0x24,0x0c,0x8b,0x49,0x04};
 memcpy(image+0x20540,ap,sizeof(ap));memcpy(image+0x584530,hp,sizeof(hp));memcpy(image+0x581130,pp,sizeof(pp));memcpy(image+ClaimedName::HostCallRva,ClaimedName::HostOriginal,5);memcpy(image+ClaimedName::PlayerCallRva,ClaimedName::PlayerOriginal,5);
 DWORD ignored;assert(VirtualProtect(image,0x1f56000,PAGE_EXECUTE_READ,&ignored));
 assert(ClaimedName::Install(image,root));assert(ClaimedName::Installed&&ClaimedName::Verified&&ClaimedName::HostVerified&&ClaimedName::PlayerVerified);
 MEMORY_BASIC_INFORMATION info{};assert(VirtualQuery(image+ClaimedName::HostCallRva,&info,sizeof(info)));assert(info.Protect==PAGE_EXECUTE_READ);
 assert(!ClaimedName::Install(image,root));assert(ClaimedName::Rollback());assert(!memcmp(image+ClaimedName::HostCallRva,ClaimedName::HostOriginal,5));assert(!memcmp(image+ClaimedName::PlayerCallRva,ClaimedName::PlayerOriginal,5));
 assert(VirtualQuery(image+ClaimedName::PlayerCallRva,&info,sizeof(info)));assert(info.Protect==PAGE_EXECUTE_READ);
 assert(VirtualFree(image,0,MEM_RELEASE));DeleteFileW(file.c_str());puts("PASS: native claim validation, signature denial, two-call installation/readback and rollback/protection restoration; synthetic mapped-image fixture only.");return 0;
}
