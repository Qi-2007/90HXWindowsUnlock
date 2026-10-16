[CmdletBinding(SupportsShouldProcess=$true)]
param([Parameter(Mandatory=$true)][string]$OutputDirectory,[string]$CertificateName='')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
$project=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
if($root -eq $project -or $root -eq [IO.Path]::GetPathRoot($root).TrimEnd('\') -or
   [IO.Path]::GetFileName($CertificateName) -ne $CertificateName){throw 'Invalid runtime package root or certificate filename.'}
$layout=[ordered]@{
 native=@('CMP90HXUnlocker.exe','CMP90HXUnlocker.exe.config','CMP90HXUnlockNative.sys','nvpermissive-core.o')
 session=@('CMP90HXUnlocker.exe','CMP90HXUnlocker.exe.config','CMP90HXUnlockSession.sys','CMP90HXUnlockSession.inf','CMP90HXUnlockSession.cat')
 reader=@('CMP90HXReader.exe','CMP90HXReader.exe.config','CMP90HXReader.sys')
 certificates=@($CertificateName)
}
# Refuse incomplete packages, source folders and directory links before deleting.
foreach($file in @('CMP90HXControl.exe','CMP90HXBackground.exe','native\CMP90HXUnlockNative.sys','session\CMP90HXUnlockSession.sys','reader\CMP90HXReader.sys')){
 if(!(Test-Path -LiteralPath (Join-Path $root $file) -PathType Leaf)){throw "Incomplete runtime package: $file"}
}
$targets=@()
foreach($folder in @('core','driver','runtime','reader-driver')+@($layout.Keys)){
 $path=Join-Path $root $folder
 if(!(Test-Path -LiteralPath $path)){continue}
 $resolved=(Resolve-Path -LiteralPath $path).ProviderPath
 $entry=Get-Item -LiteralPath $resolved -Force
 if(!$resolved.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase) -or
   ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
   @(Get-ChildItem -LiteralPath $resolved -Recurse -Force -Attributes ReparsePoint).Count){throw "Unsafe runtime directory: $resolved"}
 if($folder -eq 'certificates' -and !$CertificateName){$targets+=@($entry)}
 elseif($layout.Contains($folder)){$targets+=@(Get-ChildItem -LiteralPath $resolved -Force | Where-Object {$_.Name -notin $layout[$folder]})}
 else{$targets+=@($entry)}
}
foreach($entry in $targets){
 if($PSCmdlet.ShouldProcess($entry.FullName,'Delete obsolete runtime package files')){Remove-Item -LiteralPath $entry.FullName -Recurse -Force}
}
