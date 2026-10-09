[CmdletBinding()]
param([string]$MSBuild,[string]$DriverPath,[string]$OutputDirectory,[string]$InputDirectory)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'BuildCommon.ps1')
$root=Get-BuildRoot
$backendRoot=[IO.Path]::GetFullPath((Join-Path $root '..\90HXWinNativeUnlock'))
$package=Join-Path $root 'build\rebuilt-core\gui'
if($OutputDirectory) { $package=[IO.Path]::GetFullPath($OutputDirectory) }
$temp=Join-Path $root '.build\rebuilt-gui\ui'
$input=Join-Path $backendRoot 'build\rebuilt-core\dma-driver'
if($InputDirectory) { $input=[IO.Path]::GetFullPath($InputDirectory) }
$state=Get-Content (Join-Path $input 'package-manifest.json') -Raw | ConvertFrom-Json
$worker=Join-Path $input 'CMP90HXUnlocker.exe'
$core=Join-Path $input 'nvpermissive-rebuilt-hybrid.o'
$originalDriver=Join-Path $input 'CMP90HXDma.sys'
$driver=$originalDriver
if($DriverPath) {
    $driver=[IO.Path]::GetFullPath($DriverPath)
    if((Get-AuthenticodeSignature $driver).Status -ne 'Valid') { throw 'Specified research driver signature is not valid' }
    if((Get-BuildPayloadHash $driver) -ne (Get-BuildPayloadHash $originalDriver)) { throw 'Signed driver body does not match the tested research build' }
}
foreach($pair in @(@($worker,$state.WorkerHash),@($core,$state.CoreHash))) {
    if((Get-FileHash $pair[0]).Hash -ne $pair[1]) { throw 'Research package validation stamp mismatch' }
}
Assert-DriverCallerPolicy $driver ((Get-FileHash $worker).Hash)
New-Item -ItemType Directory -Path $temp,$package -Force | Out-Null
Copy-Item (Join-Path $root 'ui\*') -Destination $temp -Recurse -Force
$native=Join-Path $temp 'NativeWorkflow.cs'
$text=[IO.File]::ReadAllText($native)
foreach($name in @('Gen2Hash','DmaHash','CoreHash')) {
    $path=if($name -eq 'Gen2Hash') {$worker} elseif($name -eq 'DmaHash') {$driver} else {$core}
    $pattern='internal const string '+$name+'="[0-9A-F]{64}";'
    if(![regex]::IsMatch($text,$pattern)) { throw "GUI pin layout changed: $name" }
    $text=[regex]::Replace($text,$pattern,('internal const string '+$name+'="'+(Get-FileHash $path).Hash+'";'))
}
[IO.File]::WriteAllText($native,$text,(New-Object Text.UTF8Encoding($false)))
$window=Join-Path $temp 'MainWindow.xaml'
$sourceManifest=Join-Path $input 'source-manifest.json'
$sourceV5=(Test-Path -LiteralPath $sourceManifest) -and ((Get-Content -LiteralPath $sourceManifest -Raw | ConvertFrom-Json).Kind -eq 'ga102-source-v5')
$sourceVersion=if($sourceV5 -and (Get-Content -LiteralPath $sourceManifest -Raw | ConvertFrom-Json).Version){(Get-Content -LiteralPath $sourceManifest -Raw | ConvertFrom-Json).Version}else{'5'}
$title=if($sourceV5){'90HX 源码核心 v'+$sourceVersion+' 控制台'}else{'90HX 混合核心实验控制台'}
$text=[IO.File]::ReadAllText($window).Replace('Title="90HX Windows Unlock 控制台"','Title="'+$title+'"')
[IO.File]::WriteAllText($window,$text,(New-Object Text.UTF8Encoding($false)))
if($sourceV5) {
    $mainWindow=Join-Path $temp 'MainWindow.xaml.cs'
    $text=[IO.File]::ReadAllText($mainWindow).Replace(' · 核心 469dc0c',' · 源码核心 v'+$sourceVersion)
    [IO.File]::WriteAllText($mainWindow,$text,(New-Object Text.UTF8Encoding($false)))
    $text=[IO.File]::ReadAllText($native).Replace('GUI_UNLOCK_CORE=469dc0c','GUI_UNLOCK_CORE=GA102-source-v'+$sourceVersion)
    [IO.File]::WriteAllText($native,$text,(New-Object Text.UTF8Encoding($false)))
}
$cert=Get-BuildCertificate
$certSource=Join-Path (Split-Path -Parent $temp) 'CertificatePolicy.cs'
$lines=@('namespace CMP90HX.Control { internal static class CertificatePolicy {')
foreach($name in @('FileName','Hash','Thumbprint','DisplayName','SourceDirectory')) {
    $lines+='internal const string '+$name+' = '+(ConvertTo-Json -InputObject ([string]$cert.$name) -Compress)+';'
}
$lines+='} }'
[IO.File]::WriteAllText($certSource,($lines -join "`r`n"),[Text.Encoding]::UTF8)
$project=Join-Path $temp 'CMP90HXUnlockConsole.csproj'
[xml]$xml=Get-Content $project -Raw
$ns=New-Object Xml.XmlNamespaceManager($xml.NameTable)
$ns.AddNamespace('m','http://schemas.microsoft.com/developer/msbuild/2003')
$node=$xml.SelectSingleNode('//m:Target[@Name="GenerateCertificatePolicy"]',$ns)
if(!$node) { throw 'GUI certificate generation target changed' }
[void]$node.ParentNode.RemoveChild($node)
$node=$xml.SelectSingleNode('//m:Compile[@Include="..\build\CertificatePolicy.cs"]',$ns)
if(!$node) { throw 'GUI certificate source layout changed' }
$node.SetAttribute('Include',$certSource)
$xml.Save($project)
$MSBuild=Resolve-BuildMSBuild $MSBuild
Assert-BuildFramework -Gui
& $MSBuild $project /t:Rebuild /m /p:Configuration=Release /p:Platform=x64 "/p:OutputPath=$package\" "/p:IntermediateOutputPath=$temp\obj\" /verbosity:minimal
if($LASTEXITCODE -ne 0) { throw 'Hybrid GUI compilation failed' }
foreach($name in @('runtime','core','driver\cert')) { New-Item -ItemType Directory -Path (Join-Path $package $name) -Force | Out-Null }
Copy-Item $worker (Join-Path $package 'runtime\CMP90HXUnlocker.exe') -Force
$workerConfig=Join-Path $backendRoot 'build\rebuilt-core\worker-hardware\CMP90HXUnlocker.exe.config'
Copy-Item $workerConfig (Join-Path $package 'runtime\CMP90HXUnlocker.exe.config') -Force
Copy-Item $core (Join-Path $package 'core\nvpermissive-core.o') -Force
$targetDriver=Join-Path $package 'driver\CMP90HXDma.sys'
if([IO.Path]::GetFullPath($driver) -ine [IO.Path]::GetFullPath($targetDriver)) {
    Copy-Item $driver $targetDriver -Force
}
Copy-Item $cert.Path (Join-Path $package ('driver\cert\'+$cert.FileName)) -Force
$files=@{}
Get-ChildItem $package -File -Recurse | Where-Object Name -NE 'files.sha256.json' | ForEach-Object {
    $relative=$_.FullName.Substring($package.Length+1).Replace('\','/')
    $files[$relative]=(Get-FileHash $_.FullName).Hash
}
$files | ConvertTo-Json | Set-Content (Join-Path $package 'files.sha256.json') -Encoding utf8
# Existing workflow simulations use the patched GUI pins and certificate policy.
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$test=Join-Path $root 'build\rebuilt-core\HybridGuiWorkflowTests.exe'
$names=@('NativePnp.cs','DriverService.cs','NativeWorkflow.cs','UnlockSnapshot.cs','CertificateManager.cs','LogSession.cs','TaskManagement.cs','NvidiaPowerApi.cs','PowerControl.cs','PowerTasks.cs','SharedSynchronization.cs','RuntimeDeployment.cs')
$sources=@($names | ForEach-Object {Join-Path $temp $_})+@($certSource,(Join-Path $root 'tools\NativeWorkflowTests.cs'))
& $csc /nologo /target:exe /platform:x64 /warnaserror+ "/out:$test" /reference:System.Web.Extensions.dll /reference:Microsoft.CSharp.dll $sources
if($LASTEXITCODE -ne 0) { throw 'GUI workflow tests did not compile' }
$output=& $test 2>&1
$exit=$LASTEXITCODE
$output | Set-Content (Join-Path $root 'build\rebuilt-core\gui-workflow-tests.log') -Encoding utf8
if($exit -ne 0) {$output | Out-Host;throw 'GUI workflow simulations failed'}
$output | Select-Object -Last 1 | Out-Host
Write-Host "Hybrid GUI ready: $package (driver signature status: $((Get-AuthenticodeSignature $driver).Status))"
