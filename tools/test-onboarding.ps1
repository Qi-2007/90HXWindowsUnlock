$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'BuildCommon.ps1')
$project=Split-Path -Parent $PSScriptRoot
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('cmp90hx-onboarding-test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $testRoot 'ui') -Force | Out-Null
Copy-Item (Join-Path $project 'ui\OnboardingPolicy.cs') (Join-Path $testRoot 'ui')
function Get-BuildRoot {return $testRoot}
try {
 $default=Resolve-BuildOnboardingUrl ''
 if($default -cne 'https://90hx.445533.xyz/onboarding/officialsetup.html'){throw 'Unexpected default onboarding URL'}
 foreach($invalid in @('file:///C:/test.html','javascript:alert(1)','https://example.com/a b')){
  $rejected=$false;try {Resolve-BuildOnboardingUrl $invalid | Out-Null}catch{$rejected=$true}
  if(!$rejected){throw "Invalid URL accepted: $invalid"}
 }
 $runner=Join-Path $testRoot 'Test.cs'
 @'
using System;
using System.IO;
using CMP90HX.Control;
static class Test {
 static void Check(bool value,string message){if(!value)throw new Exception(message);}
 static int Main(string[] args){
  string directory=args[0],url=args[1];int opened=0;
  Check(OnboardingPolicy.Url==url,"Compiled URL differs from release selection");
  Action<string> open=delegate(string link){Check(link==url,"Wrong browser URL");opened++;};
  Check(Onboarding.OpenFirstTime(directory,url,open),"First launch did not open guide");
  Check(!Onboarding.OpenFirstTime(directory,url,open) && opened==1,"Guide reopened on later launch");
  string other=url+"#other-release";
  Check(Onboarding.OpenFirstTime(directory,other,delegate(string link){opened++;}),"Different release guide was skipped");
  Check(!Onboarding.OpenFirstTime(directory,url,open),"Switching back reopened previous guide");
  string retry=Path.Combine(directory,"retry");
  try {Onboarding.OpenFirstTime(retry,url,delegate(string link){throw new IOException("No browser");});throw new Exception("Browser failure ignored");}
  catch(IOException){}
  Check(!Directory.Exists(retry),"Failed browser launch was marked complete");
  Check(Onboarding.OpenFirstTime(retry,url,open),"Failed first launch could not retry");
  bool rejected=false;try {Onboarding.OpenFirstTime(directory,"file:///C:/bad.html",open);}catch(ArgumentException){rejected=true;}
  Check(rejected,"Local file URL was accepted");
  Console.WriteLine("PASS: compiled URL, first launch, repeat launch, release switch and browser failure retry.");return 0;
 }
}
'@ | Set-Content -LiteralPath $runner -Encoding UTF8
 $csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
 $index=0
 foreach($url in @($default,'https://example.com/onboarding/custom.html?channel=test&lang=zh-CN')){
  $policy=Write-BuildOnboardingPolicy $url
  $exe=Join-Path $testRoot ('Test'+$index+'.exe')
  & $csc /nologo /target:exe "/out:$exe" (Join-Path $project 'ui\Onboarding.cs') (Join-Path $project 'shared\AppData.cs') $policy $runner
  if($LASTEXITCODE -ne 0){throw 'Onboarding test compilation failed'}
  & $exe (Join-Path $testRoot ('state'+$index)) $url
  if($LASTEXITCODE -ne 0){throw 'Onboarding tests failed'}
  $index++
 }
} finally {
 $resolved=[IO.Path]::GetFullPath($testRoot)
 if($resolved.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved) -like 'cmp90hx-onboarding-test-*'){
  Remove-Item -LiteralPath $resolved -Recurse -Force
 }
}
