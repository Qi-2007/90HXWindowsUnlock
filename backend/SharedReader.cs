using System;
using System.Diagnostics;
using System.IO;
namespace CMP90HX.Control {
 internal static class SharedReader {
  static string Package() {string root=AppDomain.CurrentDomain.BaseDirectory;string deployed=Path.Combine(root,"reader");return Directory.Exists(deployed)?deployed:Path.Combine(root,"reader-driver");}
  internal static string Driver(RuntimePaths paths) {return Path.Combine(Package(),"CMP90HXReader.sys");}
  internal static string Client(RuntimePaths paths) {return Path.Combine(Package(),"CMP90HXReader.exe");}
  internal static void Ensure(RuntimePaths paths,Action<string> log,bool install=false) {
   RuntimePaths.RequireHash(Client(paths),RuntimePaths.ReaderClientHash);
   ReaderService.EnsureRunning(Driver(paths),log,install);
  }
  internal static int Capture(RuntimePaths paths,string bdf,string output,Action<string> log) {
   Ensure(paths,log);
   using(var process=new Process {StartInfo=new ProcessStartInfo(Client(paths),"--bdf "+WindowsWorkflowPlatform.Quote(bdf)+" --out "+WindowsWorkflowPlatform.Quote(output)) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}}) {
    process.OutputDataReceived+=(s,e)=>{if(e.Data!=null)log(e.Data);};process.ErrorDataReceived+=(s,e)=>{if(e.Data!=null)log(e.Data);};
    process.Start();process.BeginOutputReadLine();process.BeginErrorReadLine();process.WaitForExit();return process.ExitCode;
   }
  }
 }
}