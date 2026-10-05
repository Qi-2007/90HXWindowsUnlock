[CmdletBinding()]
param([switch]$ReadMmio, [switch]$ReadRtFuse, [switch]$ReadNvapi, [switch]$ReadFeatureReadout, [string]$OutputDirectory)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'ConsoleEncoding.ps1')
if ($ReadRtFuse -and !$ReadMmio) { throw '-ReadRtFuse requires -ReadMmio.' }
if ($ReadFeatureReadout -and !$ReadMmio) { throw '-ReadFeatureReadout requires -ReadMmio.' }
if ([IntPtr]::Size -ne 8) { throw 'Use x64 PowerShell.' }
if (!$OutputDirectory) {
    $OutputDirectory=Join-Path $PSScriptRoot ('..\logs\rt-probe-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$rtCompiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$rtExe=Join-Path $OutputDirectory 'ReadOnlyRtProbe.exe'
& $rtCompiler /nologo /platform:x64 "/out:$rtExe" (Join-Path $PSScriptRoot 'ReadOnlyRtProbe.cs') (Join-Path $PSScriptRoot 'ReadOnlyNvapi.cs')
if ($LASTEXITCODE -ne 0) { throw "Probe compilation failed: $LASTEXITCODE" }
$rtArgs=@()
if ($ReadMmio) { $rtArgs+= '--read-mmio' }
if ($ReadRtFuse) { $rtArgs+= '--read-rt-fuse' }
if ($ReadNvapi) { $rtArgs+= '--read-nvapi' }
if ($ReadFeatureReadout) { $rtArgs+= '--read-feature-readout' }
# Capture native stderr as text: Windows PowerShell 5.1 otherwise converts it
# into terminating NativeCommandError under ErrorActionPreference=Stop.
# Start both reads before waiting so either output stream can exceed its buffer.
$rtStart=New-Object System.Diagnostics.ProcessStartInfo
$rtStart.FileName=$rtExe
$rtStart.Arguments=($rtArgs -join ' ')
$rtStart.UseShellExecute=$false
$rtStart.CreateNoWindow=$true
$rtStart.RedirectStandardOutput=$true
$rtStart.RedirectStandardError=$true
$rtStart.StandardOutputEncoding=[Text.Encoding]::UTF8
$rtStart.StandardErrorEncoding=[Text.Encoding]::UTF8
$rtProcess=New-Object System.Diagnostics.Process
$rtProcess.StartInfo=$rtStart
try {
    if (!$rtProcess.Start()) { throw 'Unable to start probe.' }
    $rtStdoutTask=$rtProcess.StandardOutput.ReadToEndAsync()
    $rtStderrTask=$rtProcess.StandardError.ReadToEndAsync()
    $rtProcess.WaitForExit()
    $rtStdout=$rtStdoutTask.GetAwaiter().GetResult()
    $rtStderr=$rtStderrTask.GetAwaiter().GetResult()
    $rtExit=$rtProcess.ExitCode
} finally { $rtProcess.Dispose() }
$rtStdout | Set-Content -LiteralPath (Join-Path $OutputDirectory 'stdout.txt') -Encoding utf8
$rtStderr | Set-Content -LiteralPath (Join-Path $OutputDirectory 'stderr.txt') -Encoding utf8
$rtOutput=@($rtStdout,$rtStderr,"ExitCode=$rtExit")
$rtOutput | Set-Content -LiteralPath (Join-Path $OutputDirectory 'probe.txt') -Encoding utf8
$rtOutput | ForEach-Object { Write-Host $_ }
Get-CimInstance Win32_PnPSignedDriver |
    Where-Object DeviceID -like 'PCI\VEN_10DE&DEV_220D*' |
    Select-Object DeviceName,DriverVersion,InfName,DeviceID |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'pnp-driver.json') -Encoding utf8
Write-Host "Saved to $OutputDirectory"
if ($rtExit -ne 0) { throw "Probe exited with $rtExit; see probe.txt (failure is not a zero register value)." }
