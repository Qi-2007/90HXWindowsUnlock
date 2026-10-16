using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace CMP90HX.Control {
    internal static class BackendClient {
        static readonly object launchGate=new object();
        internal static bool Alive(ControlState state) {return state!=null && (DateTime.UtcNow-state.Heartbeat).TotalSeconds<5;}
        internal static void RequireCurrent(ControlState state) {
            if(state.BackendRevision!=ControlMemory.BackendRevision || state.RootCertificateSupport!=ControlMemory.RootCertificateSupport)throw new IOException("后台版本不匹配，请重新打开配套的新版控制台。");
        }
        internal static ControlState Read() {
            using(var memory=new ControlMemory(false)) {var state=memory.State();if(!Alive(state)) throw new IOException("后台已断开；硬件请求不会自动重发。");RequireCurrent(state);return state;}
        }
        internal static void Ensure(bool disableAutomatic=false) {
            lock(launchGate) {
                bool diagnostic=Environment.GetEnvironmentVariable("CMP90HX_IPC_DIAGNOSTIC")=="1";
                using(var launch=ControlMemory.OpenLock(diagnostic?ControlMemory.DiagnosticPrefix+"Launch.v1":"Global\\CMP90HX.Control.Launch.v1",diagnostic)) {
                bool owned=false;
                try {
                try {owned=launch.WaitOne(180000);}catch(AbandonedMutexException){owned=true;}
                if(!owned)throw new IOException("后台换版仍在进行，请稍后重试。");
                ControlState existing=null;
                try {using(var memory=new ControlMemory(false)){var value=memory.State();if(Alive(value))existing=value;}}
                catch(FileNotFoundException){}catch(IOException error){if(error.Message.Contains("版本不匹配"))throw;}
                if(existing!=null && existing.BackendRevision==ControlMemory.BackendRevision && existing.RootCertificateSupport==ControlMemory.RootCertificateSupport)return;
                string exe=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"CMP90HXBackground.exe");
                if(!File.Exists(exe)) throw new FileNotFoundException("缺少后台程序，请重新构建 Console。",exe);
                if(existing!=null) {
                    if(existing.BackendRevision>ControlMemory.BackendRevision)throw new IOException("后台为更新版本，请使用与后台配套的新版控制台。");
                    StopPrevious(exe,existing);
                }
                string arguments=(diagnostic?"--diagnostics ":"")+(disableAutomatic?"--disable-auto-unlock":"");
                using(var process=Process.Start(new ProcessStartInfo(exe,arguments) {UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(exe)})) {}
                var wait=Stopwatch.StartNew();
                while(wait.ElapsedMilliseconds<15000) {try {Read();return;} catch(IOException error) {if(error.Message.Contains("版本不匹配"))throw;} Thread.Sleep(100);}
                throw new IOException("后台未就绪，请检查后台日志。需要管理员权限。");
                } finally {if(owned)launch.ReleaseMutex();}
                }
            }
        }
        static void StopPrevious(string exe,ControlState state) {
            Process previous;
            try {previous=Process.GetProcessById(state.Pid);}catch(ArgumentException){return;}
            using(previous) {
                if(previous.HasExited)return;
                var info=new ProcessStartInfo(exe,"--request Shutdown --data \""+state.Instance+"\"") {UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(exe)};
                using(var request=Process.Start(info)) {
                    if(!request.WaitForExit(120000))throw new IOException("旧后台仍在完成操作，Shutdown 请求未完成；未强制结束或启动第二个后台。");
                    if(request.ExitCode!=0 && !previous.HasExited)throw new IOException("旧后台未接受 Shutdown 请求，未启动新后台。请查看后台启动错误日志。");
                }
                if(!previous.WaitForExit(30000))throw new IOException("旧后台尚未正常退出，暂未启动新后台。");
            }
        }
        internal static IpcResult Execute(string command,string data=null,bool conservative=false,Action<string> log=null) {
            // Shutdown remains available for a previous backend using protocol 2.
            if(command!="Shutdown")Ensure();
            using(var memory=new ControlMemory(false)) {
                var before=memory.State();if(!Alive(before))throw new IOException("后台已断开。");string instance=before.Instance;
                if(command=="Shutdown" && data!=null && data!=instance)throw new IOException("后台实例已经变化，未关闭其他后台。");
                var request=new IpcRequest {Id=Guid.NewGuid().ToString("N"),Command=command,Data=data,Conservative=conservative,SubmittedUtc=DateTime.UtcNow};
                memory.Submit(request);long revision=-1;string emitted="";
                while(true) {
                    var state=memory.State();
                    if(!Alive(state) || state.Instance!=instance) throw new IOException("后台失去响应。操作可能已提交，请检查状态后再执行，不能自动重试。");
                    if(state.JobId==request.Id && state.LogRevision!=revision) {
                        string text=state.Log??"";
                        if(log!=null) {
                            string delta=text.StartsWith(emitted,StringComparison.Ordinal)?text.Substring(emitted.Length):text;
                            if(delta.Length>0) log(delta.TrimEnd('\r','\n'));
                        }
                        emitted=text;revision=state.LogRevision;
                    }
                    var result=state.Results.FirstOrDefault(r=>r.Id==request.Id);
                    if(result!=null) {
                        if(log!=null && result.Log!=null && result.Log!=emitted) {
                            string delta=result.Log.StartsWith(emitted,StringComparison.Ordinal)?result.Log.Substring(emitted.Length):result.Log;
                            if(delta.Length>0)log(delta.TrimEnd('\r','\n'));
                        }
                        return result;
                    }
                    Thread.Sleep(100);
                }
            }
        }
        internal static void Require(string command,string data,Action<string> log) {
            var result=Execute(command,data,false,log);
            if(result.Code!=0) throw new IOException(result.Error??("后台操作失败："+command+"，退出码 "+result.Code));
        }
    }
}
