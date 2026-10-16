[CmdletBinding()]
param([string]$MSBuild,[string]$SdkVersion='10.0.28000.0',[switch]$UpdatePinsOnly)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'tools\BuildCommon.ps1')
if(!$UpdatePinsOnly){$MSBuild=Resolve-BuildMSBuild $MSBuild}
$out=Join-Path $PSScriptRoot 'build\reader'
New-Item -ItemType Directory -Path $out -Force | Out-Null
if(!$UpdatePinsOnly) {
 & $MSBuild (Join-Path $PSScriptRoot 'reader\client\CMP90HXReader.csproj') /t:Build /m /p:Configuration=Release /p:Platform=x64 /verbosity:minimal
 if($LASTEXITCODE -ne 0){throw 'Reader client build failed'}
 $client=Join-Path $out 'CMP90HXReader.exe'
 & $client --help
 if($LASTEXITCODE -ne 0){throw 'Reader client help failed'}
 $hash=(Get-FileHash -LiteralPath $client).Hash
 $bytes=@(0..31 | ForEach-Object {'0x'+$hash.Substring($_*2,2)}) -join ','
 [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'build\reader-caller-policy.h'),("#define CMP_CALLER_SHA256_HEX `"$hash`"`r`n#define CMP_CALLER_SHA256_BYTES {$bytes}`r`n"),[Text.Encoding]::ASCII)
 & $MSBuild (Join-Path $PSScriptRoot 'reader\driver\CMP90HXReader.vcxproj') /t:Build /m /p:Configuration=Release /p:Platform=x64 "/p:WindowsTargetPlatformVersion=$SdkVersion" /verbosity:minimal
 if($LASTEXITCODE -ne 0){throw 'Reader driver build failed'}
}
$sys=Join-Path $out 'CMP90HXReader.sys'
$client=Join-Path $out 'CMP90HXReader.exe'
$clientHash=(Get-FileHash -LiteralPath $client).Hash
$driverBytes=[Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($sys))
if(!$driverBytes.Contains('CMP90HX_CALLER_SHA256='+$clientHash)){throw 'Reader SYS/client caller policy mismatch'}
$p=Join-Path $PSScriptRoot 'ui\NativeWorkflow.cs';$s=[IO.File]::ReadAllText($p)
foreach($pin in @(@('ReaderHash',$sys),@('ReaderClientHash',$client))) {
 $pattern='internal const string '+$pin[0]+'="[0-9A-F]{64}";'
 if(![regex]::IsMatch($s,$pattern)){throw "Missing pin $($pin[0])"}
 $s=[regex]::Replace($s,$pattern,('internal const string '+$pin[0]+'="'+(Get-FileHash -LiteralPath $pin[1]).Hash+'";'))
}
[IO.File]::WriteAllText($p,$s,[Text.UTF8Encoding]::new($false))
Get-FileHash -LiteralPath $sys,$client
Write-Host 'Reader package ready; no driver loaded. Sign SYS, then run -UpdatePinsOnly and rebuild Console. Signing client requires rebuilding SYS.'
