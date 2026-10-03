[CmdletBinding()]
param([string]$MSBuild,[string]$SdkVersion)
$ErrorActionPreference='Stop'
if (!$MSBuild) {
    $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (!(Test-Path -LiteralPath $vswhere)) { throw 'VS Installer/vswhere missing. Install VS 2022 C++ desktop tools, matching SDK/WDK and WDK VS integration.' }
    $found=@(& $vswhere -latest -version '[17.0,18.0)' -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe')
    if (!$found.Count) { throw 'No VS 2022 MSBuild found. Install VS 2022 side-by-side, or explicitly pass -MSBuild for a WDK-supported installation.' }
    $MSBuild=$found[0]
}
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
& $MSBuild (Join-Path $PSScriptRoot 'driver\CMP90HXDma.vcxproj') /m /p:Configuration=Release /p:Platform=x64 "/p:WindowsTargetPlatformVersion=$SdkVersion"
if ($LASTEXITCODE -ne 0) { throw "WDK driver build failed: $LASTEXITCODE" }
$sys=Join-Path $PSScriptRoot 'build\dma-driver\CMP90HXDma.sys'
if (!(Test-Path -LiteralPath $sys)) { throw 'Build produced no CMP90HXDma.sys.' }
Get-FileHash -Algorithm SHA256 $sys
Write-Host 'Unsigned experimental driver built. Sign it before installation. No driver loaded or boot settings changed.'
