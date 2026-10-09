using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace CMP90HX.Control {
    internal static class StartupProgram {
        [STAThread] static int Main(string[] args) {
            if(args.Length==2 && args[0]=="--validate-headless") {
                bool clean=!AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name=="PresentationFramework");
                File.WriteAllText(args[1],clean?"FRONTEND_BOOTSTRAP_NO_WPF_PASSED":"FAILED");return clean?0:1;
            }
            if(args.Length==2 && args[0]=="--validate-package") {
                using(var child=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"CMP90HXBackground.exe"),"--validate-package "+App.Quote(args[1])) {UseShellExecute=false,CreateNoWindow=true})) {child.WaitForExit();return child.ExitCode;}
            }
            return RunGui();
        }
        [MethodImpl(MethodImplOptions.NoInlining)] static int RunGui(){var app=new App();app.InitializeComponent();return app.Run();}
    }
}
