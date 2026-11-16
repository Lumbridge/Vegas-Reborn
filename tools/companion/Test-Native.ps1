[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $PSScriptRoot 'Compiler.ps1')
$toolchain=Get-VegasToolchain
$vc=$toolchain.Compiler;$sdk=$toolchain.Sdk;$version=$toolchain.SdkVersion
$output=Join-Path $repo 'local/native-tests'
New-Item -ItemType Directory -Path $output -Force | Out-Null
foreach($test in @('display_style_test','claimed_name_test')){
    $arguments=@('/nologo','/std:c++17','/EHsc','/MT','/O2',('/I'+$vc+'/include'),('/I'+$sdk+'/Include/'+$version+'/ucrt'),('/I'+$sdk+'/Include/'+$version+'/shared'),('/I'+$sdk+'/Include/'+$version+'/um'),(Join-Path $repo ('tests/native/'+$test+'.cpp')),('/Fo'+$output+'/'+$test+'.obj'),'/link',('/OUT:'+$output+'/'+$test+'.exe'),('/LIBPATH:'+$vc+'/lib/x86'),('/LIBPATH:'+$sdk+'/Lib/'+$version+'/ucrt/x86'),('/LIBPATH:'+$sdk+'/Lib/'+$version+'/um/x86'),'kernel32.lib')
    & (Join-Path $vc 'bin/Hostx64/x86/cl.exe') @arguments
    if($LASTEXITCODE){throw "Native test compilation failed: $test"}
    & (Join-Path $output ($test+'.exe')) $output
    if($LASTEXITCODE){throw "Native test failed: $test"}
}
