[CmdletBinding()]
param([string]$SourceDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'tools\ConsoleEncoding.ps1')
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$destination = Join-Path $root 'drivers'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$commit = 'f802245f0f6d318670210f732092bb754396c5c3'
$files = @(
    @{ Name='WinRing0x64.sys'; Hash='11BD2C9F9E2397C9A16E0990E4ED2CF0679498FE0FD418A3DFDAC60B5C160EE5' },
    @{ Name='ThrottleStop.sys'; Hash='16F83F056177C4EC24C7E99D01CA9D9D6713BD0497EEEDB777A3FFEFA99C97F0' }
)
foreach ($file in $files) {
    $target = Join-Path $destination $file.Name
    if ($SourceDirectory) {
        Copy-Item -LiteralPath (Join-Path $SourceDirectory $file.Name) -Destination $target -Force
    } else {
        $url = "https://raw.githubusercontent.com/ngthaihoc/CMP30HXmodtoGEN2/$commit/windows-v3.0/release/gen2/drivers/$($file.Name)"
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $target
    }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $target).Hash
    if ($actual -ne $file.Hash) { throw "Hash mismatch for $($file.Name): $actual" }
    Write-Host "$($file.Name) $actual"
}
