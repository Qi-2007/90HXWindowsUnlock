$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$out=Join-Path $root 'build\device-presence-tests'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe=Join-Path $out 'DevicePresenceMonitorTests.exe'
& $compiler /nologo /warn:4 /warnaserror /target:exe "/out:$exe" (Join-Path $root 'backend\DevicePresenceMonitor.cs') (Join-Path $PSScriptRoot 'DevicePresenceMonitorTests.cs')
if($LASTEXITCODE -ne 0){throw 'Device presence test compilation failed.'}
& $exe
if($LASTEXITCODE -ne 0){throw 'Device presence regression tests failed.'}
