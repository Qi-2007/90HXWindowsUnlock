[CmdletBinding(SupportsShouldProcess=$true)]
param()
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$targets=New-Object 'System.Collections.Generic.List[string]'
function Add-Target([string]$Relative) {
    $path=Join-Path $root $Relative
    if(Test-Path -LiteralPath $path) { $targets.Add($path) }
}
function Get-ContentHash([string]$Path) {
    $sha=[Security.Cryptography.SHA256]::Create(); $stream=[IO.File]::OpenRead($Path)
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') }
    finally { $stream.Dispose(); $sha.Dispose() }
}
# Reproducible caches, logs, obsolete root project settings and old snapshots.
foreach($relative in @('.vs','build\csharp-obj','build\gui-obj','build\dma-driver-obj',
    'build\dma-driver-command-fix-obj','logs','90HXWindowsUnlock.vcxproj.user',
    '90HXWindowsUnlock.vcxproj.filters','full-before.json','full-unlock.json','full-after-enable.json',
    'build\fast-optimization-baseline-20261003-024345','build\research')) {
    Add-Target $relative
}
# Remove the old fix output only after preserving an identical canonical driver.
$fixDriver=Join-Path $root 'build\dma-driver-command-fix\CMP90HXDma.sys'
$signedDriver=Join-Path $root 'build\dma-driver\CMP90HXDmaSigned.sys'
if((Test-Path -LiteralPath $fixDriver) -and (Test-Path -LiteralPath $signedDriver)) {
    if((Get-ContentHash $fixDriver) -eq (Get-ContentHash $signedDriver)) {
        Add-Target 'build\dma-driver-command-fix'
    } else {
        Write-Host 'Keeping driver fix output: its driver differs from the canonical signed driver.'
    }
}
$dist=Join-Path $root 'dist'
if(Test-Path -LiteralPath $dist) {
    $releases=@(Get-ChildItem -LiteralPath $dist -Directory | Where-Object {
        $_.Name -match '^CMP90HX-Control-\d+\.\d+\.\d+-\d{8}-\d{6}$'
    } | Sort-Object { $_.Name.Substring($_.Name.Length-15) } -Descending)
    if($releases.Count) {
        $keep=$releases[0].Name
        Write-Host "Keeping latest release: $keep"
        foreach($entry in Get-ChildItem -LiteralPath $dist -Force) {
            if($entry.Name -match '^CMP90HX-Control-\d+\.\d+\.\d+-\d{8}-\d{6}(-signed-\d{8}-\d{6})?(\.zip(\.sha256)?)?$' -and
                !$entry.Name.StartsWith($keep,[StringComparison]::OrdinalIgnoreCase)) {
                $targets.Add($entry.FullName)
            }
        }
    }
}
$build=Join-Path $root 'build'
if(Test-Path -LiteralPath $build) {
    foreach($entry in Get-ChildItem -LiteralPath $build -File) {
        if($entry.Extension -in @('.log','.obj','.pdb') -or $entry.Name -in @(
            'NativeWorkflowTests.exe','DesktopLifecycleTests.exe','hardware_test.exe','state_test.exe')) {
            $targets.Add($entry.FullName)
        }
    }
}
# Delete only the known retired driver binaries; preserve unknown local inputs.
$legacy=@{
    'WinRing0x64.sys'='11BD2C9F9E2397C9A16E0990E4ED2CF0679498FE0FD418A3DFDAC60B5C160EE5'
    'ThrottleStop.sys'='16F83F056177C4EC24C7E99D01CA9D9D6713BD0497EEEDB777A3FFEFA99C97F0'
}
foreach($name in $legacy.Keys) {
    $path=Join-Path $root ('drivers\'+$name)
    if(Test-Path -LiteralPath $path) {
        $hash=Get-ContentHash $path
        if($hash -eq $legacy[$name]) { $targets.Add($path) }
    }
}
$targets=@($targets | Select-Object -Unique)
$bytes=0L; $count=0
# Verify the entire deletion plan before changing any files. Never follow links.
foreach($target in $targets) {
    $resolved=(Resolve-Path -LiteralPath $target).ProviderPath
    if(!$resolved.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase) -or
        $resolved -eq (Join-Path $root '.git') -or
        $resolved.StartsWith((Join-Path $root '.git')+'\',[StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe cleanup target: $resolved"
    }
    $entry=Get-Item -LiteralPath $resolved -Force
    if($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked target refused: $resolved" }
    if($entry.PSIsContainer) {
        if(@(Get-ChildItem -LiteralPath $resolved -Recurse -Force -Attributes ReparsePoint).Count) {
            throw "Directory contains links: $resolved"
        }
        $files=@(Get-ChildItem -LiteralPath $resolved -Recurse -File -Force)
        $count+=$files.Count; $bytes+=($files | Measure-Object Length -Sum).Sum
    } else { $count++; $bytes+=$entry.Length }
}
Write-Host ('Cleanup plan: {0} files, {1:N2} MiB. Canonical signed drivers, certificates and core dependencies are preserved.' -f $count,($bytes/1MB))
foreach($target in $targets) {
    if($PSCmdlet.ShouldProcess($target,'Remove generated or obsolete project files')) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}
