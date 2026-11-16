[CmdletBinding()]
param(
    [string]$RealmDirectory = 'local/realms/realm-01',
    [Parameter(Mandatory)][string]$NativeLicenseFile,
    [switch]$DesktopShortcut
)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$destination=Join-Path $repo 'local/companion'
$realm=[IO.Path]::GetFullPath((Join-Path $repo $RealmDirectory))
$license=[IO.Path]::GetFullPath((Join-Path $repo $NativeLicenseFile))
$public=Join-Path $realm 'root-public.der'
if(!(Test-Path -LiteralPath $public)){throw 'Prepared public realm key is missing'}
$licenseText=[IO.File]::ReadAllText($license)
$match=[regex]::Match($licenseText.Replace("`r",''),'(?m)^KEYID=([0-9a-f]{48})$')
if(!$match.Success){throw 'Expected a native-generated local-realm license configuration'}
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Copy-Item -LiteralPath $public -Destination (Join-Path $destination 'root-public.der')
@{version=1;host='vegas.lumbridge.co.uk';port=3074;rootPublicSha256=(Get-FileHash -LiteralPath $public -Algorithm SHA256).Hash.ToLowerInvariant();nativeLicenseId=$match.Groups[1].Value} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'service.json') -Encoding utf8NoBOM
& (Join-Path $PSScriptRoot 'Build-Companion.ps1')
if($LASTEXITCODE -ne 0){throw 'Companion build failed'}
& (Join-Path $PSScriptRoot 'Build-Companion.ps1') -Dedicated
if($LASTEXITCODE -ne 0){throw 'Dedicated companion build failed'}
$python=(Get-Command python.exe).Source
$pythonw=Join-Path (Split-Path $python) 'pythonw.exe'
if(!(Test-Path -LiteralPath $pythonw)){throw 'Python with Tkinter/pythonw is required'}
$script=Join-Path $repo 'tools/vegas_companion.py'
$shell=New-Object -ComObject WScript.Shell
$shortcutPath=Join-Path $destination 'Vegas Reborn.lnk'
if($DesktopShortcut){$shortcutPath=Join-Path ([Environment]::GetFolderPath('Desktop')) 'Vegas Reborn.lnk'}
$shortcut=$shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath=$pythonw
$shortcut.Arguments='"'+$script+'"'
$shortcut.WorkingDirectory=$repo
$shortcut.Description='Vegas Reborn Companion â€” play, host and shared progression'
$shortcut.Save()
Write-Output "Companion ready: $shortcutPath"
