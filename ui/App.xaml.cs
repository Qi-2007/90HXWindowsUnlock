using System;
using System.IO;
using System.Threading;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Threading.Tasks;

namespace CMP90HX.Control
{
    public partial class App : Application
    {
        internal static string PreviewState;
        internal static string SnapshotPath;
        internal static LogSession Session;
        Mutex instance;
        bool ownsInstance;
        DesktopPresence desktop;
        IDisposable runtimeLease;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            bool compact = false;
            string validationReport = null;
            bool elevationAttempted = false;
            bool automatic = false;
            try {
                for (int i = 0; i < e.Args.Length; i++) {
                    string argument = e.Args[i];
                    if (argument == "--preview" && i + 1 < e.Args.Length) PreviewState = e.Args[++i];
                    else if (argument == "--snapshot" && i + 1 < e.Args.Length) SnapshotPath = Path.GetFullPath(e.Args[++i]);
                    else if (argument == "--compact") compact = true;
                    else if (argument == "--elevated") elevationAttempted = true;
                    else if (argument == "--auto-unlock") automatic = true;
                    else if (argument == "--validate-package" && i + 1 < e.Args.Length) validationReport = Path.GetFullPath(e.Args[++i]);
                    else throw new ArgumentException("未知参数：" + argument);
                }
                if (SnapshotPath != null && PreviewState == null) PreviewState = "running";
                if (PreviewState != null && PreviewState != "running" && PreviewState != "success" && PreviewState != "failure" && PreviewState != "driver" && PreviewState != "task" && PreviewState != "power")
                    throw new ArgumentException("--preview 支持 running、success、failure、driver、task、power。");
                if(automatic && (PreviewState!=null || validationReport!=null || compact)) throw new ArgumentException("后台参数不能与预览或包检查混用。");
                if (validationReport != null) {
                    // Distribution checks never query devices or load drivers.
                    var paths = new RuntimePaths(AppDomain.CurrentDomain.BaseDirectory);
                    Directory.CreateDirectory(Path.GetDirectoryName(validationReport));
                    using (var report = new StreamWriter(validationReport, false, System.Text.Encoding.UTF8)) {
                        Action<string> log = line => { lock(report) { report.WriteLine(line); report.Flush(); } };
                        int code = new NativeWorkflow(paths,new WindowsWorkflowPlatform(paths,log),log).Execute("Check",Path.Combine(Path.GetTempPath(),"CMP90HX-package-check",Guid.NewGuid().ToString("N")));
                        Shutdown(code);
                    }
                    return;
                }
                if(PreviewState==null && !automatic && DesktopPresence.TryActivate()) {Shutdown(0);return;}
                if (PreviewState == null && !IsAdministrator()) {
                    if(automatic) throw new InvalidOperationException("后台控制需由管理员或 SYSTEM 运行。");
                    if(elevationAttempted) throw new InvalidOperationException("当前账户未取得管理员权限。");
                    // Elevate once. Every worker inherits this token without another UAC dialog.
                    Process.Start(new ProcessStartInfo {
                        FileName=Process.GetCurrentProcess().MainModule.FileName,
                        Arguments=String.Join(" ",Array.ConvertAll(e.Args,WindowsWorkflowPlatform.Quote))+" --elevated",
                        UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Normal
                    });
                    Shutdown(0);
                    return;
                }
                if (PreviewState == null && !automatic) {
                    instance = new Mutex(false, @"Local\CMP90HXControl");
                    try { ownsInstance = instance.WaitOne(0); }
                    catch (AbandonedMutexException) { ownsInstance = true; }
                    if (!ownsInstance) {
                        for(int attempt=0;attempt<20;attempt++) {if(DesktopPresence.TryActivate()) {Shutdown(0);return;}System.Threading.Thread.Sleep(100);}
                        MessageBox.Show("控制台正在启动，请稍后再次打开。", "CMP 90HX 控制台", MessageBoxButton.OK, MessageBoxImage.Information);
                        Shutdown(0);
                        return;
                    }
                }
                if(PreviewState==null) {
                    runtimeLease=RuntimeDeployment.Lease(AppDomain.CurrentDomain.BaseDirectory);
                    string appRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"CMP90HX");
                    DriverService.ProtectDirectory(appRoot);
                    var paths=new RuntimePaths(AppDomain.CurrentDomain.BaseDirectory);
                    DriverService.ProtectDirectory(paths.Logs);
                    Session=new LogSession(paths.Logs);
                    string legacy=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CMP90HX","logs");
                    if(Directory.Exists(legacy)) LogSession.CleanPrevious(legacy,null,DateTime.UtcNow);
                    if(!automatic) Task.Run(()=>TaskManagement.CleanUnusedRuntimes(line=>File.AppendAllText(Path.Combine(Session.DirectoryPath,"runtime-cleanup.log"),line+Environment.NewLine)));
                    if(automatic) {
                        using(var report=new StreamWriter(Path.Combine(Session.DirectoryPath,"auto-unlock.log"),false,System.Text.Encoding.UTF8)) {
                            report.AutoFlush=true;
                            Action<string> log=line=>{lock(report) report.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")+" "+line);};
                            int code=new NativeWorkflow(paths,new WindowsWorkflowPlatform(paths,log),log).Execute("AutoUnlock",Session.DirectoryPath);
                            Shutdown(code);
                        }
                        return;
                    }
                }
                MainWindow window = new MainWindow();
                if(PreviewState=="power") {
                    var power=new PowerWindow(window,new RuntimePaths(AppDomain.CurrentDomain.BaseDirectory),null,true);
                    MainWindow=power;power.Show();return;
                }
                if(PreviewState=="driver" || PreviewState=="task") {
                    var management=new ManagementWindow(window,PreviewState=="task",new RuntimePaths(AppDomain.CurrentDomain.BaseDirectory),null,true);
                    MainWindow=management;management.Show();return;
                }
                if (compact) { window.Width = 900; window.Height = 680; }
                MainWindow = window;
                if(PreviewState==null) desktop=new DesktopPresence(window);
                window.Show();
            } catch (Exception error) {
                if (SnapshotPath != null) File.WriteAllText(SnapshotPath + ".error.txt", error.ToString());
                else if(automatic) {
                    if(Session!=null) File.WriteAllText(Path.Combine(Session.DirectoryPath,"startup-error.log"),error.ToString());
                } else MessageBox.Show(error.Message, "控制台无法启动", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        internal static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if(desktop!=null) desktop.Dispose();
            if(runtimeLease!=null) runtimeLease.Dispose();
            if (ownsInstance) instance.ReleaseMutex();
            if (instance != null) instance.Dispose();
            if (Session != null) Session.Dispose();
            base.OnExit(e);
        }
    }
}
