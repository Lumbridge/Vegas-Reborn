[CmdletBinding()]
param([string]$OutputDirectory='local/claimed-name-native-tests')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output=[IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
$local=[IO.Path]::GetFullPath((Join-Path $repo 'local'))+[IO.Path]::DirectorySeparatorChar
if(!$output.StartsWith($local,[StringComparison]::OrdinalIgnoreCase)){throw 'Test output must stay under local'}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$vc='C:/Program Files/Microsoft Visual Studio/18/Insiders/VC/Tools/MSVC/14.44.35207'
$sdk='C:/Program Files (x86)/Windows Kits/10';$version='10.0.26100.0'
$arguments=@('/nologo','/std:c++17','/EHsc','/MT','/O2',('/I'+$vc+'/include'),('/I'+$sdk+'/Include/'+$version+'/ucrt'),('/I'+$sdk+'/Include/'+$version+'/shared'),('/I'+$sdk+'/Include/'+$version+'/um'),(Join-Path $repo 'tests/native/claimed_name_test.cpp'),('/Fo'+$output+'/claimed_name_test.obj'),'/link',('/OUT:'+$output+'/claimed_name_test.exe'),('/LIBPATH:'+$vc+'/lib/x86'),('/LIBPATH:'+$sdk+'/Lib/'+$version+'/ucrt/x86'),('/LIBPATH:'+$sdk+'/Lib/'+$version+'/um/x86'),'kernel32.lib')
& (Join-Path $vc 'bin/Hostx64/x86/cl.exe') @arguments
if($LASTEXITCODE -ne 0){throw 'Native claim test compilation failed'}
& (Join-Path $output 'claimed_name_test.exe') $output
if($LASTEXITCODE -ne 0){throw 'Native claim test failed'}
