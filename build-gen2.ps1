[CmdletBinding()]
param(
    [string]$MSBuild,
    [switch]$Clean,
    [string]$CertificatePath,
    [Security.SecureString]$CertificatePassword,
    [string]$TimestampUrl,
    [string]$SignTool,
    [switch]$ChooseCertificate,
    [switch]$NonInteractive
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'tools\ConsoleEncoding.ps1')
& (Join-Path $PSScriptRoot 'build.ps1') -MSBuild $MSBuild -Clean:$Clean | Out-Host
$sign=@{CertificatePath=$CertificatePath;TimestampUrl=$TimestampUrl;SignTool=$SignTool;ChooseCertificate=$ChooseCertificate;NonInteractive=$NonInteractive}
if ($null -ne $CertificatePassword) { $sign.CertificatePassword=$CertificatePassword }
& (Join-Path $PSScriptRoot 'sign-gen2.ps1') @sign
