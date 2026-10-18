[CmdletBinding()]
param([string]$OutputDirectory,[string]$MSBuild,[string]$CertificateDirectory,[switch]$NonInteractive,[switch]$PackageOnly,[switch]$EnableRootCertificates,[switch]$RebuildReader,[string]$OnboardingUrl)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'tools\BuildCommon.ps1')
if(!$NonInteractive -and !$PSBoundParameters.ContainsKey('OnboardingUrl')) {
    Write-Host ('Default onboarding URL: '+(Resolve-BuildOnboardingUrl ''))
    $OnboardingUrl=Read-Host 'Enter a custom onboarding URL, or press Enter to use the default'
}
$OnboardingUrl=Resolve-BuildOnboardingUrl $OnboardingUrl
& (Join-Path $PSScriptRoot 'tools\prepare-backend-packages.ps1') -PolicyOnly -RequireSigned
if($PackageOnly -and $RebuildReader){throw '-PackageOnly cannot be combined with -RebuildReader.'}
if(!$PackageOnly){& (Join-Path $PSScriptRoot 'build-gui.ps1') -MSBuild $MSBuild -CertificateDirectory $CertificateDirectory -NonInteractive:$NonInteractive -EnableRootCertificates:$EnableRootCertificates -RebuildReader:$RebuildReader -OnboardingUrl $OnboardingUrl}
$build=Join-Path $PSScriptRoot 'build'
$reader=Join-Path $build 'reader'
if((Get-AuthenticodeSignature -LiteralPath (Join-Path $reader 'CMP90HXReader.sys')).Status -ne 'Valid'){throw 'Sign the Console reader SYS, update pins, then rebuild Console before packaging.'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $PSScriptRoot ('dist\CMP90HX-Control-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw 'Use a new release directory to preserve prior packages.'}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
# Compare the built policy stamp with the current inputs even in PackageOnly mode.
$built=Get-Content -LiteralPath (Join-Path $build 'compiled-backends.json') -Raw | ConvertFrom-Json
if($built.OnboardingUrl -cne $OnboardingUrl){throw 'Onboarding URL differs from the compiled Console; rebuild with the requested URL.'}
if($built.RootCertificateSupport -ne $EnableRootCertificates.IsPresent){throw 'Root certificate build option differs from the compiled Console; rebuild with the same option.'}
$current=Get-Content -LiteralPath (Join-Path $build 'backend-inputs.json') -Raw | ConvertFrom-Json
if(($built.Inputs | ConvertTo-Json -Compress) -cne ($current.Inputs | ConvertTo-Json -Compress)){throw 'Backend packages changed since Console compilation; rebuild Console.'}
if(!$built.Artifacts){throw 'Missing compiled artifact hashes; rebuild Console.'}
foreach($property in $built.Artifacts.PSObject.Properties) {
 $file=Join-Path $build $property.Name.Replace('/',[IO.Path]::DirectorySeparatorChar)
 if(!(Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file).Hash -cne $property.Value){throw "Compiled artifact changed: $($property.Name). Rebuild Console."}
}
& (Join-Path $PSScriptRoot 'tools\copy-release-package.ps1') -BuildDirectory $build -OutputDirectory $OutputDirectory -EnableRootCertificates:$EnableRootCertificates
Compress-Archive -LiteralPath $OutputDirectory -DestinationPath ($OutputDirectory+'.zip')
Write-Host "RELEASE_PACKAGE_READY $OutputDirectory.zip (prepared packages only; no backend build)"
