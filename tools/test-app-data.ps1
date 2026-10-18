$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('cmp90hx-data-test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
 $runner=Join-Path $testRoot 'Test.cs'
 @'
using System;
using System.Collections.Generic;
using System.IO;
using CMP90HX.Control;
static class Test {
 static void Check(bool value,string message){if(!value)throw new Exception(message);}
 static void Write(string path,string value){Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,value);}
 static int Main(string[] args){
  string legacy=Path.Combine(args[0],"legacy","CMP90HX"),root=Path.Combine(args[0],"shared","CMP90HX");
  Write(Path.Combine(legacy,"Onboarding","guide.opened"),"url");
  Write(Path.Combine(root,"Onboarding","guide.opened"),"url");
  Write(Path.Combine(legacy,"FrontendLogs","old.log"),"old log");
  Write(Path.Combine(root,"FrontendLogs","old.log"),"new log");
  string active=Path.Combine(legacy,"FrontendLogs","locked.log");Write(active,"active");
  using(var lease=new FileStream(active,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){
   AppData.MigrateLegacy(legacy,root,Console.WriteLine);
   Check(File.Exists(active),"Migration removed a live file");
   Check(!File.Exists(Path.Combine(root,"FrontendLogs","locked.log")),"Migration copied a live file");
  }
  Check(File.ReadAllText(Path.Combine(root,"FrontendLogs","old.log"))=="new log","Migration overwrote existing data");
  Check(Directory.GetFiles(Path.Combine(root,"FrontendLogs"),"old.log.legacy-*").Length==1,"Conflicting old log was lost");
  AppData.MigrateLegacy(legacy,root,Console.WriteLine);
  Check(!Directory.Exists(legacy),"Migrated legacy directory was retained");
  Check(File.ReadAllText(Path.Combine(root,"FrontendLogs","locked.log"))=="active","Deferred migration did not retry");
  Check(Directory.GetFiles(Path.Combine(root,"Onboarding")).Length==1,"Identical guide markers were duplicated");
  string driver=Path.Combine(root,"DriverStore","test","driver.sys");Write(driver,"driver");
  var scheduled=new List<string>();
  using(var lease=new FileStream(driver,FileMode.Open,FileAccess.Read,FileShare.Read)){
   AppData.Cleanup(root,true,scheduled.Add);
   Check(scheduled.Contains(driver),"Locked driver was not deferred");
   Check(scheduled[scheduled.Count-1]==root,"Parent was scheduled before children");
   Check(!Directory.Exists(Path.Combine(root,"FrontendLogs")),"Unlocked logs were not deleted");
   Check(!Directory.Exists(Path.Combine(root,"Onboarding")),"Guide markers were not deleted");
  }
  AppData.Cleanup(root,true,delegate(string path){throw new Exception("Unexpected locked file: "+path);});
  Check(!Directory.Exists(root),"Data root was not removed");
  Write(Path.Combine(root,"settings.json"),"settings");scheduled.Clear();
  AppData.Cleanup(root,false,scheduled.Add);
  Check(File.Exists(Path.Combine(root,"settings.json")),"Deferred cleanup deleted files before processes exited");
  Check(scheduled.Count==2 && scheduled[1]==root,"Deferred cleanup order is incorrect");
  bool rejected=false;try{AppData.Cleanup(args[0],true,scheduled.Add);}catch(IOException){rejected=true;}
  Check(rejected,"Unexpected cleanup root was accepted");
  AppData.Cleanup(root,true,scheduled.Add);
  Console.WriteLine("PASS: migration, conflicts, locked-file retry, immediate cleanup and reboot scheduling.");return 0;
 }
}
'@ | Set-Content -LiteralPath $runner -Encoding UTF8
 $csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
 $exe=Join-Path $testRoot 'Test.exe'
 & $csc /nologo /target:exe "/out:$exe" (Join-Path $project 'shared\AppData.cs') $runner
 if($LASTEXITCODE -ne 0){throw 'App data test compilation failed'}
 & $exe $testRoot
 if($LASTEXITCODE -ne 0){throw 'App data tests failed'}
} finally {
 $resolved=[IO.Path]::GetFullPath($testRoot)
 if($resolved.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved) -like 'cmp90hx-data-test-*'){
  Remove-Item -LiteralPath $resolved -Recurse -Force
 }
}
