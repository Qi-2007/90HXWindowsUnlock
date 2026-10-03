[CmdletBinding()]
param([string]$MSBuild)
$ErrorActionPreference='Stop'
if (!$MSBuild) {
    $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (!(Test-Path -LiteralPath $vswhere)) { throw 'VS Installer/vswhere is missing.' }
    $found=@(& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe')
    if (!$found.Count) { throw 'MSBuild is unavailable.' }
    $MSBuild=$found[0]
}
$framework=Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8.1'
if (!(Test-Path -LiteralPath (Join-Path $framework 'PresentationFramework.dll'))) {
    throw '.NET Framework 4.8.1 Developer Pack is required.'
}
& $MSBuild (Join-Path $PSScriptRoot 'ui\CMP90HXControl.csproj') /m /p:Configuration=Release /p:Platform=x64 /verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw "GUI build failed: $LASTEXITCODE" }
$exe=Join-Path $PSScriptRoot 'build\CMP90HXControl.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'GUI executable was not created.' }
Get-FileHash -Algorithm SHA256 $exe
Write-Host "GUI ready: $exe"
