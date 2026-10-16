[CmdletBinding()]
param([string]$OutputDirectory,[switch]$PolicyOnly,[switch]$RequireSigned)
$ErrorActionPreference='Stop'
function Get-PackageFileHash([string]$LiteralPath,[string]$Algorithm='SHA256') {
 $sha=[Security.Cryptography.SHA256]::Create();$stream=[IO.File]::OpenRead($LiteralPath)
 try {[pscustomobject]@{Hash=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')}} finally {$stream.Dispose();$sha.Dispose()}
}
$root=Split-Path -Parent $PSScriptRoot
$native=Join-Path $root 'backends\native'
$session=Join-Path $root 'backends\session'
$inputs=[ordered]@{
 'native/CMP90HXUnlocker.exe'=Join-Path $native 'CMP90HXUnlocker.exe'
 'native/CMP90HXUnlocker.exe.config'=Join-Path $native 'CMP90HXUnlocker.exe.config'
 'native/CMP90HXUnlockNative.sys'=Join-Path $native 'CMP90HXUnlockNative.sys'
 'native/nvpermissive-core.o'=Join-Path $native 'nvpermissive-core.o'
 'session/CMP90HXUnlocker.exe'=Join-Path $session 'CMP90HXUnlocker.exe'
 'session/CMP90HXUnlockSession.sys'=Join-Path $session 'CMP90HXUnlockSession.sys'
 'session/CMP90HXUnlockSession.inf'=Join-Path $session 'CMP90HXUnlockSession.inf'
 'session/CMP90HXUnlockSession.cat'=Join-Path $session 'CMP90HXUnlockSession.cat'
}
$optional=Join-Path $session 'CMP90HXUnlocker.exe.config'
if(Test-Path -LiteralPath $optional){$inputs['session/CMP90HXUnlocker.exe.config']=$optional}
$hashes=[ordered]@{}
foreach($key in $inputs.Keys) {
 $file=$inputs[$key]
 if(!(Test-Path -LiteralPath $file -PathType Leaf)){throw "Missing prepared backend file: $file. Build/sign the backend separately and copy its complete package into Console/backends."}
 if((Get-Item -LiteralPath $file).Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Backend input cannot be a link: $file"}
 $hashes[$key]=(Get-PackageFileHash -LiteralPath $file -Algorithm SHA256).Hash
}
# A SYS and its worker must be from the same backend build, independently of Console version.
foreach($pair in @(@('native/CMP90HXUnlockNative.sys','native/CMP90HXUnlocker.exe'),@('session/CMP90HXUnlockSession.sys','session/CMP90HXUnlocker.exe'))) {
 $bytes=[Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($inputs[$pair[0]]))
 $policies=[regex]::Matches($bytes,'CMP90HX_CALLER_SHA256=([0-9A-F]{64})\x00')
 if($policies.Count -ne 1 -or $policies[0].Groups[1].Value -cne $hashes[$pair[1]]){throw "Mismatched SYS/worker package: $($pair[0]). Copy a matching pair; Console does not rebuild backends."}
}
if($RequireSigned) {
 foreach($key in @('native/CMP90HXUnlockNative.sys','session/CMP90HXUnlockSession.sys','session/CMP90HXUnlockSession.cat')) {
  if((Get-AuthenticodeSignature -LiteralPath $inputs[$key]).Status -ne 'Valid'){throw "Backend signature is not valid: $($inputs[$key])"}
 }
}
$nativeVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($inputs['native/CMP90HXUnlocker.exe']).FileVersion
$sessionVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($inputs['session/CMP90HXUnlocker.exe']).FileVersion
$lines=@('namespace CMP90HX.Control { internal static class BackendPackagePolicy {')
foreach($entry in @(@('NativeWorker','native/CMP90HXUnlocker.exe'),@('NativeDma','native/CMP90HXUnlockNative.sys'),@('NativeCore','native/nvpermissive-core.o'))) {
 $lines+='internal const string '+$entry[0]+'="'+$hashes[$entry[1]]+'";'
}
$lines+='internal const string NativeVersion='+ (ConvertTo-Json -InputObject ([string]$nativeVersion) -Compress)+';'
$lines+='internal const string SessionVersion='+ (ConvertTo-Json -InputObject ([string]$sessionVersion) -Compress)+';'
$lines+='internal static void VerifySession(string directory) {'
foreach($key in $inputs.Keys | Where-Object { $_.StartsWith('session/') }) {
 $lines+='RuntimePaths.RequireHash(System.IO.Path.Combine(directory,"'+$key.Substring(8)+'"),"'+$hashes[$key]+'");'
}
$lines+='} } }'
$build=Join-Path $root 'build';New-Item -ItemType Directory -Path $build -Force | Out-Null
$policy=Join-Path $build 'BackendPackagePolicy.cs';$content=$lines -join "`r`n"
if(!(Test-Path -LiteralPath $policy) -or [IO.File]::ReadAllText($policy) -ne $content){[IO.File]::WriteAllText($policy,$content,[Text.UTF8Encoding]::new($false))}
$metadata=[ordered]@{NativeVersion=$nativeVersion;SessionVersion=$sessionVersion;Inputs=$hashes}
$metadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $build 'backend-inputs.json') -Encoding UTF8
if(!$PolicyOnly) {
 if(!$OutputDirectory){$OutputDirectory=$build}
 $OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory);New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
 foreach($key in $inputs.Keys) {
  $destination=Join-Path $OutputDirectory $key.Replace('/',[IO.Path]::DirectorySeparatorChar)
  New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
  if((Test-Path -LiteralPath $destination) -and (Get-PackageFileHash -LiteralPath $destination).Hash -eq $hashes[$key]){continue}
  Copy-Item -LiteralPath $inputs[$key] -Destination $destination -Force
 }
 $metadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'backend-inputs.json') -Encoding UTF8
}
# Detect changes during preparation rather than compiling a mixed release.
foreach($key in $inputs.Keys){if((Get-PackageFileHash -LiteralPath $inputs[$key]).Hash -ne $hashes[$key]){throw 'Backend input changed during preparation. Finish copying packages, then retry.'}}
Write-Host "Prepared local backend packages: Native $nativeVersion; Session $sessionVersion. No backend compiled."
