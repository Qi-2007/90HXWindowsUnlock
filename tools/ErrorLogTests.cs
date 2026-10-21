using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace CMP90HX.Control {
    internal static class ErrorLogTests {
        static int checks;
        static void Check(bool condition,string name) {
            if(!condition)throw new Exception(name);
            Console.WriteLine("PASS "+name);checks++;
        }
        static string[] Records(string root) {return Directory.GetDirectories(root).Where(path=>File.Exists(Path.Combine(path,"error.log"))).ToArray();}
        static int Main(string[] args) {
            if(args.Length==3 && args[0]=="--save") {ErrorLog.Save(args[1],args[2],"parallel child error",null);return 0;}
            string parent=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"cmp90hx-error-tests-"+Guid.NewGuid().ToString("N"));
            string data=Path.Combine(parent,"CMP90HX"),root=Path.Combine(data,"ErrorLogs"),source=Path.Combine(data,"Logs","operation");
            Directory.CreateDirectory(source);
            try {
                File.WriteAllText(Path.Combine(source,"worker.log"),"hardware failure details");
                Directory.CreateDirectory(Path.Combine(source,"nested"));File.WriteAllText(Path.Combine(source,"nested","snapshot.json"),"evidence");
                string first=ErrorLog.Save(root,"Unlock","exit=1",source);
                Check(File.ReadAllText(Path.Combine(first,"error.log")).Contains("exit=1"),"exception details saved separately");
                Check(File.ReadAllText(Path.Combine(first,"operation","worker.log"))=="hardware failure details" && File.Exists(Path.Combine(first,"operation","nested","snapshot.json")),"operation logs and nested evidence copied");
                LogSession.CleanPrevious(Path.Combine(data,"Logs"),"",DateTime.UtcNow.AddSeconds(1));
                Check(!Directory.Exists(source) && File.Exists(Path.Combine(first,"operation","worker.log")),"startup cleanup cannot remove archived error evidence");
                string[] saved=new string[11];saved[0]=first;
                for(int i=1;i<10;i++){Thread.Sleep(5);saved[i]=ErrorLog.Save(root,"error-"+i,"failure",null);}
                Check(Records(root).Length==10 && saved.Take(10).All(Directory.Exists),"exactly ten errors are all retained");
                Thread.Sleep(5);saved[10]=ErrorLog.Save(root,"error-10","failure",null);
                Check(Records(root).Length==10 && !Directory.Exists(first) && saved.Skip(1).All(Directory.Exists),"eleventh error removes only oldest record");
                string unrelated=Path.Combine(root,"user-notes");Directory.CreateDirectory(unrelated);File.WriteAllText(Path.Combine(unrelated,"keep.txt"),"keep");
                string executable=Process.GetCurrentProcess().MainModule.FileName;
                var children=new Process[3];
                try {
                    for(int i=0;i<children.Length;i++)children[i]=Process.Start(new ProcessStartInfo(executable,"--save \""+root+"\" child-"+i) {UseShellExecute=false,CreateNoWindow=true});
                    foreach(var child in children)if(!child.WaitForExit(15000) || child.ExitCode!=0)throw new Exception("Concurrent archive process failed");
                } finally {foreach(var child in children)if(child!=null)child.Dispose();}
                Check(Records(root).Length==10,"concurrent frontend and backend saves preserve ten-record bound");
                Check(File.Exists(Path.Combine(unrelated,"keep.txt")),"retention does not delete unrelated directories");
                Check(Directory.GetDirectories(root,".pending-*").Length==0,"completed archives leave no staging directories");
                string locked=Path.Combine(Records(root)[0],"error.log");var scheduled=new List<string>();
                using(var lease=new FileStream(locked,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                    AppData.Cleanup(data,true,scheduled.Add);
                    Check(File.Exists(locked) && scheduled.Contains(locked),"locked error log is scheduled for reboot deletion");
                    Check(scheduled[scheduled.Count-1]==data,"uninstall schedules parent after locked error evidence");
                    Check(!Directory.Exists(unrelated),"uninstall removes unlocked archive contents immediately");
                }
                AppData.Cleanup(data,true,path=>{throw new Exception("Unexpected deferred deletion: "+path);});
                Check(!Directory.Exists(data),"uninstall removes error archives and entire data root after locks release");
                string deferred=ErrorLog.Save(root,"deferred-cleanup","test",null);scheduled.Clear();
                AppData.Cleanup(data,false,scheduled.Add);
                Check(File.Exists(Path.Combine(deferred,"error.log")) && scheduled.Contains(Path.Combine(deferred,"error.log")),"active-process cleanup defers error file deletion");
                Check(scheduled.IndexOf(Path.Combine(deferred,"error.log"))<scheduled.IndexOf(deferred) && scheduled[scheduled.Count-1]==data,"deferred cleanup schedules error files before archive and data directories");
                Console.WriteLine("ERROR_LOG_TESTS_PASSED count="+checks);return 0;
            } catch(Exception error) {Console.Error.WriteLine(error);return 1;}
            finally {if(Directory.Exists(parent))Directory.Delete(parent,true);}
        }
    }
}
