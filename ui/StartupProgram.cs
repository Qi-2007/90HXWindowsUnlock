using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text;

namespace CMP90HX.Control
{
    internal static class StartupProgram
    {
        [STAThread]
        static int Main(string[] args)
        {
            if(args.Length==2 && args[0]=="--validate-headless") return ValidateHeadless(args[1]);
            // Branch before constructing Application: no WPF dispatcher, XAML or window.
            if(args.Contains("--power-monitor")) return RunPower(args);
            return RunGui();
        }
        static int ValidateHeadless(string report)
        {
            var settings=new PowerSettings();settings.Validate();
            var loaded=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetName().Name).OrderBy(s=>s).ToArray();
            bool clean=!loaded.Any(s=>new[]{"PresentationFramework","PresentationCore","WindowsBase","System.Xaml"}.Contains(s));
            string path=Path.GetFullPath(report);Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path,(clean?"HEADLESS_ENTRY_NO_WPF_PASSED":"HEADLESS_ENTRY_FAILED")+"\r\n"+String.Join("\r\n",loaded),Encoding.UTF8);
            return clean?0:1;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        static int RunGui()
        {
            var app=new App();app.InitializeComponent();return app.Run();
        }
        static int RunPower(string[] args)
        {
            LogSession session=null;
            try {
                using(var identity=WindowsIdentity.GetCurrent())
                    if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 1;
                using(var lease=RuntimeDeployment.Lease(AppDomain.CurrentDomain.BaseDirectory)) {
                    string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"CMP90HX");
                    DriverService.ProtectDirectory(root);
                    string logs=Path.Combine(root,"Logs");DriverService.ProtectDirectory(logs);
                    session=new LogSession(logs);
                    if(args.Length!=1 || args[0]!="--power-monitor") throw new ArgumentException("--power-monitor 必须单独使用。");
                    // Power monitoring uses only settings and system NVAPI/NVML.
                    // No worker/core/driver file checks, snapshots, self-tests or PnP changes.
                    File.WriteAllText(Path.Combine(session.DirectoryPath,"power-host.log"),"HEADLESS_POWER_HOST: no WPF application or desktop window.\r\n",Encoding.UTF8);
                    return PowerMonitor.Run(session.DirectoryPath);
                }
            } catch(Exception error) {
                if(session!=null) File.WriteAllText(Path.Combine(session.DirectoryPath,"power-error.log"),error.ToString());
                return 1;
            } finally {if(session!=null) session.Dispose();}
        }
    }
}
