[CmdletBinding()]
param(
    [string]$DriverPath,
    [switch]$SkipGuiBuild,
    [switch]$SkipWorkerBuild,
    [switch]$RequireSignedWorker,
    [switch]$NonInteractive
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'tools\BuildCommon.ps1')
# Sign the canonical driver in place; a custom input can be selected with -DriverPath.
if (!$DriverPath) {
    $DriverPath=Join-Path $PSScriptRoot 'build\dma-driver\CMP90HXDma.sys'
}
$DriverPath=[IO.Path]::GetFullPath($DriverPath)
if (!(Test-Path -LiteralPath $DriverPath -PathType Leaf)) { throw "Signed driver is missing: $DriverPath" }
$signature=Get-AuthenticodeSignature -LiteralPath $DriverPath
if ($signature.Status -ne 'Valid') { throw "DMA signature verification failed: $($signature.Status). Sign the driver before updating hashes." }
$dmaHash=(Get-FileHash -LiteralPath $DriverPath -Algorithm SHA256).Hash

# Never bless an untested worker by simply replacing its validation stamp.
if (!$SkipWorkerBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -IfNeeded | Out-Host }
$worker=Join-Path $PSScriptRoot 'build\CMP90HXUnlocker.exe'
$gen2Hash=Assert-Gen2Validated
Assert-DriverCallerPolicy $DriverPath $gen2Hash
if ($RequireSignedWorker -and (Get-AuthenticodeSignature -LiteralPath $worker).Status -ne 'Valid') { throw 'Gen2 must have a valid Authenticode signature. Run build-gen2.ps1 first.' }

$config=Join-Path $PSScriptRoot 'ui\NativeWorkflow.cs'
$configBytes=[IO.File]::ReadAllBytes($config)
$source=[IO.File]::ReadAllText($config)
foreach ($entry in @(@{Name='Gen2Hash';Hash=$gen2Hash},@{Name='DmaHash';Hash=$dmaHash})) {
    $pattern='(?m)(internal const string '+$entry.Name+'\s*=\s*")[0-9A-Fa-f]*("\s*;)'
    if ([regex]::Matches($source,$pattern).Count -ne 1) { throw "Expected exactly one $($entry.Name) setting in $config" }
    $source=[regex]::Replace($source,$pattern,('${1}'+$entry.Hash+'${2}'))
}
if ((Get-FileHash -LiteralPath $DriverPath -Algorithm SHA256).Hash -ne $dmaHash -or
    (Get-FileHash -LiteralPath $worker -Algorithm SHA256).Hash -ne $gen2Hash) { throw 'Release inputs changed during validation; retry.' }

# GUI and automatic-task deployment use this canonical file name.
$canonical=Join-Path $PSScriptRoot 'build\dma-driver\CMP90HXDma.sys'
if (![String]::Equals($DriverPath,$canonical,[StringComparison]::OrdinalIgnoreCase)) {
    Copy-Item -LiteralPath $DriverPath -Destination $canonical -Force
}
if ((Get-FileHash -LiteralPath $canonical -Algorithm SHA256).Hash -ne $dmaHash) { throw 'Signed driver copy differs from the validated input.' }
if ($source -ne [IO.File]::ReadAllText($config)) {
    $bom=$configBytes.Length -ge 3 -and $configBytes[0] -eq 0xef -and $configBytes[1] -eq 0xbb -and $configBytes[2] -eq 0xbf
    [IO.File]::WriteAllText($config,$source,(New-Object Text.UTF8Encoding($bom)))
}
Write-Host "CMP90HXUnlocker SHA256: $gen2Hash"
Write-Host "CMP90HXDma SHA256:  $dmaHash"
if (!$SkipGuiBuild) { & (Join-Path $PSScriptRoot 'build-gui.ps1') -NonInteractive:$NonInteractive | Out-Host }
[pscustomobject]@{Gen2Hash=$gen2Hash;DmaHash=$dmaHash;DriverPath=$canonical}
