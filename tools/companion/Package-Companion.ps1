[CmdletBinding()]
param([string]$NativeDirectory='local/companion/bin')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$python=Join-Path $repo '.venv/Scripts/python.exe'
if(!(Test-Path -LiteralPath $python)){throw 'Create .venv and install requirements-build.txt first'}
& $python (Join-Path $repo 'tools/build_release.py') --native-directory (Join-Path $repo $NativeDirectory)
if($LASTEXITCODE){throw 'Companion release build failed'}
