[CmdletBinding()]
param(
    [string]$MSBuild,
    [switch]$Clean,
    [string]$CertificatePath,
    [Security.SecureString]$CertificatePassword,
    [string]$TimestampUrl,
    [string]$SignTool,
    [string]$DriverPath,
    [switch]$ChooseCertificate,
    [switch]$NonInteractive
)
$ErrorActionPreference='Stop'
$BackendRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\90HXWinNativeUnlock'))
# Program-signing parameters are retained for compatibility; no signing is needed.
& (Join-Path $BackendRoot 'build.ps1') -MSBuild $MSBuild -Clean:$Clean | Out-Host
. (Join-Path $BackendRoot 'tools\BuildCommon.ps1')
Wait-Gen2OptionalSigning -NonInteractive:$NonInteractive
& (Join-Path $PSScriptRoot 'update-release-hashes.ps1') -DriverPath $DriverPath -SkipGuiBuild -SkipWorkerBuild | Out-Host
& (Join-Path $PSScriptRoot 'build-gui.ps1') -MSBuild $MSBuild -Clean:$Clean -NonInteractive:$NonInteractive | Out-Host
Write-Host 'APP_BUILD_READY build\CMP90HXControl.exe (uses the existing signed DMA driver)'
