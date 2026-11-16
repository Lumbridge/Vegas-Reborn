[CmdletBinding()]
param([Parameter(Mandatory)][string]$ConfigDirectory, [switch]$DesktopShortcut)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$destination=Join-Path $repo 'local/companion'
$publicConfig=[IO.Path]::GetFullPath($ConfigDirectory)
# These are the public launch configuration files from a published companion ZIP.
# No realm directory, private key, backend checkout or real retail key is needed.
foreach($name in @('service.json','root-public.der')){
    if(!(Test-Path -LiteralPath (Join-Path $publicConfig $name))){throw "Missing public companion configuration: $name"}
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach($name in @('service.json','root-public.der')){
    Copy-Item -LiteralPath (Join-Path $publicConfig $name) -Destination (Join-Path $destination $name) -Force
}
& (Join-Path $PSScriptRoot 'Build-Companion.ps1')
if(!$?){throw 'Companion build failed'}
& (Join-Path $PSScriptRoot 'Build-Companion.ps1') -Dedicated
if(!$?){throw 'Dedicated build failed'}
if($DesktopShortcut){
    $pythonw=Join-Path $repo '.venv/Scripts/pythonw.exe'
    if(!(Test-Path -LiteralPath $pythonw)){throw 'Create the companion Python build environment first'}
    $shortcutPath=Join-Path ([Environment]::GetFolderPath('Desktop')) 'Vegas Reborn (development).lnk'
    $shortcut=(New-Object -ComObject WScript.Shell).CreateShortcut($shortcutPath)
    $shortcut.TargetPath=$pythonw
    $shortcut.Arguments='"'+(Join-Path $repo 'tools/vegas_companion.py')+'"'
    $shortcut.WorkingDirectory=$repo
    $shortcut.Description='Vegas Reborn companion development checkout'
    $shortcut.Save()
}
Write-Output "Companion development configuration prepared: $destination"
