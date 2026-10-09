[CmdletBinding()]
param([string]$CertificateDirectory)
$ErrorActionPreference='Stop'
# MSBuild can inherit PowerShell 7's module path; import Windows PowerShell utilities explicitly.
Import-Module (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -Force
. (Join-Path $PSScriptRoot 'BuildCommon.ps1')
Write-CertificatePolicy $CertificateDirectory | Out-Null
