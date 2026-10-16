[CmdletBinding()]
param([string]$BuildDirectory,[switch]$ValidateTaskXml)
$ErrorActionPreference='Stop'
if(!$BuildDirectory){$BuildDirectory=Join-Path $PSScriptRoot '..\build\console-1.3.1'}
if($PSVersionTable.PSEdition -eq 'Core') {
    $parameters=@('-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath,'-BuildDirectory',$BuildDirectory)
    if($ValidateTaskXml){$parameters+='-ValidateTaskXml'}
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" @parameters
    if($LASTEXITCODE -ne 0){throw 'Backend startup tests failed.'};return
}
$asm=[Reflection.Assembly]::LoadFile([IO.Path]::GetFullPath((Join-Path $BuildDirectory 'CMP90HXBackground.exe')))
$flags=[Reflection.BindingFlags]'Static,NonPublic'
function Check([bool]$value,[string]$reason){if(!$value){throw $reason}}
function Call([string]$type,[string]$method,[object[]]$arguments) {
    for($i=0;$i -lt $arguments.Length;$i++){if($arguments[$i] -is [Management.Automation.PSObject]){$arguments[$i]=$arguments[$i].PSObject.BaseObject}}
    $asm.GetType('CMP90HX.Control.'+$type,$true).GetMethod($method,$flags).Invoke($null,$arguments)
}
$executable='C:\ProgramData\CMP90HX\AutoUnlock\1.3.1-0123456789abcdef\CMP90HXBackground.exe'
$xmlText=Call BackendStartup Xml @($executable)
$xml=[xml]$xmlText;$ns=[Xml.XmlNamespaceManager]::new($xml.NameTable);$ns.AddNamespace('t',$xml.DocumentElement.NamespaceURI)
Check ($xml.SelectNodes('//t:BootTrigger',$ns).Count -eq 1) 'Missing boot trigger'
Check ($xml.SelectNodes('//t:LogonTrigger | //t:EventTrigger',$ns).Count -eq 0) 'Unexpected trigger'
Check ($xml.SelectSingleNode('//t:UserId',$ns).InnerText -eq 'S-1-5-18') 'Task must run as SYSTEM'
Check ($xml.SelectSingleNode('//t:Arguments',$ns).InnerText -eq '--startup') 'Incorrect backend argument'
Check ($xml.SelectSingleNode('//t:Command',$ns).InnerText -eq $executable) 'Incorrect executable'
Check ($xml.SelectSingleNode('//t:ExecutionTimeLimit',$ns).InnerText -eq 'PT0S') 'Resident task has a time limit'
Check ($xml.SelectSingleNode('//t:AllowHardTerminate',$ns).InnerText -eq 'false') 'Task must not forcibly terminate hardware work'
$packageRoot=[IO.Path]::GetFullPath($BuildDirectory)
$valid=Call BackendStartup PackagePath @($packageRoot,'reader/CMP90HXReader.exe')
Check ($valid -eq (Join-Path $packageRoot 'reader\CMP90HXReader.exe')) 'Valid package path rejected'
foreach($relative in @('../outside.exe','reader/../../outside.exe','C:\outside.exe','reader/file:stream','reader//file.exe')) {
    $rejected=$false;try{$null=Call BackendStartup PackagePath @($packageRoot,$relative)}catch{$rejected=$_.Exception.InnerException -is [IO.IOException]}
    Check $rejected ('Unsafe path accepted: '+$relative)
}
Add-Type -AssemblyName System.Web.Extensions
$json=[Web.Script.Serialization.JavaScriptSerializer]::new()
$optionType=$asm.GetType('CMP90HX.Control.ControlOptions',$true)
$options=$json.Deserialize('{"Backend":"Session","UnlockOnStartup":true}',$optionType)
Check (!$options.StartOnBoot -and $options.UnlockOnStartup) 'Legacy settings unexpectedly enabled boot registration'
$options.StartOnBoot=$true;$options.UnlockOnStartup=$false
$roundtrip=$json.Deserialize($json.Serialize($options),$optionType)
Check ($roundtrip.StartOnBoot -and !$roundtrip.UnlockOnStartup) 'Autostart and unlock options are coupled'
$state=[Activator]::CreateInstance($asm.GetType('CMP90HX.Control.ControlState',$true))
$rejected=$false;try{Call BackendClient RequireCurrent @($state)}catch{$rejected=$_.Exception.InnerException -is [IO.IOException]}
Check $rejected 'Previous backend revision was silently accepted'
$state.BackendRevision=$asm.GetType('CMP90HX.Control.ControlMemory',$true).GetField('BackendRevision',$flags).GetRawConstantValue()
Call BackendClient RequireCurrent @($state)
if($ValidateTaskXml) {
    $service=New-Object -ComObject Schedule.Service;$definition=$null
    try{$service.Connect();$definition=$service.NewTask(0);$definition.XmlText=$xmlText}
    finally{if($definition){[Runtime.InteropServices.Marshal]::FinalReleaseComObject($definition)|Out-Null};[Runtime.InteropServices.Marshal]::FinalReleaseComObject($service)|Out-Null}
    Write-Host 'TASK_SCHEDULER_XML_VALIDATION_PASSED (definition only; no task registered)'
}
Write-Host 'BACKEND_STARTUP_TESTS_PASSED: boot policy, independent settings, path boundaries and backend revision; no hardware or startup changes.'
