[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out=Join-Path $root 'build'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$exe=Join-Path $out 'StartupSettingsTests.exe'
& $compiler /nologo /target:exe /r:System.Web.Extensions.dll "/out:$exe" (Join-Path $root 'ui\StartupSettings.cs') (Join-Path $PSScriptRoot 'StartupSettingsTests.cs')
if($LASTEXITCODE -ne 0) {throw 'Startup settings tests compilation failed.'}
& $exe
if($LASTEXITCODE -ne 0) {throw 'Startup settings tests failed.'}
