[CmdletBinding()]
param([string]$BuildDirectory)
$ErrorActionPreference='Stop'
if(!$BuildDirectory){$BuildDirectory=Join-Path (Split-Path -Parent $PSScriptRoot) 'build'}
if($PSVersionTable.PSEdition -eq 'Core') {
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -BuildDirectory $BuildDirectory
    exit $LASTEXITCODE
}
$previousDiagnostic=$env:CMP90HX_IPC_DIAGNOSTIC
$env:CMP90HX_IPC_DIAGNOSTIC='1'
$root=Split-Path -Parent $PSScriptRoot
if(!$BuildDirectory){$BuildDirectory=Join-Path $root 'build'}
$exe=[IO.Path]::GetFullPath((Join-Path $BuildDirectory 'CMP90HXBackground.exe'))
# Diagnostic servers use a per-user Local namespace and reject hardware/config
# commands, so they can be tested while a production Global backend is running.
$assembly=[Reflection.Assembly]::LoadFile($exe)
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$type=$assembly.GetType('CMP90HX.Control.ControlMemory',$true)
$map=$null;$server=$null
function State { $type.GetMethod('State',$flags).Invoke($script:map,@()) }
function Child([string]$arguments) {
    $info=[Diagnostics.ProcessStartInfo]::new($exe,$arguments);$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $process=[Diagnostics.Process]::Start($info)
    [pscustomobject]@{Process=$process;Output=$process.StandardOutput.ReadToEndAsync();Error=$process.StandardError.ReadToEndAsync()}
}
try {
    $server=Start-Process -FilePath $exe -ArgumentList '--diagnostics' -WindowStyle Hidden -PassThru
    $wait=[Diagnostics.Stopwatch]::StartNew()
    while($wait.ElapsedMilliseconds -lt 10000) {
        try{$map=$type.GetConstructors($flags)[0].Invoke(@($false,$false));$initial=State;if($initial){break}}catch{}
        Start-Sleep -Milliseconds 100
    }
    if(!$map -or !$initial -or $initial.Pid -ne $server.Id){throw 'Diagnostic server startup failed.'}
    $duplicate=Child '--diagnostics'; $duplicate.Process.WaitForExit();if($duplicate.Process.ExitCode -ne 0){throw $duplicate.Error.Result}
    $requests=@();for($i=0;$i -lt 8;$i++){$requests+=Child "--request Echo --data ipc-$i"}
    for($i=0;$i -lt $requests.Count;$i++) {
        $child=$requests[$i];$child.Process.WaitForExit();$output=$child.Output.Result
        if($child.Process.ExitCode -ne 0 -or !$output.Contains("ipc-$i")){throw "IPC request $i failed: $($child.Error.Result) $output"}
        $child.Process.Dispose()
    }
    $after=State;if($after.Pid -ne $server.Id -or $after.Instance -ne $initial.Instance){throw 'Backend singleton changed.'}
    # Exercise the exact frontend assembly's client against the real shared mapping.
    $front=[Reflection.Assembly]::LoadFile(([IO.Path]::GetFullPath((Join-Path $BuildDirectory 'CMP90HXControl.exe'))))
    foreach($name in @('NativePnp','WindowsWorkflowPlatform','NativeWorkflow','NvidiaPowerApi','PowerMonitor')) {
        if($front.GetType("CMP90HX.Control.$name",$false)){throw "Frontend still contains hardware implementation: $name"}
    }
    $client=$front.GetType('CMP90HX.Control.BackendClient',$true)
    $frontendState=$client.GetMethod('Read',[Reflection.BindingFlags]'Static,NonPublic').Invoke($null,@())
    if($frontendState.Pid -ne $server.Id){throw 'Frontend failed to attach to existing backend.'}
    $refresh=$client.GetMethod('Execute',[Reflection.BindingFlags]'Static,NonPublic').Invoke($null,@('RefreshDevices',$null,$false,$null))
    if($refresh.Code -ne 0){throw 'Device metadata refresh request failed.'}
    $refreshed=$client.GetMethod('Read',[Reflection.BindingFlags]'Static,NonPublic').Invoke($null,@())
    if($refreshed.Operation -ne 'RefreshDevices' -or $refreshed.Snapshot -ne $frontendState.Snapshot){throw 'Device refresh unexpectedly changed unlock status.'}
    $stop=Child '--request StopDiagnostic';$stop.Process.WaitForExit();if($stop.Process.ExitCode -ne 0){throw $stop.Error.Result}
    if(!$server.WaitForExit(5000)){throw 'Diagnostic server did not stop gracefully.'}
    'REAL_IPC_PASSED: 8 concurrent clients, single backend PID, frontend attaches, no GPU implementation in frontend, graceful exit; no hardware/configuration/task changes.'
} finally {
    if($map){$map.Dispose()}
    if($server -and !$server.HasExited){$last=Child '--request StopDiagnostic';$last.Process.WaitForExit(5000)|Out-Null}
    if($server){$server.Dispose()}
    $env:CMP90HX_IPC_DIAGNOSTIC=$previousDiagnostic
}
