[CmdletBinding()]
param(
    [string]$SignedDriverPath,
    [switch]$SkipGuiBuild
)
$ErrorActionPreference='Stop'
# Prefer the actual build artifact, which may have been signed in place.
if (!$SignedDriverPath) {
    $SignedDriverPath=Join-Path $PSScriptRoot 'build\dma-driver\CMP90HXDma.sys'
    if (!(Test-Path -LiteralPath $SignedDriverPath -PathType Leaf)) {
        $SignedDriverPath=Join-Path $PSScriptRoot 'build\dma-driver\CMP90HXDmaSigned.sys'
    }
}
$SignedDriverPath=[IO.Path]::GetFullPath($SignedDriverPath)
if (!(Test-Path -LiteralPath $SignedDriverPath -PathType Leaf)) { throw "Signed driver is missing: $SignedDriverPath" }
$signature=Get-AuthenticodeSignature -LiteralPath $SignedDriverPath
if ($signature.Status -ne 'Valid') { throw "DMA signature verification failed: $($signature.Status). Sign the driver before updating hashes." }
$dmaHash=(Get-FileHash -LiteralPath $SignedDriverPath -Algorithm SHA256).Hash

# Never bless an untested worker by simply replacing its validation stamp.
& (Join-Path $PSScriptRoot 'build.ps1') -IfNeeded | Out-Host
$worker=Join-Path $PSScriptRoot 'build\CMP90HXGen2.exe'
$stamp=Join-Path $PSScriptRoot 'build\CMP90HXGen2.validated.sha256'
$gen2Hash=(Get-FileHash -LiteralPath $worker -Algorithm SHA256).Hash
if ((Get-Content -Raw -LiteralPath $stamp).Trim() -ne $gen2Hash) { throw 'Worker validation stamp does not match; run build.ps1 successfully first.' }

$config=Join-Path $PSScriptRoot 'ui\NativeWorkflow.cs'
$configBytes=[IO.File]::ReadAllBytes($config)
$source=[IO.File]::ReadAllText($config)
foreach ($entry in @(@{Name='Gen2Hash';Hash=$gen2Hash},@{Name='DmaHash';Hash=$dmaHash})) {
    $pattern='(?m)(internal const string '+$entry.Name+'\s*=\s*")[0-9A-Fa-f]*("\s*;)'
    if ([regex]::Matches($source,$pattern).Count -ne 1) { throw "Expected exactly one $($entry.Name) setting in $config" }
    $source=[regex]::Replace($source,$pattern,('${1}'+$entry.Hash+'${2}'))
}
if ((Get-FileHash -LiteralPath $SignedDriverPath -Algorithm SHA256).Hash -ne $dmaHash -or
    (Get-FileHash -LiteralPath $worker -Algorithm SHA256).Hash -ne $gen2Hash) { throw 'Release inputs changed during validation; retry.' }

# GUI and automatic-task deployment use this canonical file name.
$canonical=Join-Path $PSScriptRoot 'build\dma-driver\CMP90HXDmaSigned.sys'
if (![String]::Equals($SignedDriverPath,$canonical,[StringComparison]::OrdinalIgnoreCase)) {
    Copy-Item -LiteralPath $SignedDriverPath -Destination $canonical -Force
}
if ((Get-FileHash -LiteralPath $canonical -Algorithm SHA256).Hash -ne $dmaHash) { throw 'Signed driver copy differs from the validated input.' }
if ($source -ne [IO.File]::ReadAllText($config)) {
    $bom=$configBytes.Length -ge 3 -and $configBytes[0] -eq 0xef -and $configBytes[1] -eq 0xbb -and $configBytes[2] -eq 0xbf
    [IO.File]::WriteAllText($config,$source,(New-Object Text.UTF8Encoding($bom)))
}
Write-Host "CMP90HXGen2 SHA256: $gen2Hash"
Write-Host "CMP90HXDma SHA256:  $dmaHash"
if (!$SkipGuiBuild) { & (Join-Path $PSScriptRoot 'build-gui.ps1') | Out-Host }
[pscustomobject]@{Gen2Hash=$gen2Hash;DmaHash=$dmaHash;SignedDriverPath=$canonical}
