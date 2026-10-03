[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$PackageDirectory)
$ErrorActionPreference='Stop'
$PackageDirectory=[IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\')
$manifestPath=Join-Path $PackageDirectory 'files.sha256.json'
$originalManifest=[IO.File]::ReadAllText($manifestPath)
$manifest=$originalManifest | ConvertFrom-Json
$gui=Join-Path $PackageDirectory 'CMP90HXControl.exe'
$signature=Get-AuthenticodeSignature -LiteralPath $gui
if ($signature.Status -ne 'Valid') { throw "GUI signature verification failed: $($signature.Status): $($signature.StatusMessage)" }

# Signing changes the checksum and certificate table, not the executable body.
# Prove that against the original archive before changing its pinned hash.
function Get-UnsignedPayload([byte[]]$Bytes) {
    if ($Bytes.Length -lt 64 -or [BitConverter]::ToUInt16($Bytes,0) -ne 0x5a4d) { throw 'Invalid PE image.' }
    $pe=[BitConverter]::ToInt32($Bytes,60)
    if ($pe -lt 64 -or $pe -gt $Bytes.Length-256 -or [BitConverter]::ToUInt32($Bytes,$pe) -ne 0x4550) { throw 'Invalid PE header.' }
    $optional=$pe+24
    $magic=[BitConverter]::ToUInt16($Bytes,$optional)
    if ($magic -eq 0x20b) { $directories=$optional+112 }
    elseif ($magic -eq 0x10b) { $directories=$optional+96 }
    else { throw 'Unsupported PE optional header.' }
    $security=$directories+32
    $certificate=[BitConverter]::ToUInt32($Bytes,$security)
    $size=[BitConverter]::ToUInt32($Bytes,$security+4)
    $length=$Bytes.Length
    if ($certificate -or $size) {
        if (!$certificate -or !$size -or ($certificate % 8) -ne 0 -or
            [uint64]$certificate+$size -ne $Bytes.Length -or $certificate -le $security+8) { throw 'Unexpected PE certificate layout.' }
        $length=[int]$certificate
    }
    $payload=New-Object byte[] $length
    [Array]::Copy($Bytes,$payload,$length)
    [Array]::Clear($payload,$optional+64,4)
    [Array]::Clear($payload,$security,8)
    return ,$payload
}
function Hash-Bytes([byte[]]$Bytes) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-','') }
    finally { $sha.Dispose() }
}
$currentGuiHash=(Get-FileHash -LiteralPath $gui -Algorithm SHA256).Hash
foreach ($entry in $manifest.PSObject.Properties) {
    $path=[IO.Path]::GetFullPath((Join-Path $PackageDirectory $entry.Name))
    if (!$path.StartsWith($PackageDirectory+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path escapes the package.' }
    if ($entry.Name -eq 'CMP90HXControl.exe') { continue }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.Value) { throw "Unexpected file change: $($entry.Name). Rebuild the package instead." }
}
if ($currentGuiHash -ne $manifest.'CMP90HXControl.exe') {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive=[IO.Compression.ZipFile]::OpenRead($PackageDirectory+'.zip')
    try {
        $entries=@($archive.Entries | Where-Object { $_.FullName.Replace('\','/').EndsWith('/CMP90HXControl.exe') -or $_.FullName -eq 'CMP90HXControl.exe' })
        if ($entries.Count -ne 1) { throw 'Expected one original GUI in the release ZIP.' }
        $stream=$entries[0].Open(); $memory=New-Object IO.MemoryStream
        try { $stream.CopyTo($memory); $original=$memory.ToArray() }
        finally { $stream.Dispose(); $memory.Dispose() }
    } finally { $archive.Dispose() }
    if ((Hash-Bytes $original) -ne $manifest.'CMP90HXControl.exe') { throw 'Original ZIP does not match the previous GUI pin.' }
    $signed=[IO.File]::ReadAllBytes($gui)
    if ((Hash-Bytes (Get-UnsignedPayload $original)) -ne (Hash-Bytes (Get-UnsignedPayload $signed))) {
        throw 'GUI executable body changed; rebuild and validate the package instead of refreshing its hash.'
    }
}
$manifest.'CMP90HXControl.exe'=$currentGuiHash
$zip=$PackageDirectory+'-signed-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.zip'
if (Test-Path -LiteralPath $zip) { throw 'Signed ZIP already exists; retry with a new timestamp.' }
$report=Join-Path (Split-Path -Parent $PackageDirectory) ('signed-package-validation-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.log')
try {
    $manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    $check=Start-Process -FilePath $gui -ArgumentList @('--validate-package',('"'+$report+'"')) -WindowStyle Hidden -Wait -PassThru
    if ($check.ExitCode -ne 0) { throw "Signed package validation failed; inspect $report" }
} catch {
    [IO.File]::WriteAllText($manifestPath,$originalManifest,(New-Object Text.UTF8Encoding($true)))
    throw
}
Compress-Archive -LiteralPath $PackageDirectory -DestinationPath $zip -CompressionLevel Optimal
$hash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
$hash+'  '+[IO.Path]::GetFileName($zip) | Set-Content -LiteralPath ($zip+'.sha256') -Encoding ASCII
Write-Host "SIGNED_PACKAGE_READY $zip"
Write-Host "SHA256 $hash"
Write-Host "Validation log: $report"
