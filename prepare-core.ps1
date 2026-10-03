[CmdletBinding()]
param([string]$EfiPath,[string]$LegacyCorePath,[string]$Python='python')
$ErrorActionPreference='Stop'
if (!$EfiPath -and !$LegacyCorePath) { throw 'Provide -EfiPath and/or -LegacyCorePath.' }
if ($EfiPath) {
    $managed=Join-Path $PSScriptRoot 'vendor\nvpermissive-dist-469dc0c\obj\nvpermissive-core.o'
    & $Python (Join-Path $PSScriptRoot 'tools\extract-managed-core.py') $EfiPath --extract $managed
    if ($LASTEXITCODE -ne 0) { throw 'Pinned EFI extraction failed.' }
    if ((Get-FileHash -LiteralPath $managed -Algorithm SHA256).Hash -ne 'B533B7B245ED606C151CA336B9A6BACEBE935E3EC478246E879C668A4DD98A6A') { throw 'Extracted core hash mismatch.' }
    Write-Host 'Managed 469dc0c core prepared; no EFI execution or hardware access.'
}
if ($LegacyCorePath) {
    if ((Get-FileHash -LiteralPath $LegacyCorePath -Algorithm SHA256).Hash -ne 'C9702B4887D397272F86DCC25EEA2BB11A46D636C91311D7B71F2FB8B01951E5') { throw 'Legacy core hash mismatch.' }
    $legacy=Join-Path $PSScriptRoot 'vendor\nvpermissive-dist-380bdf3\obj\nvpermissive-core.o'
    New-Item -ItemType Directory -Path (Split-Path -Parent $legacy) -Force | Out-Null
    if ([IO.Path]::GetFullPath($LegacyCorePath) -ne [IO.Path]::GetFullPath($legacy)) {
        Copy-Item -LiteralPath $LegacyCorePath -Destination $legacy -Force
    }
    Write-Host 'Legacy 380bdf3 core prepared.'
}
