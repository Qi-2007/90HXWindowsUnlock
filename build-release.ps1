[CmdletBinding()]
param(
    [string]$SignedDriverPath,
    [string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$version='1.2.2'
$releaseInputs=& (Join-Path $PSScriptRoot 'update-release-hashes.ps1') -SignedDriverPath $SignedDriverPath -SkipGuiBuild
$expectedDriver=$releaseInputs.DmaHash
$expectedWorker=$releaseInputs.Gen2Hash
$SignedDriverPath=$releaseInputs.SignedDriverPath
$signature=Get-AuthenticodeSignature -LiteralPath $SignedDriverPath
if ($signature.Status -ne 'Valid') { throw "DMA signature verification failed: $($signature.Status)" }
& (Join-Path $PSScriptRoot 'build-gui.ps1')
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExe=Join-Path $PSScriptRoot 'build\NativeWorkflowTests.exe'
$testSources=@('ui\NativePnp.cs','ui\DriverService.cs','ui\NativeWorkflow.cs','ui\UnlockSnapshot.cs','ui\CertificateManager.cs','ui\LogSession.cs','ui\TaskManagement.cs','ui\NvidiaPowerApi.cs','ui\PowerControl.cs','ui\PowerTasks.cs','ui\SharedSynchronization.cs','ui\RuntimeDeployment.cs','tools\NativeWorkflowTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $csc /nologo /target:exe /platform:x64 /warnaserror+ "/out:$testExe" /reference:System.Web.Extensions.dll /reference:Microsoft.CSharp.dll $testSources
if($LASTEXITCODE -ne 0) { throw 'Native workflow tests did not compile.' }
& $testExe $SignedDriverPath
if($LASTEXITCODE -ne 0) { throw 'Native workflow tests failed.' }
if (!$OutputDirectory) { $OutputDirectory=Join-Path $PSScriptRoot ('dist\CMP90HX-Control-'+$version+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Output directory already exists. Use a new directory to preserve the previous package.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
# Keep the script ASCII-safe for Windows PowerShell 5.1 without a UTF-8 BOM.
$guideName=(-join [char[]]@(0x4f7f,0x7528,0x8bf4,0x660e))+'.md'
$files=[ordered]@{
    'CMP90HXControl.exe'='build\CMP90HXControl.exe'
    'CMP90HXControl.exe.config'='build\CMP90HXControl.exe.config'
    'runtime\CMP90HXGen2.exe'='build\CMP90HXGen2.exe'
    'runtime\CMP90HXGen2.exe.config'='build\CMP90HXGen2.exe.config'
    'core\nvpermissive-core.o'='vendor\nvpermissive-dist-469dc0c\obj\nvpermissive-core.o'
    'LICENSE'='LICENSE'
    'THIRD_PARTY_NOTICES.md'='THIRD_PARTY_NOTICES.md'
    $guideName='docs\RELEASE_GUIDE_ZH.md'
    'driver\cert\Pikachu Test CA RSA.cer'='build\dma-driver\cert\Pikachu Test CA RSA.cer'
    'driver\cert\Pikachu Time Sub CA.cer'='build\dma-driver\cert\Pikachu Time Sub CA.cer'
}
foreach ($entry in $files.GetEnumerator()) {
    $destination=Join-Path $OutputDirectory $entry.Key
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    $source=Join-Path $PSScriptRoot $entry.Value
    if ($entry.Key -eq $guideName) {
        [IO.File]::WriteAllText($destination,[IO.File]::ReadAllText($source),(New-Object Text.UTF8Encoding($true)))
    } else {
        Copy-Item -LiteralPath $source -Destination $destination
    }
}
New-Item -ItemType Directory -Path (Join-Path $OutputDirectory 'driver') -Force | Out-Null
Copy-Item -LiteralPath $SignedDriverPath -Destination (Join-Path $OutputDirectory 'driver\CMP90HXDmaSigned.sys')
if ((Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'driver\CMP90HXDmaSigned.sys')).Hash -ne $expectedDriver -or
    (Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'runtime\CMP90HXGen2.exe')).Hash -ne $expectedWorker) {
    throw 'Packaged worker/driver differs from the pinned release inputs; retry the build.'
}
$hashes=[ordered]@{}
foreach ($file in Get-ChildItem -LiteralPath $OutputDirectory -File -Recurse) {
    $relative=$file.FullName.Substring($OutputDirectory.Length+1).Replace('\','/')
    $hashes[$relative]=(Get-FileHash -LiteralPath $file.FullName).Hash
}
$hashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'files.sha256.json') -Encoding UTF8
@{version=$version;builtUtc=[DateTime]::UtcNow.ToString('o');coreVersion='469dc0c';gen2Sha256=$expectedWorker;dmaSha256=$expectedDriver;dmaSigner=$signature.SignerCertificate.Subject;workflow='native-csharp';uac='once-per-launch';unlockTiming='fast-default-with-conservative-option';verificationSamples=1;startupStatusReads=1;autoUnlock='SYSTEM boot + Kernel-Power 107; device-readiness without fixed delay';idlePower='opt-in SYSTEM resident NVAPI P8; coordinated unlock pause; GPU/video/application release';logs='ProgramData startup-cleanup; power log capped at 1 MiB'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'release.json') -Encoding UTF8
$report=Join-Path $PSScriptRoot 'build\release-validation.log'
$gui=Join-Path $OutputDirectory 'CMP90HXControl.exe'
$check=Start-Process -FilePath $gui -ArgumentList @('--validate-package',('"'+$report+'"')) -WindowStyle Hidden -Wait -PassThru
if($check.ExitCode -ne 0) { Get-Content -LiteralPath $report -Tail 15; throw "Distribution validation failed: $($check.ExitCode)" }
$headlessReport=Join-Path $PSScriptRoot 'build\headless-validation.log'
$headless=Start-Process -FilePath $gui -ArgumentList @('--validate-headless',('"'+$headlessReport+'"')) -WindowStyle Hidden -Wait -PassThru
if($headless.ExitCode -ne 0) {Get-Content -LiteralPath $headlessReport;throw 'Headless startup loaded WPF.'}
$zip=$OutputDirectory+'.zip'
Compress-Archive -LiteralPath $OutputDirectory -DestinationPath $zip -CompressionLevel Optimal
$archiveHash=(Get-FileHash -LiteralPath $zip).Hash
$archiveHash+'  '+[IO.Path]::GetFileName($zip) | Set-Content -LiteralPath ($zip+'.sha256') -Encoding ASCII
Write-Host "RELEASE_PACKAGE_READY $zip"
Write-Host "SHA256 $archiveHash"
Write-Host "Validation log: $report"
