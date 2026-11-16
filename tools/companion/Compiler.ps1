function Get-VegasToolchain {
    $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if(!(Test-Path -LiteralPath $vswhere)){throw 'Install Visual Studio C++ build tools and Windows SDK'}
    $installation=(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1)
    if(!$installation){throw 'Visual Studio C++ tools were not found'}
    $toolsVersion=([IO.File]::ReadAllText((Join-Path $installation 'VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt'))).Trim()
    $compiler=Join-Path $installation ('VC/Tools/MSVC/'+$toolsVersion)
    $sdk=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
    $sdkVersion=Get-ChildItem -LiteralPath (Join-Path $sdk 'Include') -Directory |
        Where-Object {Test-Path -LiteralPath (Join-Path $_.FullName 'um/Windows.h')} |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty Name
    if(!$sdkVersion){throw 'Windows SDK headers were not found'}
    return @{Compiler=$compiler;Sdk=$sdk;SdkVersion=$sdkVersion}
}
