[CmdletBinding()]
param(
    [Parameter(Position=0)][ValidateSet('Status','Preflight','Unlock','Verify','GpuDmaTest','GpuDmaInitTest')][string]$Mode='Preflight',
    [switch]$ValidateOnly,
    [switch]$Conservative,
    [switch]$LegacyCore,
    [ValidateSet('kernel')][string]$DmaBackend='kernel',
    [switch]$PhysicalDmaExperiment,
    [string]$LogDirectory
)
$ErrorActionPreference='Stop'
if ($Mode -ne 'Status' -and $DmaBackend -eq 'kernel' -and !$PhysicalDmaExperiment) { throw 'Kernel DMA requires -PhysicalDmaExperiment; this driver does not supply IOMMU mappings.' }
$dmaArgs=@('--dma-backend',$DmaBackend)
if ($PhysicalDmaExperiment) { $dmaArgs+='--physical-dma-experiment' }
$root=$PSScriptRoot
$project=$root
$exe=Join-Path $root 'build\CMP90HXGen2.exe'
$drivers=Join-Path $root 'drivers'
$coreVersion=if ($LegacyCore) { '380bdf3' } else { '469dc0c' }
$core=Join-Path $project "vendor\nvpermissive-dist-$coreVersion\obj\nvpermissive-core.o"
$coreHash=if ($LegacyCore) { 'C9702B4887D397272F86DCC25EEA2BB11A46D636C91311D7B71F2FB8B01951E5' } else { 'B533B7B245ED606C151CA336B9A6BACEBE935E3EC478246E879C668A4DD98A6A' }
$logs=if ($LogDirectory) { [IO.Path]::GetFullPath($LogDirectory) } else { Join-Path $root 'logs' }
if ($ValidateOnly) {
    & (Join-Path $root 'build.ps1')
    if ($Mode -ne 'Status' -and !(Test-Path -LiteralPath $core)) { throw 'Pinned core object is missing.' }
    if ($Mode -ne 'Status' -and (Get-FileHash -Algorithm SHA256 $core).Hash -ne $coreHash) { throw 'Core hash mismatch.' }
    Write-Host 'Full-test inputs validated. No firmware-variable read, driver load, PnP change or GPU access performed.'
    exit 0
}
$admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (!$admin) {
    Write-Host "Requesting elevation. Detailed output will be in $logs."
    $childArgs=@('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"",'-Mode',$Mode)
    if ($Conservative) { $childArgs+='-Conservative' }
    if ($LegacyCore) { $childArgs+='-LegacyCore' }
    if ($LogDirectory) { $childArgs+=@('-LogDirectory',"`"$logs`"") }
    $childArgs+=@('-DmaBackend',$DmaBackend)
    if ($PhysicalDmaExperiment) { $childArgs+='-PhysicalDmaExperiment' }
    $child=Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList $childArgs -Wait -PassThru
    if (Test-Path -LiteralPath (Join-Path $logs 'full-script.log')) { Get-Content -LiteralPath (Join-Path $logs 'full-script.log') -Tail 16 }
    exit $child.ExitCode
}
New-Item -ItemType Directory -Path $logs -Force | Out-Null
Start-Transcript -Path (Join-Path $logs 'full-script.log') -Append | Out-Null
$exitCode=1
$mutexName='Global\CMP90HX_FullTestScript'
$mutexRights=[Security.AccessControl.MutexRights]::Synchronize -bor [Security.AccessControl.MutexRights]::Modify
try { $workflowMutex=[Threading.Mutex]::OpenExisting($mutexName,$mutexRights) }
catch [Threading.WaitHandleCannotBeOpenedException] {
    $mutexAcl=[Security.AccessControl.MutexSecurity]::new()
    foreach($sidType in @([Security.Principal.WellKnownSidType]::LocalSystemSid,[Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid)) {
        $sid=[Security.Principal.SecurityIdentifier]::new($sidType,$null)
        $mutexAcl.AddAccessRule([Security.AccessControl.MutexAccessRule]::new($sid,[Security.AccessControl.MutexRights]::FullControl,[Security.AccessControl.AccessControlType]::Allow))
    }
    $mutexCreated=$false
    try {$workflowMutex=[Threading.Mutex]::new($false,$mutexName,[ref]$mutexCreated,$mutexAcl)}
    catch [UnauthorizedAccessException] {$workflowMutex=[Threading.Mutex]::OpenExisting($mutexName,$mutexRights)}
}
$workflowOwned=$false
$workflowTimer=[Diagnostics.Stopwatch]::StartNew()
try {
    try { $workflowOwned=$workflowMutex.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $workflowOwned=$true }
    if (!$workflowOwned) { throw 'Another full-test script is running; refusing concurrent PnP operations.' }
    & (Join-Path $root 'build.ps1') -IfNeeded
    if ($Mode -ne 'Status' -and !(Test-Path -LiteralPath $core)) { throw 'Pinned core object is missing.' }
    if ($Mode -ne 'Status' -and (Get-FileHash -Algorithm SHA256 $core).Hash -ne $coreHash) { throw 'Core hash mismatch.' }
    if ($Mode -ne 'Status') {
    Write-Host "Using pinned core $coreVersion; conservative timing=$($Conservative.IsPresent)."
    & $exe arena-info @dmaArgs --log (Join-Path $logs 'arena.log')
    if ($LASTEXITCODE -ne 0) { throw 'DMA backend validation failed; start CMP90HXDma and cold boot if quarantined.' }
    }
    # Windows can omit a Code 22 device from -PresentOnly after a reboot.
    # Include that disabled device, but reject old phantom devnodes.
    $targets=@(Get-PnpDevice | Where-Object { $_.InstanceId -like 'PCI\VEN_10DE&DEV_220D*' } | Where-Object {
        $problem=(Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_ProblemCode' -ErrorAction SilentlyContinue).Data
        $null -ne $problem -and [uint32]$problem -in @(0,22,43)
    })
    if ($targets.Count -ne 1) { throw "Expected exactly one active/disabled 90HX; found $($targets.Count)." }
    $instance=$targets[0].InstanceId
    $bus=[uint32](Get-PnpDeviceProperty -InstanceId $instance -KeyName 'DEVPKEY_Device_BusNumber').Data
    $address=[uint32](Get-PnpDeviceProperty -InstanceId $instance -KeyName 'DEVPKEY_Device_Address').Data
    $dev=($address -shr 16) -band 65535
    $fn=$address -band 65535
    if ($bus -gt 255 -or $dev -gt 31 -or $fn -gt 7) { throw 'PnP BDF is outside segment-0 PCI access range.' }
    $bdf='{0:x2}:{1:x2}.{2}' -f $bus,$dev,$fn
    Write-Host "Target: $instance / BDF=$bdf"
    if ($Mode -eq 'Status') {
        & $exe snapshot --drivers $drivers --bdf $bdf --out (Join-Path $logs 'unlock-status.json') --log (Join-Path $logs 'unlock-status.log')
        if ($LASTEXITCODE -ne 0) { throw 'Status snapshot unavailable. No GPU writes or PnP changes attempted.' }
        Write-Host 'UNLOCK_STATUS_CAPTURED'
        $exitCode=0
    } elseif ($Mode -eq 'Preflight') {
        & $exe dma-test @dmaArgs --drivers $drivers --log (Join-Path $logs 'dma-test.log')
        if ($LASTEXITCODE -ne 0) { throw 'DMA mapping test failed. No GPU unlock attempted.' }
        & $exe bridge-probe --drivers $drivers --bdf $bdf --out (Join-Path $root 'full-before.json') --log (Join-Path $logs 'full-before.log')
        if ($LASTEXITCODE -ne 0) { throw 'GPU snapshot failed. Preflight is read-only and does not prepare a disabled GPU in D0/Memory Decode. Use the explicit diagnostic mode only if the device is already Code 22.' }
        Write-Host 'PREFLIGHT_PASSED. DMA mapped and physical readback verified. PnP unchanged; no GPU write performed.'
        $exitCode=0
    } elseif ($Mode -eq 'GpuDmaTest' -or $Mode -eq 'GpuDmaInitTest') {
        if ([uint32](Get-PnpDeviceProperty -InstanceId $instance -KeyName 'DEVPKEY_Device_ProblemCode').Data -ne 22) {
            throw "$Mode requires the 90HX already disabled (Code 22). It will not change PnP state."
        }
        if ($Mode -eq 'GpuDmaInitTest') {
            Write-Host 'Explicit GSP-only engine reset before DMA probe. Existing GSP firmware/DMEM state is not restored; no SBR, firmware boot or fuse/PLM writes. Device stays disabled.'
            & $exe gpu-dma-init-test @dmaArgs --core $core --drivers $drivers --bdf $bdf --log (Join-Path $logs 'gpu-dma-init-test.log')
        } else {
            & $exe gpu-dma-test @dmaArgs --drivers $drivers --bdf $bdf --log (Join-Path $logs 'gpu-dma-test.log')
        }
        if ($LASTEXITCODE -ne 0) {
            $probeLog=if ($Mode -eq 'GpuDmaInitTest') { 'gpu-dma-init-test.log' } else { 'gpu-dma-test.log' }
            throw "GPU DMA probe failed or unavailable. See $probeLog. Device stays disabled; no automatic retry. Cold boot if reset/probe restoration is unconfirmed."
        }
        Write-Host 'GPU_DMA_PROBE_PASSED. Device remains disabled. This does not verify firmware unlock or Gen2.'
        $exitCode=0
    } else {
        $wasEnabled=([uint32](Get-PnpDeviceProperty -InstanceId $instance -KeyName 'DEVPKEY_Device_ProblemCode').Data -ne 22)
        if ($Mode -eq 'Unlock') {
            # Save link width before stopping the device. Failure here is fatal
            # while enabled, but an already-disabled GPU can be prepared in D0.
            if ($wasEnabled) {
                & $exe snapshot --drivers $drivers --bdf $bdf --out (Join-Path $root 'full-before.json') --log (Join-Path $logs 'full-before.log')
                if ($LASTEXITCODE -ne 0) { throw 'Pre-stop snapshot failed; no device disabled.' }
            }
            $restoreDevice=$false
            $unlockCode=1
            try {
                if ($wasEnabled) {
                    $restoreDevice=$true
                    Disable-PnpDevice -InstanceId $instance -Confirm:$false
                }
                $disabled=$false
                for ($poll=0;$poll -lt 30;$poll++) {
                    if ([uint32](Get-PnpDeviceProperty -InstanceId $instance -KeyName 'DEVPKEY_Device_ProblemCode').Data -eq 22) { $disabled=$true; break }
                    Start-Sleep -Milliseconds 200
                }
                if (!$disabled) { throw '90HX did not reach disabled Code 22; refusing full unlock.' }
                $unlockArgs=@('full-unlock','--core',$core,'--drivers',$drivers,'--bdf',$bdf,'--out',(Join-Path $root 'full-unlock.json'),'--log',(Join-Path $logs 'full-unlock.log'))
                $unlockArgs+=$dmaArgs
                if ($Conservative) { $unlockArgs+='--conservative' }
                & $exe @unlockArgs
                $unlockCode=$LASTEXITCODE
            } finally {
                # A native process crash can bypass its exit-6 handler. Check
                # persisted DMA ownership before allowing NVIDIA to reattach.
                if ($unlockCode -ne 0) {
                    & $exe arena-info @dmaArgs --log (Join-Path $logs 'arena.log')
                    if ($LASTEXITCODE -ne 0) { $unlockCode=6 }
                }
                if ($unlockCode -eq 6) {
                    Write-Host 'DMA_RETAINED: keep 90HX disabled; cold boot before retrying, then start CMP90HXDma.' -ForegroundColor Red
                } elseif ($restoreDevice) {
                    Write-Host 'Re-enabling the original 90HX PnP device.'
                    Enable-PnpDevice -InstanceId $instance -Confirm:$false
                }
            }
            if ($unlockCode -ne 0) { throw "Full unlock failed with exit $unlockCode. See full-unlock.log and FAILED_STAGE. No automatic retry." }
            if (!$wasEnabled) {
                Write-Host 'FULL_UNLOCK_VERIFIED_WHILE_DISABLED_ONLY. Device was already disabled and remains disabled; enable it manually, then run Verify.'
                $exitCode=0
            }
        }
        if ($Mode -eq 'Verify' -or $wasEnabled) {
            if ($Conservative) { Start-Sleep -Seconds 3 } else { Start-Sleep -Milliseconds 200 }
            $ready=$false
            for ($poll=0;$poll -lt 30;$poll++) {
                $problem=[uint32](Get-PnpDeviceProperty -InstanceId $instance -KeyName 'DEVPKEY_Device_ProblemCode').Data
                if ($problem -eq 0) { $ready=$true; break }
                if ($problem -eq 22 -or $problem -eq 43) { break }
                Start-Sleep -Seconds 1
            }
            if (!$ready) { throw "NVIDIA/PnP did not reattach normally: problem code $problem." }
            $width=1
            if (Test-Path -LiteralPath (Join-Path $root 'full-before.json')) {
                $baseline=Get-Content -Raw -LiteralPath (Join-Path $root 'full-before.json') | ConvertFrom-Json
                if ($baseline.GpuBdf -eq (($bus -shl 8) -bor ($dev -shl 3) -bor $fn)) {
                    $width=([uint32]$baseline.Gpu.Status -shr 4) -band 63
                }
            }
            for ($sample=0;$sample -lt 3;$sample++) {
                & $exe snapshot --drivers $drivers --bdf $bdf --out (Join-Path $root 'full-after-enable.json') --log (Join-Path $logs 'full-after-enable.log')
                if ($LASTEXITCODE -ne 0) { throw 'Post-enable snapshot failed.' }
                $state=Get-Content -Raw -LiteralPath (Join-Path $root 'full-after-enable.json') | ConvertFrom-Json
                foreach ($link in @($state.Gpu,$state.Bridge)) {
                    if (([uint32]$link.Status -band 15) -ne 2 -or (([uint32]$link.Status -shr 4) -band 63) -lt $width -or ([uint32]$link.Status -band 2048) -ne 0) {
                        throw 'Gen2/width verification failed after NVIDIA reattach.'
                    }
                }
                foreach ($expected in @(@{Offset=0x82381c;Value=[uint32]0x88888888L},@{Offset=0x823820;Value=8},@{Offset=0x823830;Value=4})) {
                    $r=@($state.Registers | Where-Object Offset -EQ $expected.Offset)
                    if ($r.Count -ne 1 -or $null -ne $r[0].Error -or [uint32]$r[0].Value -ne $expected.Value) { throw 'Compute/graphics override lost after NVIDIA reattach.' }
                }
                if ($sample -lt 2) { Start-Sleep -Seconds 1 }
            }
            Write-Host 'FULL_UNLOCK_VERIFIED_AFTER_NVIDIA_REATTACH: PnP Code 0, both ends Gen2, width retained, core overrides retained for three samples.'
            $exitCode=0
        }
    }
} catch {
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Logs: $logs"
    $exitCode=1
} finally {
    Write-Host ('WORKFLOW_TIMING mode={0} conservative={1} core={2} elapsed_ms={3}' -f $Mode,$Conservative.IsPresent,$coreVersion,$workflowTimer.ElapsedMilliseconds)
    if ($workflowOwned) { $workflowMutex.ReleaseMutex() }
    $workflowMutex.Dispose()
    Stop-Transcript | Out-Null
}
exit $exitCode

