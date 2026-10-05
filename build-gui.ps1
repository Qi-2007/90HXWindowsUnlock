[CmdletBinding()]
param([string]$MSBuild,[switch]$Clean,[string]$CertificateDirectory,[switch]$NonInteractive)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'tools\BuildCommon.ps1')
$MSBuild=Resolve-BuildMSBuild $MSBuild
Assert-BuildFramework -Gui
$certificate=Write-CertificatePolicy $CertificateDirectory
$CertificateDirectory=Split-Path -Parent $certificate.Path
$target=if($Clean) {'/t:Rebuild'} else {'/t:Build'}
& $MSBuild (Join-Path $PSScriptRoot 'ui\CMP90HXUnlockConsole.csproj') $target /m /p:Configuration=Release /p:Platform=x64 "/p:CertificateDirectory=$CertificateDirectory" /verbosity:minimal | Out-Host
if ($LASTEXITCODE -ne 0) { throw "GUI build failed: $LASTEXITCODE" }
$exe=Join-Path $PSScriptRoot 'build\CMP90HXControl.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'GUI executable was not created.' }
Wait-GuiOptionalSigning $exe -NonInteractive:$NonInteractive
Get-FileHash -Algorithm SHA256 $exe
Write-Host "GUI ready: $exe"
