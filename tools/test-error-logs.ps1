$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$out=Join-Path $root 'build\error-log-tests'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe=Join-Path $out 'ErrorLogTests.exe'
$sources=@('shared\AppData.cs','shared\ErrorLog.cs','ui\SharedSynchronization.cs','ui\LogSession.cs','tools\ErrorLogTests.cs') | ForEach-Object {Join-Path $root $_}
& $compiler /nologo /warn:4 /warnaserror /target:exe "/out:$exe" $sources
if($LASTEXITCODE -ne 0){throw 'Error log test compilation failed.'}
& $exe
if($LASTEXITCODE -ne 0){throw 'Error log regression tests failed.'}
