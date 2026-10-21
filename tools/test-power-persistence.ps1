$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$out=Join-Path $root 'build\power-persistence-tests'
New-Item -ItemType Directory -Path $out -Force | Out-Null
# Compile the production policy and engine against a fake GPU. This excludes
# legacy monitor, PnP and NVAPI entry points from the regression executable.
$source=[IO.File]::ReadAllText((Join-Path $root 'ui\PowerControl.cs'))
$start=$source.IndexOf('    internal sealed class IdlePowerPolicy')
$end=$source.IndexOf('    internal sealed class PowerCoordination',$start)
if($start -lt 0 -or $end -le $start){throw 'Cannot locate production power engine.'}
$engine=Join-Path $out 'PowerEngine.cs'
[IO.File]::WriteAllText($engine,('using System; namespace CMP90HX.Control {'+$source.Substring($start,$end-$start)+'}'),[Text.UTF8Encoding]::new($false))
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe=Join-Path $out 'PowerPersistenceTests.exe'
& $compiler /nologo /warn:4 /warnaserror /target:exe "/out:$exe" $engine (Join-Path $root 'shared\PowerDtos.cs') (Join-Path $PSScriptRoot 'PowerPersistenceTests.cs')
if($LASTEXITCODE -ne 0){throw 'Power persistence test compilation failed.'}
& $exe
if($LASTEXITCODE -ne 0){throw 'Power persistence regression tests failed.'}
