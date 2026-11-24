[CmdletBinding()]
param([string]$OutputDirectory='local/companion/bin', [switch]$Dedicated, [switch]$Vegas2)
$ErrorActionPreference='Stop'
$Repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$OutputDirectory=[IO.Path]::GetFullPath((Join-Path $Repo $OutputDirectory))
if(!$OutputDirectory.StartsWith((Join-Path $Repo 'local/'),[StringComparison]::OrdinalIgnoreCase)){throw 'Companion binaries must stay in the local working copy'}
New-Item -ItemType Directory -Force $OutputDirectory|Out-Null
. (Join-Path $PSScriptRoot 'Compiler.ps1')
$toolchain=Get-VegasToolchain
$Compiler=$toolchain.Compiler
$Sdk=$toolchain.Sdk
$SdkVersion=$toolchain.SdkVersion
$Cl=Join-Path $Compiler 'bin/Hostx64/x86/cl.exe'
$MinHook=Join-Path $Repo 'local/dependencies/minhook'
if(!(Test-Path -LiteralPath (Join-Path $MinHook 'include/MinHook.h'))){
    & git clone --depth 1 --branch v1.3.4 https://github.com/TsudaKageyu/minhook.git $MinHook
    if($LASTEXITCODE -ne 0){throw 'Cannot retrieve the pinned hook library'}
}
if((& git -C $MinHook rev-parse HEAD).Trim() -ne 'c3fcafdc10146beb5919319d0683e44e3c30d537'){throw 'MinHook source identity differs'}
$Arguments=@('/nologo','/std:c++17','/EHsc','/MT','/O2','/W4','/LD',('/I'+(Join-Path $Compiler 'include')),('/I'+(Join-Path $Sdk "Include/$SdkVersion/ucrt")),('/I'+(Join-Path $Sdk "Include/$SdkVersion/shared")),('/I'+(Join-Path $Sdk "Include/$SdkVersion/um")),('/I'+(Join-Path $MinHook 'include')),(Join-Path $PSScriptRoot 'compat.cpp'))
if($Dedicated){$Arguments=$Arguments | ForEach-Object {$_ -replace 'compat.cpp$','dedicated_compat.cpp'}}
if($Vegas2){$Arguments=$Arguments | ForEach-Object {$_ -replace 'compat.cpp$','compat_vegas2.cpp'}}
$Arguments+=@('src/buffer.c','src/hook.c','src/trampoline.c','src/hde/hde32.c')|ForEach-Object{Join-Path $MinHook $_}
$Arguments+=@(('/Fo'+$OutputDirectory+'/'),'/link',('/DEF:'+(Join-Path $PSScriptRoot 'compat.def')),('/OUT:'+(Join-Path $OutputDirectory 'VegasCompanion.dll')),('/IMPLIB:'+(Join-Path $OutputDirectory 'VegasCompanion.lib')),('/LIBPATH:'+(Join-Path $Compiler 'lib/x86')),('/LIBPATH:'+(Join-Path $Sdk "Lib/$SdkVersion/ucrt/x86")),('/LIBPATH:'+(Join-Path $Sdk "Lib/$SdkVersion/um/x86")),'user32.lib','kernel32.lib','shell32.lib','ws2_32.lib','winhttp.lib')
if($Dedicated){$Arguments=$Arguments | ForEach-Object {$_ -replace 'VegasCompanion','VegasDedicated'}}
if($Vegas2){$Arguments=$Arguments | ForEach-Object {$_ -replace 'VegasCompanion\.','VegasCompanionVegas2.'}}
& $Cl @Arguments
if($LASTEXITCODE -ne 0){throw 'Retail companion adapter build failed'}
if($Vegas2){Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'VegasCompanionVegas2.dll') -Algorithm SHA256;return}
if($Dedicated){Copy-Item -LiteralPath (Join-Path $OutputDirectory 'VegasDedicated.dll') -Destination (Join-Path $OutputDirectory 'VegasDedicated-4.dll') -Force;Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'VegasDedicated.dll') -Algorithm SHA256;return}
Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'VegasCompanion.dll') -Algorithm SHA256
Copy-Item -LiteralPath (Join-Path $OutputDirectory 'VegasCompanion.dll') -Destination (Join-Path $OutputDirectory 'VegasCompanion-14.dll') -Force
$Injector=@('/nologo','/std:c++17','/EHsc','/MT','/O2',('/I'+(Join-Path $Compiler 'include')),('/I'+(Join-Path $Sdk "Include/$SdkVersion/ucrt")),('/I'+(Join-Path $Sdk "Include/$SdkVersion/shared")),('/I'+(Join-Path $Sdk "Include/$SdkVersion/um")),(Join-Path $PSScriptRoot 'injector.cpp'),('/Fo'+(Join-Path $OutputDirectory 'retail_injector.obj')),'/link',('/OUT:'+(Join-Path $OutputDirectory 'VegasInjector.exe')),('/LIBPATH:'+(Join-Path $Compiler 'lib/x86')),('/LIBPATH:'+(Join-Path $Sdk "Lib/$SdkVersion/ucrt/x86")),('/LIBPATH:'+(Join-Path $Sdk "Lib/$SdkVersion/um/x86")),'kernel32.lib')
& $Cl @Injector
if($LASTEXITCODE -ne 0){throw 'Retail injector build failed'}
$Cl64=Join-Path $Compiler 'bin/Hostx64/x64/cl.exe'
$Gate=@('/nologo','/std:c++17','/EHsc','/MT','/O2','/LD',('/I'+(Join-Path $Compiler 'include')),('/I'+(Join-Path $Sdk "Include/$SdkVersion/ucrt")),('/I'+(Join-Path $Sdk "Include/$SdkVersion/shared")),('/I'+(Join-Path $Sdk "Include/$SdkVersion/um")),('/I'+(Join-Path $MinHook 'include')),(Join-Path $PSScriptRoot 'steam_gate.cpp'))
$Gate+=@('src/buffer.c','src/hook.c','src/trampoline.c','src/hde/hde64.c')|ForEach-Object{Join-Path $MinHook $_}
$Gate+=@(('/Fo'+$OutputDirectory+'/'),'/link',('/DEF:'+(Join-Path $PSScriptRoot 'steam_gate.def')),('/OUT:'+(Join-Path $OutputDirectory 'VegasSteamGate.dll')),('/IMPLIB:'+(Join-Path $OutputDirectory 'VegasSteamGate.lib')),('/LIBPATH:'+(Join-Path $Compiler 'lib/x64')),('/LIBPATH:'+(Join-Path $Sdk "Lib/$SdkVersion/ucrt/x64")),('/LIBPATH:'+(Join-Path $Sdk "Lib/$SdkVersion/um/x64")),'kernel32.lib','user32.lib')
& $Cl64 @Gate
if($LASTEXITCODE -ne 0){throw 'Retail Steam launch gate build failed'}
$Control=$Injector|ForEach-Object{$_ -replace '([/\\])x86','$1x64' -replace 'VegasInjector.exe','VegasSteamControl.exe' -replace 'retail_injector.obj','retail_gate_control.obj'}
& $Cl64 @Control
if($LASTEXITCODE -ne 0){throw 'Retail launch gate control build failed'}
$Sources=@{}
foreach($Name in @('compat.cpp','companion_core.h','compat_vegas2.cpp','dedicated_compat.cpp','display_style.h','claimed_name.h','native_claimed_session_name.h','native_readiness.h','launch_options.h','native_rank.h','native_report_xp.h','native_ranked_access.h','native_ranked_host.h','native_host_statistics.h','native_solo_hunt.h','native_procedural_animation.h','native_procedural_animation_vegas2.h','native_login_vegas2.h','native_claimed_name_vegas2.h','native_solo_match_vegas2.h','native_xp_modifier_vegas2.h','native_skip_intros_vegas2.h','native_display_vegas2.h','native_punkbuster_vegas2.h','native_punkbuster.h','native_awards.h','native_gamepad.h','procedural_clock.h','native_unlock_policy.h','native_pec_defaults.h','compat.def','injector.cpp','steam_gate.cpp','steam_gate.def','Build-Companion.ps1','Compiler.ps1')){
    $Sources[$Name]=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $Name) -Algorithm SHA256).Hash.ToLowerInvariant()
}
@{kind='retail_companion_build';sources=$Sources;minhookCommit='c3fcafdc10146beb5919319d0683e44e3c30d537';adapterSha256=(Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'VegasCompanion.dll') -Algorithm SHA256).Hash.ToLowerInvariant()}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $OutputDirectory 'build.json')
