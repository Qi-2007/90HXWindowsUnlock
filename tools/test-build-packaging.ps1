$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'BuildCommon.ps1')
$project=Split-Path -Parent $PSScriptRoot
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('cmp90hx-build-test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $testRoot 'build\reader') -Force | Out-Null
function Check($Condition,[string]$Message){if(!$Condition){throw $Message}}
function Get-BuildRoot {return $testRoot}
function Get-BuildPayloadHash([string]$Path){return 'fixture-payload'}
function Get-AuthenticodeSignature([string]$LiteralPath){return [pscustomobject]@{Status=$signatures.Dequeue()}}
function Read-Host([string]$Prompt){return $answers.Dequeue()}
@'
param($MSBuild,[switch]$UpdatePinsOnly,[switch]$SkipPinUpdate)
if($UpdatePinsOnly){Add-Content (Join-Path $PSScriptRoot 'pins.log') 'pins';return}
if(!$SkipPinUpdate){throw 'Build must defer pins until signing'}
Add-Content (Join-Path $PSScriptRoot 'build.log') 'build'
Set-Content (Join-Path $PSScriptRoot 'build\reader\CMP90HXReader.sys') 'driver'
Set-Content (Join-Path $PSScriptRoot 'build\reader\CMP90HXReader.exe') 'client'
'@ | Set-Content -LiteralPath (Join-Path $testRoot 'build-reader-driver.ps1')
function Reset-Answers($Replies,$Statuses){
 $script:answers=New-Object 'Collections.Generic.Queue[string]'
 foreach($item in $Replies){$answers.Enqueue($item)}
 $script:signatures=New-Object 'Collections.Generic.Queue[string]'
 foreach($item in $Statuses){$signatures.Enqueue($item)}
}
try {
 Reset-Answers @('') @('NotSigned','Valid')
 Initialize-BuildReader
 Check ((Get-Content (Join-Path $testRoot 'build.log')).Count -eq 1) 'Missing Reader was not built'
 Check (Test-Path (Join-Path $testRoot 'pins.log')) 'Signed Reader pins were not updated'
 Reset-Answers @('n') @('Valid')
 Initialize-BuildReader
 Check (@(Get-Content (Join-Path $testRoot 'build.log')).Count -eq 1) 'Declining rebuild compiled Reader'
 Reset-Answers @('y') @('Valid')
 Initialize-BuildReader
 Check (@(Get-Content (Join-Path $testRoot 'build.log')).Count -eq 2) 'Accepting rebuild did not compile Reader'
 Reset-Answers @() @('Valid')
 Initialize-BuildReader -NonInteractive
 Reset-Answers @() @('NotSigned')
 $rejected=$false
 try {Initialize-BuildReader -NonInteractive}catch{$rejected=$_.Exception.Message -like '*Sign*rerun*'}
 Check $rejected 'Unsigned noninteractive build did not stop with signing instructions'
 $pinsBefore=@(Get-Content (Join-Path $testRoot 'pins.log')).Count
 Reset-Answers @('n','q') @('NotSigned')
 $cancelled=$false
 try {Initialize-BuildReader}catch{$cancelled=$_.Exception.Message -like '*cancelled at Reader signing*'}
 Check $cancelled 'Signing cancellation failed'
 Check (@(Get-Content (Join-Path $testRoot 'pins.log')).Count -eq $pinsBefore) 'Unsigned Reader pins were updated'
 Reset-Answers @('q') @()
 $cancelled=$false
 try {Initialize-BuildReader}catch{$cancelled=$_.Exception.Message -like '*cancelled*'}
 Check $cancelled 'Reader prompt cancellation failed'
 Reset-Answers @() @('Valid')
 Initialize-BuildReader -NonInteractive -RebuildReader
 Check (@(Get-Content (Join-Path $testRoot 'build.log')).Count -eq 3) 'Forced rebuild failed'

 $build=Join-Path $testRoot 'package-build'
 New-Item -ItemType Directory -Path $build | Out-Null
 $names=@('CMP90HXControl.exe','CMP90HXControl.exe.config','CMP90HXBackground.exe','CMP90HXBackground.exe.config',
 'native/CMP90HXUnlocker.exe','native/CMP90HXUnlocker.exe.config','native/CMP90HXUnlockNative.sys','native/nvpermissive-core.o',
 'session/CMP90HXUnlocker.exe','session/CMP90HXUnlockSession.sys','session/CMP90HXUnlockSession.inf','session/CMP90HXUnlockSession.cat',
 'reader/CMP90HXReader.exe','reader/CMP90HXReader.sys','certificates/root.cer','backend-inputs.json','debug.pdb')
 $hashes=[ordered]@{}
 foreach($name in $names){
  $path=Join-Path $build $name
  New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
  Set-Content -LiteralPath $path -Value $name
  $hashes[$name]=(Get-FileHash -LiteralPath $path).Hash
 }
 foreach($enabled in @($false,$true)){
  @{Artifacts=$hashes;RootCertificateSupport=$enabled} | ConvertTo-Json | Set-Content (Join-Path $build 'compiled-backends.json')
  $output=Join-Path $testRoot ('release-'+$enabled)
  & (Join-Path $project 'tools\copy-release-package.ps1') -BuildDirectory $build -OutputDirectory $output -EnableRootCertificates:$enabled
  $expected=14+4
  if($enabled){$expected++}
  Check (@(Get-ChildItem $output -Recurse -File).Count -eq $expected) 'Release whitelist has unexpected files'
  Check ((Test-Path (Join-Path $output 'certificates/root.cer')) -eq $enabled) 'Certificate feature packaging mismatch'
  $manifest=Get-Content (Join-Path $output 'files.sha256.json') -Raw | ConvertFrom-Json
  foreach($entry in $manifest.PSObject.Properties){Check ((Get-FileHash (Join-Path $output $entry.Name)).Hash -eq $entry.Value) 'Runtime manifest hash mismatch'}
  Check (!(Test-Path (Join-Path $output 'backend-inputs.json'))) 'Build metadata leaked into release'
 }
 Set-Content (Join-Path $build 'reader/CMP90HXReader.sys') 'changed after build'
 $rejected=$false
 try {& (Join-Path $project 'tools\copy-release-package.ps1') -BuildDirectory $build -OutputDirectory (Join-Path $testRoot 'invalid') -EnableRootCertificates}catch{$rejected=$_.Exception.Message -like '*artifact changed*'}
 Check $rejected 'Modified compiled driver was packaged'
 Write-Host 'PASS: Reader build/signing decisions and minimal release packages (certificates enabled/disabled).'
} finally {
 $resolved=[IO.Path]::GetFullPath($testRoot)
 if($resolved.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved) -like 'cmp90hx-build-test-*'){
  Remove-Item -LiteralPath $resolved -Recurse -Force
 }
}
