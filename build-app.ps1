[CmdletBinding()]
param([string]$MSBuild,[switch]$Clean,[switch]$NonInteractive,[string]$CertificateDirectory)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'build-gui.ps1') -MSBuild $MSBuild -Clean:$Clean -NonInteractive:$NonInteractive -CertificateDirectory $CertificateDirectory