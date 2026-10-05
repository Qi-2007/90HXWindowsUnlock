[CmdletBinding()]
param([string]$CertificateDirectory)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'BuildCommon.ps1')
Write-CertificatePolicy $CertificateDirectory | Out-Null
