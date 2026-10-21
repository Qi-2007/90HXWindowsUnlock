[CmdletBinding()]
param([string]$BuildDirectory)
$ErrorActionPreference='Stop'
if(!$BuildDirectory){$BuildDirectory=Join-Path $PSScriptRoot '..\build'}
if($PSVersionTable.PSEdition -eq 'Core') {
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -BuildDirectory $BuildDirectory
    if($LASTEXITCODE -ne 0){throw 'Power storage tests failed.'};return
}
$assembly=[Reflection.Assembly]::LoadFile([IO.Path]::GetFullPath((Join-Path $BuildDirectory 'CMP90HXBackground.exe')))
$storage=$assembly.GetType('CMP90HX.Control.PowerStorage',$true)
$statusType=$assembly.GetType('CMP90HX.Control.PowerStatus',$true)
$flags=[Reflection.BindingFlags]'Static,NonPublic'
$read=$storage.GetMethod('ReadStatus',$flags,$null,[Type[]]@([string],[Action[string]]),$null)
$write=$storage.GetMethod('WriteStatus',$flags,$null,[Type[]]@([string],$statusType),$null)
$testParent=[IO.Path]::GetFullPath($BuildDirectory).TrimEnd('\')+'\'
$root=Join-Path $testParent ('power-storage-test-'+[Guid]::NewGuid().ToString('N'))
$state=Join-Path $root 'State';$legacy=Join-Path $root 'Power'
$file=Join-Path $state 'power-status.json';$oldFile=Join-Path $legacy 'status.json'
$messages=[Collections.Generic.List[string]]::new()
$logger=[Action[string]]{param($line) $messages.Add($line)}
$script:count=0
function Check([bool]$condition,[string]$message) {
    if(!$condition){throw $message};$script:count++;Write-Host ('PASS '+$message)
}
function Invoke-Storage($method,[object[]]$arguments) {
    for($i=0;$i -lt $arguments.Length;$i++) {
        if($arguments[$i] -is [Management.Automation.PSObject]){$arguments[$i]=$arguments[$i].PSObject.BaseObject}
    }
    $method.Invoke($null,$arguments)
}
function Read-Status {Invoke-Storage $read @($root,$logger)}
[IO.Directory]::CreateDirectory($state) | Out-Null
[IO.Directory]::CreateDirectory($legacy) | Out-Null
try {
    Check ($null -eq (Read-Status)) 'Missing cache needs no recovery'
    [IO.File]::WriteAllText($oldFile,'{"LimitApplied":true,"Policy":"legacy"}')
    Check ((Read-Status).Policy -eq 'legacy') 'Legacy P8 ownership survives migration'
    [IO.File]::WriteAllText($file,'{"LimitApplied":false,"Policy":"current"}')
    Check (!(Read-Status).LimitApplied -and (Read-Status).Policy -eq 'current') 'Current cache takes precedence over stale legacy ownership'
    $bad=@([string]::new([char]0,292),'',' ',"`r`n`t",'{"LimitApplied":','not JSON','null','[]','{}','{"LimitApplied":"false"}','{"LimitApplied":false,"Sample":{"Gpu":"broken"}}','{"LimitApplied":true,"HeartbeatUtc":"broken"}')
    foreach($text in $bad) {
        [IO.File]::WriteAllText($file,$text)
        $messages.Clear();$status=Read-Status
        Check ($status.LimitApplied -and $messages.Count -eq 1 -and $status.Detail.Contains($file)) 'Corrupt cache keeps recovery pending and logs the affected file'
        Check ([IO.File]::ReadAllText($file) -ceq $text) 'Reading corrupt cache preserves evidence'
    }
    [IO.File]::WriteAllText($file,'{"LimitApplied":false}')
    $locked=[IO.File]::Open($file,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {Check ((Read-Status).LimitApplied) 'Temporarily locked cache does not crash or discard recovery'}
    finally {$locked.Dispose()}
    [IO.File]::Delete($file)
    [IO.File]::WriteAllBytes($oldFile,[byte[]]::new(292))
    Check ((Read-Status).LimitApplied) 'Zero-filled legacy cache also recovers'
    [IO.File]::WriteAllText((Join-Path $root 'settings.json'),'{"FutureFeature":42}')
    $status=[Activator]::CreateInstance($statusType);$status.LimitApplied=$true
    $null=Invoke-Storage $write @($root,$status)
    Check ((Read-Status).LimitApplied -and !(Test-Path -LiteralPath $oldFile)) 'Durable write replaces corrupt legacy state'
    $status.LimitApplied=$false
    $null=Invoke-Storage $write @($root,$status)
    Check (!(Read-Status).LimitApplied) 'Successful recovery can atomically clear ownership'
    Check (@(Get-ChildItem -LiteralPath $state -Filter '*.tmp').Count -eq 0) 'Atomic replacement leaves no temporary file'
    Check ([IO.File]::ReadAllText((Join-Path $root 'settings.json')) -ceq '{"FutureFeature":42}') 'Cache recovery preserves user configuration'
    Write-Host "POWER_STORAGE_TESTS_PASSED count=$script:count (isolated fixtures; no GPU access)"
} finally {
    $resolved=[IO.Path]::GetFullPath($root)
    if(!$resolved.StartsWith($testParent,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe test cleanup path'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
