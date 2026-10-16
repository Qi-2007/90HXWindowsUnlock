[CmdletBinding(SupportsShouldProcess=$true)]
param()
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$build=Join-Path $root 'build'
if(!(Test-Path -LiteralPath $build)){return}
$archive=Join-Path $root ('.build\archive\build-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
$keep=@('native','session','reader','certificates',
 'CMP90HXBackground.exe','CMP90HXBackground.exe.config','CMP90HXControl.exe','CMP90HXControl.exe.config',
 'backend-inputs.json','compiled-backends.json','files.sha256.json',
 'BackendPackagePolicy.cs','CertificatePolicy.cs','reader-caller-policy.h')
$caches=@('background-obj','gui-obj','reader-client-obj','reader-driver-obj')
$plan=@(Get-ChildItem -LiteralPath $build -Force | Where-Object {$_.Name -notin $keep})
$reader=Join-Path $build 'reader'
if(Test-Path -LiteralPath $reader){$plan+=@(Get-ChildItem -LiteralPath $reader -File | Where-Object {$_.Name -notin @('CMP90HXReader.exe','CMP90HXReader.exe.config','CMP90HXReader.sys')})}
# Validate every target before moving or deleting anything. Never follow links.
foreach($entry in $plan){
 $resolved=(Resolve-Path -LiteralPath $entry.FullName).ProviderPath
 if(!$resolved.StartsWith($build+'\',[StringComparison]::OrdinalIgnoreCase)){throw "Unsafe target: $resolved"}
 if(($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
    ($entry.PSIsContainer -and @(Get-ChildItem -LiteralPath $resolved -Recurse -Force -Attributes ReparsePoint).Count)){throw "Linked target refused: $resolved"}
}
foreach($entry in $plan){
 $files=if($entry.PSIsContainer){@(Get-ChildItem -LiteralPath $entry.FullName -Recurse -File -Force)}else{@($entry)}
 $locked=$false
 foreach($file in $files){
  $stream=$null
  try{$stream=[IO.File]::Open($file.FullName,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}
  catch{$locked=$true;break}
  finally{if($stream){$stream.Dispose()}}
 }
 if($locked){Write-Warning "Keeping files in use: $($entry.FullName)";continue}
 if($entry.Name -in $caches -and [IO.Path]::GetDirectoryName($entry.FullName) -eq $build){
  if($PSCmdlet.ShouldProcess($entry.FullName,'Remove reproducible compilation cache')){Remove-Item -LiteralPath $entry.FullName -Recurse -Force}
 }else{
  $relative=$entry.FullName.Substring($build.Length+1)
  $destination=Join-Path $archive $relative
  if($PSCmdlet.ShouldProcess($entry.FullName,"Archive to $destination")){
   New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
   Move-Item -LiteralPath $entry.FullName -Destination $destination
  }
 }
}
Write-Host "Current runtime and reader build inputs preserved. Archive: $archive"
