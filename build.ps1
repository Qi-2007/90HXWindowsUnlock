[CmdletBinding()]
param([switch]$Clean,[switch]$IfNeeded,[string]$MSBuild)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $root 'build'
if ($Clean -and (Test-Path -LiteralPath $out)) {
    $resolvedOut=[IO.Path]::GetFullPath($out)
    if ($resolvedOut -ne [IO.Path]::GetFullPath((Join-Path $root 'build'))) { throw 'Unexpected build cleanup path.' }
    Remove-Item -LiteralPath $resolvedOut -Recurse -Force
}
New-Item -ItemType Directory -Path $out -Force | Out-Null
$framework=Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8.1'
if (!(Test-Path -LiteralPath (Join-Path $framework 'mscorlib.dll'))) { throw '.NET Framework 4.8.1 Developer Pack is required.' }
if (!$MSBuild) {
    $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (!(Test-Path -LiteralPath $vswhere)) { throw 'VS Installer/vswhere is missing.' }
    $found=@(& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe')
    if (!$found.Count) { throw 'MSBuild is unavailable.' }
    $MSBuild=$found[0]
}
$exe = Join-Path $out 'CMP90HXGen2.exe'
$stamp = Join-Path $out 'CMP90HXGen2.validated.sha256'
$sources=@('Hardware.cs','Gen2.cs','SelfTests.cs','Program.cs','NativeCore.cs','DmaArena.cs','KernelDma.cs','FullUnlock.cs','BridgeEcam.cs','GpuDmaProbe.cs') |
    ForEach-Object { Join-Path (Join-Path $root 'src') $_ }
$cores=@('469dc0c','380bdf3') | ForEach-Object {
    Join-Path $root "vendor\nvpermissive-dist-$_\obj\nvpermissive-core.o"
}
$cores=@($cores | Where-Object { Test-Path -LiteralPath $_ })
if (!$cores.Count) { Write-Warning 'No native core installed: building Windows sources and self-tests only. Run prepare-core.ps1 before Unlock.' }
if ($IfNeeded -and !$Clean -and (Test-Path -LiteralPath $exe) -and (Test-Path -LiteralPath $stamp)) {
    $built=(Get-Item -LiteralPath $exe).LastWriteTimeUtc
    $inputs=@($sources)+@($PSCommandPath,(Join-Path $root 'src\CMP90HXWindows.csproj'),(Join-Path $root 'src\App.config'))+@($cores)
    $newer=@($inputs | Where-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc -gt $built })
    if ($newer.Count -eq 0 -and (Get-Content -Raw -LiteralPath $stamp).Trim() -eq (Get-FileHash -Algorithm SHA256 $exe).Hash) {
        Write-Host 'Build is current; reusing validated executable.'; return
    }
}
if (Test-Path -LiteralPath $stamp) { Remove-Item -LiteralPath $stamp }
& $MSBuild (Join-Path $root 'src\CMP90HXWindows.csproj') /m /p:Configuration=Release /p:Platform=x64 /verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw "C# compilation failed: $LASTEXITCODE" }
& $exe self-test
if ($LASTEXITCODE -ne 0) { throw "Self-test failed: $LASTEXITCODE" }
foreach ($core in $cores) {
    & $exe core-test --core $core
    if ($LASTEXITCODE -ne 0) { throw "Pinned native core test failed: $LASTEXITCODE" }
}
$builtHash=Get-FileHash -Algorithm SHA256 $exe
Set-Content -LiteralPath $stamp -Value $builtHash.Hash -Encoding ASCII
$builtHash
