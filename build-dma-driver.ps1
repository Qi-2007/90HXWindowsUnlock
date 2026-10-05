[CmdletBinding()]
param([string]$MSBuild,[string]$SdkVersion,[switch]$Clean)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'tools\BuildCommon.ps1')
& (Join-Path $PSScriptRoot 'build.ps1') -IfNeeded | Out-Host
$callerHash=Write-CallerPolicy
$MSBuild=Resolve-BuildMSBuild $MSBuild -Driver
$kitRoot=(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -ErrorAction Stop).KitsRoot10
$versions=@(Get-ChildItem -LiteralPath (Join-Path $kitRoot 'Include') -Directory | Where-Object {
    (Test-Path -LiteralPath (Join-Path $_.FullName 'km\ntddk.h')) -and
    (Test-Path -LiteralPath (Join-Path $kitRoot "Lib\$($_.Name)\km\x64\ntoskrnl.lib")) -and
    (Test-Path -LiteralPath (Join-Path $_.FullName 'um\windows.h'))
} | Sort-Object { [version]$_.Name } -Descending)
if ($SdkVersion) {
    if (!($versions | Where-Object Name -EQ $SdkVersion)) { throw "SDK/WDK $SdkVersion is incomplete: require km/ntddk.h, km/x64/ntoskrnl.lib and um/windows.h." }
} elseif ($versions.Count) { $SdkVersion=$versions[0].Name }
else { throw 'No matching SDK/WDK headers and kernel libraries found. Install WDK, not only Windows SDK.' }
Write-Host "MSBuild: $MSBuild"
Write-Host "SDK/WDK: $SdkVersion; Kits root: $kitRoot"
$target=if($Clean) {'/t:Rebuild'} else {'/t:Build'}
& $MSBuild (Join-Path $PSScriptRoot 'driver\CMP90HXDma.vcxproj') $target /m /p:Configuration=Release /p:Platform=x64 "/p:WindowsTargetPlatformVersion=$SdkVersion" /verbosity:minimal | Out-Host
if ($LASTEXITCODE -ne 0) { throw "WDK driver build failed: $LASTEXITCODE" }
$sys=Join-Path $PSScriptRoot 'build\dma-driver\CMP90HXDma.sys'
if (!(Test-Path -LiteralPath $sys)) { throw 'Build produced no CMP90HXDma.sys.' }
if ((Assert-Gen2Validated) -ne $callerHash) { throw 'Gen2 changed during driver build; retry.' }
Assert-DriverCallerPolicy $sys $callerHash
Get-FileHash -Algorithm SHA256 $sys
Write-Host 'Unsigned experimental driver built. Sign it before installation. No driver loaded or boot settings changed.'
