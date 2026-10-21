using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows;

namespace CMP90HX.Control {
    public partial class App : Application {
        internal static string PreviewState,SnapshotPath;
        internal static LogSession Session;
        internal static bool DisableAutoUnlock;
        internal static int CleanupBackendPid;
        Mutex instance;bool owned;DesktopPresence desktop;
        protected override void OnStartup(StartupEventArgs e) {
            base.OnStartup(e);
            DispatcherUnhandledException+=(sender,failure)=> {
                if(PreviewState==null)ErrorLog.TrySave("frontend-unhandled",failure.Exception.ToString(),Session==null?null:Session.DirectoryPath);
            };
            try {
                bool compact=false,elevated=false;
                for(int i=0;i<e.Args.Length;i++) {
                    if(e.Args[i]=="--preview" && i+1<e.Args.Length)PreviewState=e.Args[++i];
                    else if(e.Args[i]=="--snapshot" && i+1<e.Args.Length)SnapshotPath=Path.GetFullPath(e.Args[++i]);
                    else if(e.Args[i]=="--compact")compact=true;
                    else if(e.Args[i]=="--elevated")elevated=true;
                    else if(e.Args[i]=="--disable-auto-unlock")DisableAutoUnlock=true;
                    else throw new ArgumentException("未知参数："+e.Args[i]);
                }
                if(SnapshotPath!=null && PreviewState==null)PreviewState="running";
                if(PreviewState==null) {
                    if(!IsAdministrator()) {
                        if(elevated)throw new UnauthorizedAccessException("前台需要管理员权限，以连接管理员/SYSTEM 后台。");
                        Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,String.Join(" ",Array.ConvertAll(e.Args,Quote))+" --elevated") {UseShellExecute=true,Verb="runas"});Shutdown(0);return;
                    }
                    instance=SharedSynchronization.OpenMutex("Global\\CMP90HX.Control.Frontend.v1");
                    try {owned=instance.WaitOne(0);}catch(AbandonedMutexException){owned=true;}
                    if(!owned) {DesktopPresence.TryActivate();Shutdown(0);return;}
                    AppData.MigrateLegacy();
                    BackendClient.Ensure(DisableAutoUnlock);
                    if(DisableAutoUnlock)BackendClient.Require("DisableAutomatic",null,null);
                    var state=BackendClient.Read();DisableAutoUnlock|=state.AutoUnlockDisabled;
                    var paths=new RuntimePaths(AppDomain.CurrentDomain.BaseDirectory);Directory.CreateDirectory(paths.Logs);Session=new LogSession(paths.Logs);
                }
                var window=new MainWindow();if(compact){window.Width=900;window.Height=680;}
                if(PreviewState=="power") {var power=new PowerWindow(window,new RuntimePaths(AppDomain.CurrentDomain.BaseDirectory),null,true);MainWindow=power;power.Show();return;}
                if(PreviewState=="driver") {var management=new ManagementWindow(window,new RuntimePaths(AppDomain.CurrentDomain.BaseDirectory),null,true);MainWindow=management;management.Show();return;}
                if(PreviewState=="settings") {var management=new ManagementWindow(window,new RuntimePaths(AppDomain.CurrentDomain.BaseDirectory),null,true);var settings=new BackendSettingsWindow(management,true);MainWindow=settings;settings.Show();return;}
                MainWindow=window;if(PreviewState==null)desktop=new DesktopPresence(window);window.Show();
            }catch(Exception error){if(SnapshotPath!=null)File.WriteAllText(SnapshotPath+".error.txt",error.ToString());else {ErrorLog.TrySave("frontend-startup",error.ToString());MessageBox.Show(error.Message,"控制台无法启动");}Shutdown(1);}
        }
        internal static string Quote(string value) {return "\""+value.Replace("\"","\\\"")+"\"";}
        internal static bool IsAdministrator(){using(var identity=WindowsIdentity.GetCurrent())return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);}
        protected override void OnExit(ExitEventArgs e){
            if(desktop!=null)desktop.Dispose();
            if(Session!=null)Session.Dispose();
            if(CleanupBackendPid>0) {
                try {AppData.StartCleanup(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"CMP90HXBackground.exe"),CleanupBackendPid);}
                catch(Exception error){MessageBox.Show("无法启动数据清理，请在退出后删除 "+AppData.Root+"："+error.Message,"卸载清理未完成");}
            }
            if(owned)instance.ReleaseMutex();if(instance!=null)instance.Dispose();base.OnExit(e);
        }
    }
}
