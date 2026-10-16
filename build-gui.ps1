[CmdletBinding()]
param([string]$MSBuild,[switch]$Clean,[string]$CertificateDirectory,[switch]$NonInteractive,[string]$OutputDirectory,[switch]$EnableRootCertificates)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'tools\BuildCommon.ps1')
$MSBuild=Resolve-BuildMSBuild $MSBuild
Assert-BuildFramework -Gui
$certificate=Write-CertificatePolicy $CertificateDirectory -EnableRootCertificates:$EnableRootCertificates
if($certificate){$CertificateDirectory=Split-Path -Parent $certificate.Path}
$rootCertificateProperty='/p:RootCertificateSupport='+$EnableRootCertificates.IsPresent.ToString().ToLowerInvariant()
if(!$OutputDirectory){$OutputDirectory=Join-Path $PSScriptRoot 'build'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
& (Join-Path $PSScriptRoot 'tools\prepare-backend-packages.ps1') -OutputDirectory $OutputDirectory
$pinSource=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ui\NativeWorkflow.cs'))
foreach($pair in @(@('ReaderHash','CMP90HXReader.sys'),@('ReaderClientHash','CMP90HXReader.exe'))) {
 $file=Join-Path $PSScriptRoot ('build\reader\'+$pair[1])
 $pin=[regex]::Match($pinSource,('internal const string '+$pair[0]+'="([0-9A-F]{64})";'))
 if(!$pin.Success -or !(Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file).Hash -cne $pin.Groups[1].Value){throw 'Shared reader inputs changed. Run build-reader-driver.ps1 -UpdatePinsOnly before rebuilding Console.'}
}
$target=if($Clean) {'/t:Rebuild'} else {'/t:Build'}
& $MSBuild (Join-Path $PSScriptRoot 'backend\CMP90HXBackground.csproj') $target /m /p:Configuration=Release /p:Platform=x64 "/p:OutputPath=$OutputDirectory\" "/p:CertificateDirectory=$CertificateDirectory" $rootCertificateProperty /verbosity:minimal | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Background build failed: $LASTEXITCODE" }
& $MSBuild (Join-Path $PSScriptRoot 'ui\CMP90HXUnlockConsole.csproj') $target /m /p:Configuration=Release /p:Platform=x64 "/p:OutputPath=$OutputDirectory\" "/p:CertificateDirectory=$CertificateDirectory" $rootCertificateProperty /verbosity:minimal | Out-Host
if ($LASTEXITCODE -ne 0) { throw "GUI build failed: $LASTEXITCODE" }
$exe=Join-Path $OutputDirectory 'CMP90HXControl.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'GUI executable was not created.' }
Wait-GuiOptionalSigning $exe -NonInteractive:$NonInteractive
if($EnableRootCertificates){
 $certOut=Join-Path $OutputDirectory 'certificates';New-Item -ItemType Directory -Path $certOut -Force | Out-Null
 Copy-Item -LiteralPath $certificate.Path -Destination $certOut -Force
}
$readerSource=Join-Path $PSScriptRoot 'build\reader'
if(Test-Path -LiteralPath $readerSource){
 New-Item -ItemType Directory -Path (Join-Path $OutputDirectory 'reader') -Force | Out-Null
 foreach($name in @('CMP90HXReader.exe','CMP90HXReader.exe.config','CMP90HXReader.sys')){
  $file=Join-Path $readerSource $name
  $destination=Join-Path $OutputDirectory ('reader\'+$name)
  if((Test-Path -LiteralPath $file) -and ![string]::Equals([IO.Path]::GetFullPath($file),[IO.Path]::GetFullPath($destination),[StringComparison]::OrdinalIgnoreCase)){Copy-Item -LiteralPath $file -Destination $destination -Force}
 }
}
& (Join-Path $PSScriptRoot 'tools\prune-runtime-package.ps1') -OutputDirectory $OutputDirectory -CertificateName $certificate.FileName
$manifest=[ordered]@{}
foreach($folder in @('native','session','reader','certificates')){
 $path=Join-Path $OutputDirectory $folder;if(Test-Path -LiteralPath $path){Get-ChildItem -LiteralPath $path -Recurse -File | ForEach-Object {$manifest[$_.FullName.Substring($OutputDirectory.Length+1).Replace('\','/')]=(Get-FileHash -LiteralPath $_.FullName).Hash}}
}
foreach($name in @('CMP90HXControl.exe','CMP90HXControl.exe.config','CMP90HXBackground.exe','CMP90HXBackground.exe.config')){$manifest[$name]=(Get-FileHash -LiteralPath (Join-Path $OutputDirectory $name)).Hash}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'files.sha256.json') -Encoding UTF8
$staged=Get-Content -LiteralPath (Join-Path $OutputDirectory 'backend-inputs.json') -Raw | ConvertFrom-Json
$current=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'build\backend-inputs.json') -Raw | ConvertFrom-Json
if(($staged.Inputs | ConvertTo-Json -Compress) -cne ($current.Inputs | ConvertTo-Json -Compress)){throw 'Backend inputs changed during Console compilation; finish copying and rebuild.'}
$staged | Add-Member -NotePropertyName Artifacts -NotePropertyValue $manifest -Force
$staged | Add-Member -NotePropertyName RootCertificateSupport -NotePropertyValue $EnableRootCertificates.IsPresent -Force
$staged | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'compiled-backends.json') -Encoding UTF8
Get-FileHash -Algorithm SHA256 $exe
Write-Host "GUI ready: $exe"
