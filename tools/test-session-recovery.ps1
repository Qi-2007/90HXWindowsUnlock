$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$out=Join-Path $root 'build\session-recovery-tests'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe=Join-Path $out 'SessionRecoveryFailureTests.exe'
& $csc /nologo /warn:4 /warnaserror /target:exe "/out:$exe" (Join-Path $root 'shared\SessionRecoveryFailure.cs') (Join-Path $PSScriptRoot 'SessionRecoveryFailureTests.cs')
if($LASTEXITCODE -ne 0){throw 'Session recovery test compilation failed.'}
& $exe
if($LASTEXITCODE -ne 0){throw 'Session recovery regression tests failed.'}
