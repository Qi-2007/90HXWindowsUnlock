using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace CMP90HX.Control {
    internal static class BackendClient {
        static readonly object launchGate=new object();
        internal static bool Alive(ControlState state) {return state!=null && (DateTime.UtcNow-state.Heartbeat).TotalSeconds<5;}
        internal static ControlState Read() {
            using(var memory=new ControlMemory(false)) {var state=memory.State();if(!Alive(state)) throw new IOException("后台已断开；硬件请求不会自动重发。");return state;}
        }
        internal static void Ensure(bool disableAutomatic=false) {
            lock(launchGate) {
                try {Read();return;} catch(FileNotFoundException) {} catch(IOException error) {if(error.Message.Contains("版本不匹配"))throw;}
                string exe=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"CMP90HXBackground.exe");
                if(!File.Exists(exe)) throw new FileNotFoundException("缺少后台程序，请重新构建 Console。",exe);
                using(var process=Process.Start(new ProcessStartInfo(exe,disableAutomatic?"--disable-auto-unlock":"") {UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(exe)})) {}
                var wait=Stopwatch.StartNew();
                while(wait.ElapsedMilliseconds<15000) {try {Read();return;} catch(IOException) {} Thread.Sleep(100);}
                throw new IOException("后台未就绪，请检查后台日志。需要管理员权限。");
            }
        }
        internal static IpcResult Execute(string command,string data=null,bool conservative=false,Action<string> log=null) {
            Ensure();
            using(var memory=new ControlMemory(false)) {
                var before=memory.State();string instance=before.Instance;
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
