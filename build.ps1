[CmdletBinding()]
param([string]$MSBuild,[switch]$Clean,[string]$CertificateDirectory,[switch]$EnableRootCertificates)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'build-gui.ps1') -MSBuild $MSBuild -Clean:$Clean -CertificateDirectory $CertificateDirectory -EnableRootCertificates:$EnableRootCertificates -NonInteractive
