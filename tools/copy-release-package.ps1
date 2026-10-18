[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$BuildDirectory,[Parameter(Mandatory=$true)][string]$OutputDirectory,[switch]$EnableRootCertificates)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$build=[IO.Path]::GetFullPath($BuildDirectory).TrimEnd('\')
$output=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if($output -eq $build -or $output -eq $project){throw 'Release destination must differ from build and source directories.'}
if((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count){throw 'Release destination must be empty.'}
$stamp=Get-Content -LiteralPath (Join-Path $build 'compiled-backends.json') -Raw | ConvertFrom-Json
if($stamp.RootCertificateSupport -ne $EnableRootCertificates.IsPresent){throw 'Root certificate option differs from the compiled Console.'}
$required=@(
 'CMP90HXControl.exe','CMP90HXControl.exe.config','CMP90HXBackground.exe','CMP90HXBackground.exe.config',
 'native/CMP90HXUnlocker.exe','native/CMP90HXUnlocker.exe.config','native/CMP90HXUnlockNative.sys','native/nvpermissive-core.o',
 'session/CMP90HXUnlocker.exe','session/CMP90HXUnlockSession.sys','session/CMP90HXUnlockSession.inf','session/CMP90HXUnlockSession.cat',
 'reader/CMP90HXReader.exe','reader/CMP90HXReader.sys'
)
$optional=@('session/CMP90HXUnlocker.exe.config','reader/CMP90HXReader.exe.config')
$files=[ordered]@{}
foreach($name in $required+$optional){
 $property=$stamp.Artifacts.PSObject.Properties[$name]
 if(!$property){if($name -in $required){throw "Missing compiled runtime artifact: $name"};continue}
 $files[$name]=$property.Value
}
if($EnableRootCertificates){
 $certificates=@($stamp.Artifacts.PSObject.Properties | Where-Object {$_.Name -match '^certificates/[^/\\:]+\.(cer|crt)$'})
 if($certificates.Count -ne 1){throw 'Expected one compiled public root certificate.'}
 $files[$certificates[0].Name]=$certificates[0].Value
}
# Validate the entire whitelist before copying; metadata and build caches never enter the package.
foreach($name in $files.Keys){
 $source=Join-Path $build $name
 if(!(Test-Path -LiteralPath $source -PathType Leaf) -or (Get-FileHash -LiteralPath $source).Hash -cne $files[$name]){throw "Compiled artifact changed: $name. Rebuild Console."}
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
foreach($name in $files.Keys){
 $destination=Join-Path $output $name
 New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
 Copy-Item -LiteralPath (Join-Path $build $name) -Destination $destination
 if((Get-FileHash -LiteralPath $destination).Hash -cne $files[$name]){throw "Runtime artifact changed during packaging: $name"}
}
Copy-Item -LiteralPath (Join-Path $project 'LICENSE') -Destination $output
$files | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'files.sha256.json') -Encoding UTF8
