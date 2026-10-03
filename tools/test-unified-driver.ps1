[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
if (!(Get-Command cl.exe -ErrorAction SilentlyContinue)) { throw 'Run in a Visual Studio x64 developer PowerShell.' }
$root=Split-Path -Parent $PSScriptRoot
$out=Join-Path $root 'build'
New-Item -ItemType Directory -Path $out -Force | Out-Null
foreach ($name in @('state_test','hardware_test')) {
    $source=Join-Path $root "driver\tests\$name.c"
    $exe=Join-Path $out "$name.exe"
    $obj=Join-Path $out "$name.obj"
    & cl.exe /nologo /std:c17 /W4 /WX /TC $source "/Fe:$exe" "/Fo:$obj"
    if ($LASTEXITCODE -ne 0) { throw "$name compilation failed" }
    & $exe
    if ($LASTEXITCODE -ne 0) { throw "$name failed" }
}
Write-Host 'Host-only unified driver tests passed; no driver load or hardware access.'
