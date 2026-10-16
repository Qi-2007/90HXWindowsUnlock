[CmdletBinding()]
param([string]$BuildDirectory)
$ErrorActionPreference='Stop'
if(!$BuildDirectory){$BuildDirectory=Join-Path $PSScriptRoot '..\build\console-1.3.2'}
if($PSVersionTable.PSEdition -eq 'Core') {
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -BuildDirectory $BuildDirectory
    if($LASTEXITCODE -ne 0){throw 'Backend upgrade tests failed.'};return
}
$root=Split-Path -Parent $PSScriptRoot;$output=[IO.Path]::GetFullPath($BuildDirectory)
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fixture=Join-Path $output 'LegacyBackendFixture.exe'
$sources=@('tools\LegacyBackendFixture.cs','shared\ControlIpc.cs','shared\PowerDtos.cs','ui\SharedSynchronization.cs')|ForEach-Object{Join-Path $root $_}
& $compiler /nologo /target:exe /r:System.Web.Extensions.dll /r:System.Core.dll "/out:$fixture" $sources
if($LASTEXITCODE -ne 0){throw 'Fixture compilation failed.'}
$probe=Join-Path $output 'BackendUpgradeProbe.exe'
& $compiler /nologo /target:exe /r:System.Web.Extensions.dll "/out:$probe" (Join-Path $PSScriptRoot 'BackendUpgradeProbe.cs')
if($LASTEXITCODE -ne 0){throw 'Probe compilation failed.'}
$prior=$env:CMP90HX_IPC_DIAGNOSTIC;$env:CMP90HX_IPC_DIAGNOSTIC='1'
$front=[Reflection.Assembly]::LoadFile((Join-Path $output 'CMP90HXControl.exe'))
$client=$front.GetType('CMP90HX.Control.BackendClient',$true);$flags=[Reflection.BindingFlags]'Static,NonPublic'
$marker=Join-Path $output ('upgrade-test-'+[Guid]::NewGuid().ToString('N'))
$old=$null;$new=$null
try {
    $old=Start-Process -FilePath $fixture -ArgumentList ('"'+$marker+'"') -WindowStyle Hidden -PassThru
    $memoryType=$front.GetType('CMP90HX.Control.ControlMemory',$true);$map=$null
    $wait=[Diagnostics.Stopwatch]::StartNew();$state=$null
    while($wait.ElapsedMilliseconds -lt 10000) {
        try {
            $map=$memoryType.GetConstructors([Reflection.BindingFlags]'Instance,NonPublic')[0].Invoke(@($false,$false))
            $state=$memoryType.GetMethod('State',[Reflection.BindingFlags]'Instance,NonPublic').Invoke($map,@())
            if($state -and $state.Pid -eq $old.Id){break}
        }catch{}
        if($map){$map.Dispose();$map=$null};Start-Sleep -Milliseconds 50
    }
    if(!$state -or $state.Pid -ne $old.Id){throw 'Mock old backend was not ready.'}
    $map.Dispose();$map=$null
    $probeChild=Start-Process -FilePath $probe -WindowStyle Hidden -PassThru
    try {if(!$probeChild.WaitForExit(20000) -or $probeChild.ExitCode -ne 0){throw 'Upgrade probe failed.'}}finally{$probeChild.Dispose()}
    $state=$client.GetMethod('Read',$flags).Invoke($null,@())
    if(!$old.HasExited -or !(Test-Path -LiteralPath ($marker+'.exited'))){throw 'Replacement started before old process exited.'}
    $revision=$memoryType.GetField('BackendRevision',$flags).GetRawConstantValue()
    if($state.Pid -eq $old.Id -or $state.BackendRevision -ne $revision){throw 'New backend was not started.'}
    if([IO.File]::ReadAllText($marker) -ne 'Shutdown'){throw 'Old backend did not receive Shutdown.'}
    $new=Get-Process -Id $state.Pid
    # Repeated startup must reuse the new instance.
    $client.GetMethod('Ensure',$flags).Invoke($null,@($false))
    $again=$client.GetMethod('Read',$flags).Invoke($null,@())
    if($again.Instance -ne $state.Instance){throw 'Current backend was restarted unnecessarily.'}
    $result=$client.GetMethod('Execute',$flags).Invoke($null,@('StopDiagnostic',$null,$false,$null))
    if($result.Code -ne 0 -or !$new.WaitForExit(5000)){throw 'Diagnostic backend did not stop.'}
    foreach($pair in @(@('CMP90HX.Control.MainWindow','ShortState'),@('CMP90HX.Control.ManagementWindow','ShortSessionState'))) {
        $method=$front.GetType($pair[0],$true).GetMethod($pair[1],$flags)
        $summary=$method.Invoke($null,@(('已安装'+[char]0xB7+'details')))
        if($summary -ne '已安装'){throw 'Installed status was not shortened.'}
    }
    Write-Host 'BACKEND_UPGRADE_TESTS_PASSED: Shutdown during busy state, wait for real exit, replacement launch, instance reuse and short status; diagnostic namespace only.'
} finally {
    if($map){$map.Dispose()}
    if($new -and !$new.HasExited){try{$null=$client.GetMethod('Execute',$flags).Invoke($null,@('Shutdown',$null,$false,$null));$new.WaitForExit(5000)|Out-Null}catch{}}
    if($old -and !$old.HasExited){$old.Kill();$old.WaitForExit()}
    if($old){$old.Dispose()};if($new){$new.Dispose()}
    foreach($file in @($marker,($marker+'.exited'),$fixture,$probe)){if(Test-Path -LiteralPath $file){Remove-Item -LiteralPath $file -Force}}
    $env:CMP90HX_IPC_DIAGNOSTIC=$prior
}
