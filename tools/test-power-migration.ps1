[CmdletBinding()]
param([string]$BuildDirectory)
$ErrorActionPreference='Stop'
if(!$BuildDirectory){$BuildDirectory=Join-Path $PSScriptRoot '..\build\console-review'}
if($PSVersionTable.PSEdition -eq 'Core') {
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -BuildDirectory $BuildDirectory
    if($LASTEXITCODE -ne 0){throw 'Power migration tests failed.'};return
}
$assembly=[Reflection.Assembly]::LoadFile([IO.Path]::GetFullPath((Join-Path $BuildDirectory 'CMP90HXBackground.exe')))
$flags=[Reflection.BindingFlags]'Static,NonPublic'
function Call([string]$type,[string]$method,[object[]]$arguments,[Type[]]$signature) {
    $target=$assembly.GetType('CMP90HX.Control.'+$type,$true)
    for($i=0;$i -lt $arguments.Length;$i++){if($arguments[$i] -is [Management.Automation.PSObject]){$arguments[$i]=$arguments[$i].PSObject.BaseObject}}
    $target.GetMethod($method,$flags,$null,$signature,$null).Invoke($null,$arguments)
}
function Check([bool]$condition,[string]$message) {if(!$condition){throw $message}}
$testParent=[IO.Path]::GetFullPath($BuildDirectory).TrimEnd('\')+'\'
$root=Join-Path $testParent ('CMP90HX-migration-test-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory((Join-Path $root 'Power')) | Out-Null
try {
    $signature=[Type[]]@([string],[string])
    Check (Call LegacyPowerMigration IsLegacy @('C:\old\CMP90HXControl.exe','--power-monitor') $signature) 'Old monitor was not recognized'
    foreach($pair in @(@('C:\old\CMP90HXControl.exe','--auto-unlock'),@('C:\old\CMP90HXControl.exe','--power-monitor --startup'),@('C:\old\nvidiaInspector.exe','--power-monitor'),@('C:\old\CMP90HXBackground.exe','--power-monitor'))) {
        Check (!(Call LegacyPowerMigration IsLegacy $pair $signature)) 'Unrelated process matched migration'
    }
    [IO.File]::WriteAllText((Join-Path $root 'Power\settings.json'),'{"Enabled":true,"Threshold":27,"IdleSeconds":12,"FullSpeedApps":"render.exe"}')
    [IO.File]::WriteAllText((Join-Path $root 'settings.json'),'{"Control":{"Backend":"Session"},"FutureFeature":42}')
    $settings=Call PowerStorage ReadSettings @($root) ([Type[]]@([string]))
    Check ($settings.Enabled -and $settings.Threshold -eq 27 -and $settings.FullSpeedApps -eq 'render.exe') 'Control section hid legacy power settings'
    $powerType=$assembly.GetType('CMP90HX.Control.PowerSettings',$true)
    Call PowerStorage WriteSettings @($root,$settings) ([Type[]]@([string],$powerType))
    $merged=Get-Content -LiteralPath (Join-Path $root 'settings.json') -Raw | ConvertFrom-Json
    Check ($merged.Control.Backend -eq 'Session' -and $merged.FutureFeature -eq 42 -and $merged.Power.Enabled) 'Migration lost unrelated settings'
    # Simulate a crash after the old controller has been disabled. The durable
    # journal must retain the original enabled state and all policy settings.
    $journalType=$assembly.GetType('CMP90HX.Control.LegacyPowerMigration+Journal',$true)
    $journal=[Activator]::CreateInstance($journalType,$true)
    $journal.Settings=$settings;$journal.Directories=[Collections.Generic.List[string]]::new()
    Call PowerStorage Write @($root,'migration.json',$journal) ([Type[]]@([string],[string],[object]))
    $disabled=[Activator]::CreateInstance($powerType);$disabled.Enabled=$false
    Call PowerStorage WriteSettings @($root,$disabled) ([Type[]]@([string],$powerType))
    Add-Type -AssemblyName System.Web.Extensions
    $serializer=[Web.Script.Serialization.JavaScriptSerializer]::new()
    $restored=$serializer.Deserialize([IO.File]::ReadAllText((Join-Path $root 'migration.json')),$journalType)
    Check ($restored.Settings.Enabled -and $restored.Settings.Threshold -eq 27 -and $restored.Settings.IdleSeconds -eq 12 -and $restored.Settings.FullSpeedApps -eq 'render.exe') 'Interrupted migration lost the saved policy'
    Call PowerStorage WriteSettings @($root,$restored.Settings) ([Type[]]@([string],$powerType))
    [IO.Directory]::CreateDirectory((Join-Path $root 'Power')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $root 'Power\settings.json'),'{"Enabled":false,"Threshold":3}')
    $settings=Call PowerStorage ReadSettings @($root) ([Type[]]@([string]))
    Check ($settings.Enabled -and $settings.Threshold -eq 27) 'Legacy settings overwrote newer unified settings'
    [IO.File]::WriteAllText((Join-Path $root 'Power\status.json'),'{"LimitApplied":true}')
    $status=Call PowerStorage ReadStatus @($root) ([Type[]]@([string]))
    [IO.Directory]::CreateDirectory((Join-Path $root 'State')) | Out-Null
    Call PowerStorage WriteStatus @($root,$status) ([Type[]]@([string],$assembly.GetType('CMP90HX.Control.PowerStatus',$true)))
    Check ((Call PowerStorage ReadStatus @($root) ([Type[]]@([string]))).LimitApplied) 'Recovery intent was lost'
    $runtime=Join-Path $root 'AutoUnlock'
    $candidate=Join-Path $runtime '1.2.2-0123456789abcdef';$unrelated=Join-Path $runtime '1.2.3-0123456789abcdef'
    foreach($path in @($candidate,$unrelated)){[IO.Directory]::CreateDirectory($path) | Out-Null}
    $log=[Action[string]]{param($line)}
    $cleanSignature=[Type[]]@([string],[Collections.Generic.IEnumerable[string]],[Action[string]],[Collections.Generic.IEnumerable[string]])
    Call RuntimeDeployment Clean @($runtime,[string[]]@((Join-Path $candidate 'CMP90HXControl.exe')),$log,[string[]]@($candidate)) $cleanSignature
    Check (Test-Path -LiteralPath $candidate) 'Referenced runtime was deleted'
    Call RuntimeDeployment Clean @($runtime,[string[]]@(),$log,[string[]]@($candidate)) $cleanSignature
    Check (!(Test-Path -LiteralPath $candidate) -and (Test-Path -LiteralPath $unrelated)) 'Cleanup exceeded the identified runtime scope'
    Write-Host 'POWER_MIGRATION_TESTS_PASSED (temporary files only; no tasks, processes or GPU changed)'
} finally {
    if([IO.Path]::GetFullPath($root).StartsWith($testParent,[StringComparison]::OrdinalIgnoreCase)) {Remove-Item -LiteralPath $root -Recurse -Force}
}
