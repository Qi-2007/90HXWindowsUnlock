using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace CMP90HX.Control
{
    internal static class DesktopLifecycleTests
    {
        [STAThread]
        static int Main(string[] args)
        {
            if(args.Length==2 && args[0]=="--activate") return DesktopPresence.TryActivate(args[1])?0:1;
            string report=args[0],channel=".test-"+Guid.NewGuid().ToString("N");
            var app=new Application();var window=new Window {Title="CMP90HX activation test",Width=300,Height=120};
            using(var presence=new DesktopPresence(window,channel)) {
                app.Startup+=async(sender,eventArgs)=> {
                    try {
                        window.Show();window.WindowState=WindowState.Minimized;
                        int exit=await Task.Run(()=> {
                            using(var child=new Process {StartInfo=new ProcessStartInfo {FileName=Process.GetCurrentProcess().MainModule.FileName,Arguments="--activate "+channel,UseShellExecute=false,CreateNoWindow=true}}) {
                                child.Start();if(!child.WaitForExit(5000)) throw new Exception("activation sender timed out");return child.ExitCode;
                            }
                        });
                        await Task.Delay(300);
                        if(exit!=0 || window.WindowState!=WindowState.Normal || !window.IsActive) throw new Exception("existing desktop window was not restored and focused");
                        File.WriteAllText(report,"DESKTOP_REACTIVATION_PASSED: a second process restores and focuses the existing minimized window.");app.Shutdown(0);
                    } catch(Exception error) {File.WriteAllText(report,error.ToString());app.Shutdown(1);}
                };
                return app.Run();
            }
        }
    }
}
