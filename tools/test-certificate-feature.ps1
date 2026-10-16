[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$BuildDirectory,[switch]$Enabled)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -eq 'Core'){
 $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath,'-BuildDirectory',$BuildDirectory)
 if($Enabled){$arguments+='-Enabled'}
 & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" @arguments
 if($LASTEXITCODE -ne 0){throw 'Certificate feature tests failed.'};return
}
$root=[IO.Path]::GetFullPath($BuildDirectory)
$backend=[Reflection.Assembly]::LoadFile((Join-Path $root 'CMP90HXBackground.exe'))
$frontend=[Reflection.Assembly]::LoadFile((Join-Path $root 'CMP90HXControl.exe'))
$flags=[Reflection.BindingFlags]'Static,NonPublic'
function Check([bool]$condition,[string]$message){if(!$condition){throw $message}}
$support=$backend.GetType('CMP90HX.Control.ControlMemory',$true).GetField('RootCertificateSupport',$flags).GetRawConstantValue()
Check ($support -eq $Enabled.IsPresent) 'Incorrect backend feature flag'
$state=[Activator]::CreateInstance($backend.GetType('CMP90HX.Control.ControlState',$true))
$state.BackendRevision=$backend.GetType('CMP90HX.Control.ControlMemory',$true).GetField('BackendRevision',$flags).GetRawConstantValue()
$require=$backend.GetType('CMP90HX.Control.BackendClient',$true).GetMethod('RequireCurrent',$flags)
$null=$require.Invoke($null,@($state))
$state.RootCertificateSupport=!$support
$rejected=$false;try{$null=$require.Invoke($null,@($state))}catch{$rejected=$_.Exception.InnerException -is [IO.IOException]}
Check $rejected 'Different feature editions were accepted'
$window=$frontend.GetType('CMP90HX.Control.ManagementWindow',$true)
$field=$window.GetField('certificate',[Reflection.BindingFlags]'Instance,NonPublic')
Check (($null -ne $field) -eq $Enabled.IsPresent) 'Certificate UI was compiled into the wrong edition'
Check (($null -ne $frontend.GetType('CMP90HX.Control.CertificateManager')) -eq $Enabled.IsPresent) 'Frontend certificate commands were compiled into the wrong edition'
$manager=$backend.GetType('CMP90HX.Control.CertificateManager',$true)
if(!$Enabled){
 $installed=$manager.GetMethod('Installed',$flags).Invoke($null,@())
 Check ($installed.Length -eq 0) 'Disabled build queried certificates'
 $null=$manager.GetMethod('RequireInstalled',$flags).Invoke($null,@())
 $null=$manager.GetMethod('ValidateFiles',$flags).Invoke($null,@($null))
 $null=$manager.GetMethod('EnsureInstalled',$flags).Invoke($null,@($null,$null))
 $rejected=$false;try{$null=$manager.GetMethod('Install',$flags).Invoke($null,@($null,$null))}catch{$rejected=$_.Exception.InnerException -is [NotSupportedException]}
 Check $rejected 'Disabled build can install certificates'
}
if($Enabled){
 $ensure=$manager.GetMethod('EnsureInstalled',$flags,$null,[Type[]]@([Func[bool]],[Action],[Action[string]]),$null)
 Check ($null -ne $ensure) 'Certificate pre-install policy missing'
 $policy=@{Ready=$true;Installs=0;Checks=0;Logs=0}
 $ready=[Func[bool]]{$policy.Checks++;return $policy.Ready}
 $install=[Action]{$policy.Installs++;$policy.Ready=$true}
 $log=[Action[string]]{param($message)$policy.Logs++}
 $null=$ensure.Invoke($null,@($ready,$install,$log))
 Check ($policy.Installs -eq 0 -and $policy.Checks -eq 1) 'Existing root certificate was reinstalled'
 $policy.Ready=$false;$policy.Checks=0
 $null=$ensure.Invoke($null,@($ready,$install,$log))
 Check ($policy.Installs -eq 1 -and $policy.Ready -and $policy.Checks -eq 2) 'Missing root certificate was not installed and rechecked'
 $policy.Ready=$false
 $failedInstall=[Action]{throw [IO.IOException]::new('Synthetic certificate installation failure')}
 $continued=$false;$rejected=$false
 try {$null=$ensure.Invoke($null,@($ready,$failedInstall,$log));$continued=$true}catch{$rejected=$true}
 Check ($rejected -and !$continued) 'Certificate installation failure allowed the driver operation to continue'
 $noInstall=[Action]{}
 $rejected=$false
 try {$null=$ensure.Invoke($null,@($ready,$noInstall,$log))}catch{$rejected=$true}
 Check $rejected 'Failed certificate post-install verification was accepted'
}
Check ((Test-Path -LiteralPath (Join-Path $root 'certificates')) -eq $Enabled.IsPresent) 'Incorrect certificate packaging'
Add-Type -AssemblyName System.Web.Extensions
$json=[Web.Script.Serialization.JavaScriptSerializer]::new()
$metadata=$json.DeserializeObject([IO.File]::ReadAllText((Join-Path $root 'compiled-backends.json')))
Check ($metadata['RootCertificateSupport'] -eq $Enabled.IsPresent) 'Missing compiled feature stamp'
$manifest=$json.DeserializeObject([IO.File]::ReadAllText((Join-Path $root 'files.sha256.json')))
Check ((@($manifest.Keys | Where-Object {$_.StartsWith('certificates/')}).Count -gt 0) -eq $Enabled.IsPresent) 'Incorrect certificate manifest entries'
Write-Host "CERTIFICATE_FEATURE_TESTS_PASSED enabled=$support; no trust store changes."
