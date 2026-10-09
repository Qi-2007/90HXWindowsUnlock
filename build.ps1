[CmdletBinding()]
param([string]$MSBuild,[switch]$Clean,[string]$CertificateDirectory)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'build-gui.ps1') -MSBuild $MSBuild -Clean:$Clean -CertificateDirectory $CertificateDirectory -NonInteractive