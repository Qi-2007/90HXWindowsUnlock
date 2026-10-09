[CmdletBinding()]
param([switch]$SkipGuiBuild,[switch]$SkipWorkerBuild,[switch]$RequireSignedWorker,[switch]$NonInteractive)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'tools\prepare-backend-packages.ps1') -PolicyOnly -RequireSigned
if($RequireSignedWorker){foreach($kind in @('native','session')){if((Get-AuthenticodeSignature -LiteralPath (Join-Path $PSScriptRoot "backends\$kind\CMP90HXUnlocker.exe")).Status -ne 'Valid'){throw "Unsigned worker: $kind"}}}
if(!$SkipGuiBuild){& (Join-Path $PSScriptRoot 'build-gui.ps1') -NonInteractive:$NonInteractive}
Write-Host 'Hashes come only from Console/backends. SkipWorkerBuild is retained for compatibility; workers are never built here.'
