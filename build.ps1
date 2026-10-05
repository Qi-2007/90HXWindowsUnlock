[CmdletBinding()]
param([switch]$Clean,[switch]$IfNeeded,[string]$MSBuild)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'tools\BuildCommon.ps1')
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $root 'build'
# -Clean rebuilds this project only; driver signatures/certificates are preserved.
New-Item -ItemType Directory -Path $out -Force | Out-Null
Assert-BuildFramework
$MSBuild=Resolve-BuildMSBuild $MSBuild
$project=Join-Path $root 'src\CMP90HXUnlockProgram.csproj'
$exe=Get-Gen2ProjectOutput
$stamp = Join-Path $out 'CMP90HXUnlocker.validated.sha256'
$sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -File | Where-Object Extension -in @('.cs','.config','.csproj','.props','.targets') | Select-Object -ExpandProperty FullName)
$cores=@('469dc0c','380bdf3') | ForEach-Object {
    Join-Path $root "vendor\nvpermissive-dist-$_\obj\nvpermissive-core.o"
}
$cores=@($cores | Where-Object { Test-Path -LiteralPath $_ })
if (!$cores.Count) { Write-Warning 'No native core installed: building Windows sources and self-tests only. Run prepare-core.ps1 before Unlock.' }
if ($IfNeeded -and !$Clean -and (Test-Path -LiteralPath $exe) -and (Test-Path -LiteralPath $stamp)) {
    $built=(Get-Item -LiteralPath $exe).LastWriteTimeUtc
    $inputs=@($sources)+@($PSCommandPath,(Join-Path $root 'tools\BuildCommon.ps1'))+@($cores)
    $newer=@($inputs | Where-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc -gt $built })
    if ($newer.Count -eq 0 -and (Get-Content -Raw -LiteralPath $stamp).Trim() -eq (Get-FileHash -Algorithm SHA256 $exe).Hash) {
        Write-Host 'Build is current; reusing validated executable.'; return
    }
}
if (Test-Path -LiteralPath $stamp) { Remove-Item -LiteralPath $stamp }
$target=if($Clean) {'/t:Rebuild'} else {'/t:Build'}
& $MSBuild $project $target /m /p:Configuration=Release /p:Platform=x64 /verbosity:minimal | Out-Host
if ($LASTEXITCODE -ne 0) { throw "C# compilation failed: $LASTEXITCODE" }
if (!(Test-Path -LiteralPath $exe)) { throw "Project output is missing: $exe" }
& $exe self-test | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Self-test failed: $LASTEXITCODE" }
foreach ($core in $cores) {
    & $exe core-test --core $core | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Pinned native core test failed: $LASTEXITCODE" }
}
$builtHash=Get-FileHash -Algorithm SHA256 $exe
Set-Content -LiteralPath $stamp -Value $builtHash.Hash -Encoding ASCII
$builtHash
Write-Host "Unlocker ready: $exe"
