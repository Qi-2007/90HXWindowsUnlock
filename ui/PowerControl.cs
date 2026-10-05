using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CMP90HX.Control
{
    internal sealed class PowerSettings
    {
        public bool Enabled {get;set;}
        public int Threshold {get;set;}
        public int IdleSeconds {get;set;}
        public string FullSpeedApps {get;set;}
        public PowerSettings() {Threshold=15;IdleSeconds=10;FullSpeedApps="";}
        internal void Validate()
        {
            if(Threshold<1 || Threshold>100 || IdleSeconds<1 || IdleSeconds>120 || (FullSpeedApps??"").Length>4096)
                throw new IOException("省电阈值需为 1–100%，空闲等待需为 1–120 秒。");
        }
        internal bool HasFullSpeedApp()
        {
            var names=(FullSpeedApps??"").Split(new[]{',',';','\r','\n'},StringSplitOptions.RemoveEmptyEntries)
                .Select(s=>Path.GetFileNameWithoutExtension(s.Trim())).Where(s=>s.Length>0).ToArray();
            if(names.Length==0) return false;
            foreach(var process in Process.GetProcesses()) using(process) {
                try {if(names.Contains(process.ProcessName,StringComparer.OrdinalIgnoreCase)) return true;}
                catch(InvalidOperationException) { }
                catch(System.ComponentModel.Win32Exception) { }
            }
            return false;
        }
    }
    internal sealed class PowerStatus
    {
        public DateTime HeartbeatUtc {get;set;}
        public string Policy {get;set;}
        public string Detail {get;set;}
        public bool LimitApplied {get;set;}
        public PowerSample Sample {get;set;}
    }
    internal static class PowerStorage
    {
        static bool prepared;
        internal static string Root {get {return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"CMP90HX");}}
        internal static PowerSettings ReadSettings()
        {
            using(var deployment=new DeploymentLock()) {
                var value=ReadSettings(Root);
                if(!File.Exists(Path.Combine(Root,"settings.json")) && File.Exists(Path.Combine(Root,"Power","settings.json"))) {
                    Prepare();WriteSettings(Root,value);
                }
                return value;
            }
        }
        internal static PowerSettings ReadSettings(string root)
        {
            var json=new JavaScriptSerializer();string file=Path.Combine(root,"settings.json");PowerSettings value;
            if(File.Exists(file)) {
                var sections=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(file));object power;
                if(sections==null) throw new IOException("配置文件无效。");
                value=sections.TryGetValue("Power",out power)?json.ConvertToType<PowerSettings>(power):new PowerSettings();
            } else {
                file=Path.Combine(root,"Power","settings.json");
                value=File.Exists(file)?json.Deserialize<PowerSettings>(File.ReadAllText(file)):new PowerSettings();
            }
            if(value==null) throw new IOException("省电配置无效。");value.Validate();return value;
        }
        internal static PowerStatus ReadStatus()
        {return ReadStatus(Root);}
        internal static PowerStatus ReadStatus(string root)
        {
            string file=Path.Combine(root,"State","power-status.json");
            if(!File.Exists(file)) file=Path.Combine(root,"Power","status.json");
            return File.Exists(file)?new JavaScriptSerializer().Deserialize<PowerStatus>(File.ReadAllText(file)):null;
        }
        internal static void WriteSettings(PowerSettings settings)
        {using(var deployment=new DeploymentLock()) {Prepare();WriteSettings(Root,settings);}}
        internal static void WriteSettings(string root,PowerSettings settings)
        {
            settings.Validate();string file=Path.Combine(root,"settings.json");var json=new JavaScriptSerializer();
            var sections=File.Exists(file)?json.Deserialize<Dictionary<string,object>>(File.ReadAllText(file)):new Dictionary<string,object>();
            if(sections==null) throw new IOException("配置文件无效。");
            // Preserve other feature sections when only the power settings change.
            sections["Power"]=settings;Write(root,"settings.json",sections);
            string legacy=Path.Combine(root,"Power","settings.json");
            if(File.Exists(legacy)) {
                if((File.GetAttributes(legacy)&FileAttributes.ReparsePoint)!=0) throw new IOException("旧设置不能是符号链接。");
                File.Delete(legacy);
            }
            CleanLegacyDirectory(root);
        }
        internal static void WriteStatus(PowerStatus status)
        {Prepare();WriteStatus(Root,status);}
        internal static void WriteStatus(string root,PowerStatus status)
        {
            Write(Path.Combine(root,"State"),"power-status.json",status);
            string legacy=Path.Combine(root,"Power","status.json");
            if(File.Exists(legacy) && (File.GetAttributes(legacy)&FileAttributes.ReparsePoint)==0) File.Delete(legacy);
            CleanLegacyDirectory(root);
        }
        static void CleanLegacyDirectory(string root)
        {
            string directory=Path.Combine(root,"Power");
            if(Directory.Exists(directory) && (File.GetAttributes(directory)&FileAttributes.ReparsePoint)==0 && Directory.GetFileSystemEntries(directory).Length==0) Directory.Delete(directory);
        }
        static void Prepare()
        {if(!prepared) {DriverService.ProtectDirectory(Root);DriverService.ProtectDirectory(Path.Combine(Root,"State"));prepared=true;}}
        static void Write(string directory,string name,object data)
        {
            string target=Path.Combine(directory,name);
            if(File.Exists(target) && (File.GetAttributes(target)&FileAttributes.ReparsePoint)!=0) throw new IOException("省电文件不能是符号链接。");
            string temporary=Path.Combine(directory,Guid.NewGuid().ToString("N")+".tmp");
            try {
                File.WriteAllText(temporary,new JavaScriptSerializer().Serialize(data),Encoding.UTF8);
                if(File.Exists(target)) File.Replace(temporary,target,null);else File.Move(temporary,target);
            } finally {if(File.Exists(temporary)) File.Delete(temporary);}
        }
    }
    internal sealed class IdlePowerPolicy
    {
        DateTime? idleSince;
        internal bool WantsP8(PowerSettings settings,PowerSample sample,bool fullSpeed,DateTime now)
        {
            if(!settings.Enabled || fullSpeed || sample.Gpu>=settings.Threshold || sample.Video>0) {idleSince=null;return false;}
            if(!idleSince.HasValue || now<idleSince.Value) idleSince=now;
            return (now-idleSince.Value).TotalSeconds>=settings.IdleSeconds;
        }
        internal void Reset() {idleSince=null;}
    }
    internal sealed class PowerEngine
    {
        readonly IPowerGpu gpu;
        readonly IdlePowerPolicy policy=new IdlePowerPolicy();
        readonly Action<bool> intent;
        internal bool Limited {get;private set;}
        internal PowerEngine(IPowerGpu gpu,Action<bool> intent,bool recover=false) {this.gpu=gpu;this.intent=intent;Limited=recover;}
        internal void Release()
        {
            if(Limited) {gpu.Limit(0);Limited=false;intent(false);}
            policy.Reset();
        }
        internal PowerStatus Tick(PowerSettings settings,bool fullSpeed,bool pause,bool reset,DateTime now)
        {
            if(reset) Release();
            if(pause || !settings.Enabled) {Release();return new PowerStatus {Policy=pause?"已暂停 · 等待解锁完成":"已停用",LimitApplied=false};}
            var sample=gpu.Read();
            bool desired=policy.WantsP8(settings,sample,fullSpeed,now);
            if(desired && !Limited) {
                // Persist ownership before the write, so a crash can be recovered next launch.
                intent(true);Limited=true;
                try {gpu.Limit(8);} catch {Release();throw;}
            } else if(!desired && Limited) Release();
            return new PowerStatus {Policy=Limited?"空闲省电 · P8 限制":"自动性能",LimitApplied=Limited,Sample=sample,
                Detail=fullSpeed?"全速应用运行中":sample.Video>0?"检测到视频负载":sample.Gpu>=settings.Threshold?"GPU 负载达到阈值":"空闲计时中"};
        }
    }
    internal sealed class PowerCoordination : IDisposable
    {
        internal readonly EventWaitHandle Running,Request,Acknowledged;
        bool pause;
        internal PowerCoordination()
        {
            Running=Event("Running");Request=Event("PauseRequested");Acknowledged=Event("Paused");
        }
        static EventWaitHandle Event(string name)
        {
            var acl=new EventWaitHandleSecurity();
            foreach(var type in new[]{WellKnownSidType.LocalSystemSid,WellKnownSidType.BuiltinAdministratorsSid})
                acl.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(type,null),EventWaitHandleRights.FullControl,AccessControlType.Allow));
            bool created;return new EventWaitHandle(false,EventResetMode.ManualReset,@"Global\CMP90HX_Power_"+name,out created,acl);
        }
        internal static IDisposable Pause()
        {
            var coordination=new PowerCoordination();
            if(!coordination.Running.WaitOne(0)) {coordination.Dispose();return null;}
            coordination.pause=true;coordination.Acknowledged.Reset();coordination.Request.Set();
            if(!coordination.Acknowledged.WaitOne(10000)) {coordination.Dispose();throw new IOException("省电控制尚未释放限制，硬件操作未开始。请先停用省电再重试。");}
            return coordination;
        }
        public void Dispose()
        {
            if(pause) {Request.Reset();Acknowledged.Reset();}
            Running.Dispose();Request.Dispose();Acknowledged.Dispose();
        }
    }
    internal static class InspectorConflict
    {
        [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,int id);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process,int type,IntPtr information,uint length,out uint returned);
        internal static bool Running()
        {
            foreach(var process in Process.GetProcessesByName("nvidiaInspector")) using(process) {
                IntPtr handle=OpenProcess(0x1000,false,process.Id);
                if(handle==IntPtr.Zero) return true;
                try {
                    uint size;NtQueryInformationProcess(handle,60,IntPtr.Zero,0,out size);
                    if(size<16 || size>65536) return true;
                    IntPtr data=Marshal.AllocHGlobal((int)size);
                    try {
                        if(NtQueryInformationProcess(handle,60,data,size,out size)!=0) return true;
                        int length=(ushort)Marshal.ReadInt16(data);IntPtr pointer=Marshal.ReadIntPtr(data,8);
                        long start=data.ToInt64(),address=pointer.ToInt64();
                        if(address<start || address+length>start+size) return true;
                        string command=Marshal.PtrToStringUni(pointer,length/2);
                        if(command.IndexOf("-multiDisplayPowerSaver",StringComparison.OrdinalIgnoreCase)>=0) return true;
                    } finally {Marshal.FreeHGlobal(data);}
                } finally {CloseHandle(handle);}
            }
            return false;
        }
    }
    internal static class PowerMonitor
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint Notify(IntPtr context,uint type,IntPtr setting);
        [StructLayout(LayoutKind.Sequential)] struct Notification {internal IntPtr Callback,Context;}
        [DllImport("powrprof.dll")] static extern uint PowerRegisterSuspendResumeNotification(uint flags,ref Notification recipient,out IntPtr handle);
        [DllImport("powrprof.dll")] static extern uint PowerUnregisterSuspendResumeNotification(IntPtr handle);
        internal static int Run(string session)
        {
            using(var lifetime=SharedSynchronization.OpenMutex(@"Global\CMP90HX_Power_Monitor")) {
                bool owned=false;
                try {
                    try {owned=lifetime.WaitOne(0);} catch(AbandonedMutexException) {owned=true;}
                    return owned?RunExclusive(session):0;
                } finally {if(owned) lifetime.ReleaseMutex();}
            }
        }
        static int RunExclusive(string session)
        {
            using(var coordination=new PowerCoordination()) {
                if(coordination.Running.WaitOne(0)) return 0;
                var prior=PowerStorage.ReadStatus();bool recover=prior!=null && prior.LimitApplied;
                PowerEngine engine=null;NvidiaPowerApi api=null;string last="";
                int reset=1;IntPtr notification=IntPtr.Zero;
                Notify callback=(context,type,setting)=>{if(type==4 || type==18 || type==7) Interlocked.Exchange(ref reset,1);return 0;};
                var registration=new Notification {Callback=Marshal.GetFunctionPointerForDelegate(callback)};
                PowerRegisterSuspendResumeNotification(2,ref registration,out notification);
                var status=new PowerStatus {LimitApplied=recover};
                Action<string> log=line=> {
                    string file=Path.Combine(session,"power-monitor.log");
                    if(File.Exists(file) && new FileInfo(file).Length>1024*1024) File.WriteAllText(file,"日志容量达到 1 MiB，已开始新的记录。\r\n");
                    File.AppendAllText(file,DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")+" "+line+"\r\n",Encoding.UTF8);
                };
                coordination.Running.Set();
                using(var mutex=SharedSynchronization.OpenMutex(SharedSynchronization.HardwareName)) {
                    try {
                        while(true) {
                            bool owned=false;
                            try {
                                var settings=PowerStorage.ReadSettings();bool pause=coordination.Request.WaitOne(0);
                                if(!settings.Enabled && api==null && !recover) {
                                    PowerStorage.WriteStatus(new PowerStatus {Policy="已停用",HeartbeatUtc=DateTime.UtcNow});break;
                                }
                                if(!pause) {
                                    try {owned=mutex.WaitOne(0);} catch(AbandonedMutexException) {owned=true;}
                                    if(!owned) {Thread.Sleep(100);continue;}
                                }
                                if(pause && api==null && !recover) {
                                    // No owned limit exists: acknowledge even before NVIDIA is loaded.
                                    status=new PowerStatus {Policy="已暂停 · 等待解锁完成",HeartbeatUtc=DateTime.UtcNow};
                                    PowerStorage.WriteStatus(status);coordination.Acknowledged.Set();
                                    Thread.Sleep(500);continue;
                                }
                                if(api==null) {
                                    if(!settings.Enabled && !recover) break;
                                    api=new NvidiaPowerApi();
                                    engine=new PowerEngine(api,limited=> {
                                        recover=limited;status.LimitApplied=limited;status.HeartbeatUtc=DateTime.UtcNow;PowerStorage.WriteStatus(status);
                                    },recover);
                                }
                                // A second power controller would otherwise overwrite the same limit.
                                bool conflict=InspectorConflict.Running();
                                status=engine.Tick(settings,settings.HasFullSpeedApp(),pause || conflict,Interlocked.Exchange(ref reset,0)!=0,DateTime.UtcNow);
                                if(conflict) {status.Policy="等待退出 Inspector 省电模式";status.Detail="退出 Multi Display Power Saver，避免两个控制器覆盖限制。";}
                                if(pause) coordination.Acknowledged.Set();else coordination.Acknowledged.Reset();
                                status.HeartbeatUtc=DateTime.UtcNow;PowerStorage.WriteStatus(status);
                                if(status.Policy!=last) {last=status.Policy;log(last+" "+status.Detail);}
                                if(!settings.Enabled && !engine.Limited) break;
                            } catch(Exception error) {
                                if(engine!=null) {try {engine.Release();} catch(Exception release) {log("恢复自动性能失败："+release.Message);}}
                                if(api!=null) {api.Dispose();api=null;engine=null;}
                                status=new PowerStatus {Policy="等待设备 / 驱动恢复",Detail=error.Message,LimitApplied=recover,HeartbeatUtc=DateTime.UtcNow};
                                PowerStorage.WriteStatus(status);
                                if(last!=error.Message) {last=error.Message;log(error.Message);}
                                Interlocked.Exchange(ref reset,1);
                            } finally {if(owned) mutex.ReleaseMutex();}
                            Thread.Sleep(500);
                        }
                        return 0;
                    } finally {
                        if(notification!=IntPtr.Zero) PowerUnregisterSuspendResumeNotification(notification);
                        GC.KeepAlive(callback);
                        try {if(engine!=null && engine.Limited) using(var gate=new PowerHardwareGate()) engine.Release();}
                        finally {
                            if(api!=null) api.Dispose();
                            coordination.Running.Reset();coordination.Acknowledged.Reset();
                        }
                    }
                }
            }
        }
    }
}
