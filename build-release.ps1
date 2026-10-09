[CmdletBinding()]
param([string]$OutputDirectory,[string]$MSBuild,[string]$CertificateDirectory,[switch]$NonInteractive,[switch]$PackageOnly)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'tools\BuildCommon.ps1')
& (Join-Path $PSScriptRoot 'tools\prepare-backend-packages.ps1') -PolicyOnly -RequireSigned
if(!$PackageOnly){& (Join-Path $PSScriptRoot 'build-gui.ps1') -MSBuild $MSBuild -CertificateDirectory $CertificateDirectory -NonInteractive:$NonInteractive}
$build=Join-Path $PSScriptRoot 'build'
$reader=Join-Path $build 'reader'
if((Get-AuthenticodeSignature -LiteralPath (Join-Path $reader 'CMP90HXReader.sys')).Status -ne 'Valid'){throw 'Sign the Console reader SYS, update pins, then rebuild Console before packaging.'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $PSScriptRoot ('dist\CMP90HX-Control-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw 'Use a new release directory to preserve prior packages.'}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
# Compare the built policy stamp with the current inputs even in PackageOnly mode.
$built=Get-Content -LiteralPath (Join-Path $build 'compiled-backends.json') -Raw | ConvertFrom-Json
$current=Get-Content -LiteralPath (Join-Path $build 'backend-inputs.json') -Raw | ConvertFrom-Json
if(($built.Inputs | ConvertTo-Json -Compress) -cne ($current.Inputs | ConvertTo-Json -Compress)){throw 'Backend packages changed since Console compilation; rebuild Console.'}
if(!$built.Artifacts){throw 'Missing compiled artifact hashes; rebuild Console.'}
foreach($property in $built.Artifacts.PSObject.Properties) {
 $file=Join-Path $build $property.Name.Replace('/',[IO.Path]::DirectorySeparatorChar)
 if(!(Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file).Hash -cne $property.Value){throw "Compiled artifact changed: $($property.Name). Rebuild Console."}
}
& (Join-Path $PSScriptRoot 'tools\prepare-backend-packages.ps1') -OutputDirectory $OutputDirectory -RequireSigned
foreach($name in @('CMP90HXControl.exe','CMP90HXControl.exe.config','CMP90HXBackground.exe','CMP90HXBackground.exe.config')){Copy-Item -LiteralPath (Join-Path $build $name) -Destination $OutputDirectory}
New-Item -ItemType Directory -Path (Join-Path $OutputDirectory 'reader') -Force | Out-Null
foreach($name in @('CMP90HXReader.exe','CMP90HXReader.exe.config','CMP90HXReader.sys')) {
 $file=Join-Path $reader $name;if(Test-Path -LiteralPath $file){Copy-Item -LiteralPath $file -Destination (Join-Path $OutputDirectory 'reader')}
}
$cert=Get-BuildCertificate $CertificateDirectory
New-Item -ItemType Directory -Path (Join-Path $OutputDirectory 'driver\cert') -Force | Out-Null
Copy-Item -LiteralPath $cert.Path -Destination (Join-Path $OutputDirectory 'driver\cert')
foreach($name in @('LICENSE','THIRD_PARTY_NOTICES.md')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $OutputDirectory}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs\backend-packages.md') -Destination (Join-Path $OutputDirectory 'BACKEND_PACKAGES.md')
$files=[ordered]@{}
Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File | ForEach-Object {$files[$_.FullName.Substring($OutputDirectory.Length+1).Replace('\','/')]=(Get-FileHash -LiteralPath $_.FullName).Hash}
$files | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'files.sha256.json') -Encoding UTF8
$current | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'release.json') -Encoding UTF8
Compress-Archive -LiteralPath $OutputDirectory -DestinationPath ($OutputDirectory+'.zip')
Write-Host "RELEASE_PACKAGE_READY $OutputDirectory.zip (prepared packages only; no backend build)"
