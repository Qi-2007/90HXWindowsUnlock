[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$DriverPath,
    [switch]$UnsignedSmokeTest
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'tools\ConsoleEncoding.ps1')
$admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (!$admin) { throw 'Run this script in an elevated PowerShell.' }
$path=[IO.Path]::GetFullPath($DriverPath)
if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Driver file is missing.' }
if ($path -notmatch '^[A-Za-z]:\\' -or [IO.Path]::GetExtension($path) -ne '.sys') { throw 'Use a local .sys file.' }
$exe=Join-Path $PSScriptRoot 'build\CMP90HXUnlocker.exe'
if ($UnsignedSmokeTest -and !(Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Smoke test executable is missing. Run build.ps1 first.' }
# Never replace an existing service or alter signing / Secure Boot policy.
if (Get-Service -Name CMP90HXDma -ErrorAction SilentlyContinue) { throw 'CMP90HXDma service already exists; inspect it manually. It is not replaced.' }
Get-FileHash -Algorithm SHA256 $path
$signature=Get-AuthenticodeSignature -LiteralPath $path
if ($signature.Status -ne 'Valid') {
    if (!$UnsignedSmokeTest -or $signature.Status -ne 'NotSigned') {
        throw "Driver signature must validate on this test machine: $($signature.Status). Use -UnsignedSmokeTest only for an unsigned driver on a disposable test boot."
    }
    Write-Warning 'Unsigned smoke test: Windows must already permit unsigned kernel drivers for this boot. Loading can still fail or crash the machine.'
}
& sc.exe create CMP90HXDma type= kernel start= demand error= normal binPath= (('\??\')+$path)
if ($LASTEXITCODE -ne 0) { throw "CreateService failed: $LASTEXITCODE" }
& sc.exe start CMP90HXDma
if ($LASTEXITCODE -ne 0) { throw "StartService failed: $LASTEXITCODE. Service retained for inspection; signature validation does not guarantee kernel acceptance." }
if ($UnsignedSmokeTest) {
    & $exe arena-info --physical-dma-experiment
    if ($LASTEXITCODE -ne 0) { throw "Driver INFO test failed: $LASTEXITCODE. Service retained for inspection." }
    & $exe arena-reserve-test --physical-dma-experiment
    if ($LASTEXITCODE -ne 0) { throw "Driver reserve/map test failed: $LASTEXITCODE. Service retained for inspection." }
    Write-Host 'No-GPU smoke test passed: driver loaded, arena reserved and mapped. Physical readback and GPU DMA were not tested.'
}
Write-Host 'CMP90HXDma started. It cannot unload during this boot. DMA pages/quarantine last until reboot; no EFI bootstrap required for this backend.'
