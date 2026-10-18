using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

namespace CMP90HX.Control {
    internal static class BackgroundProgram {
        static int Main(string[] args) {
            if(args.Length==3 && args[0]=="--cleanup-data") {
                try {return AppData.CleanupAfterExit(Int32.Parse(args[1]),Int32.Parse(args[2]));}
                catch(Exception error){Console.Error.WriteLine(error);return 1;}
            }
            try {
                if(args.Length==2 && args[0]=="--validate-package") {
                    var paths=new RuntimePaths(AppDomain.CurrentDomain.BaseDirectory);
                    using(var writer=new StreamWriter(Path.GetFullPath(args[1]))) {
                        Action<string> log=writer.WriteLine;
                        return new NativeWorkflow(paths,new WindowsWorkflowPlatform(paths,log),log).Execute("Check",Path.Combine(Path.GetTempPath(),"CMP90HX-package-check",Guid.NewGuid().ToString("N")));
                    }
                }
                bool diagnostic=args.Contains("--diagnostics");
                string command=null,data=null;
                for(int i=0;i<args.Length;i++) {
                    if(args[i]=="--request" && i+1<args.Length) command=args[++i];
                    else if(args[i]=="--data" && i+1<args.Length) data=args[++i];
                    else if(args[i]!="--startup" && args[i]!="--notify-resume" && args[i]!="--diagnostics" && args[i]!="--disable-auto-unlock") throw new ArgumentException("未知后台参数："+args[i]);
                }
                if(command!=null) {var result=BackendClient.Execute(command,data,log:Console.WriteLine);Console.WriteLine(ControlMemory.Json().Serialize(result));return result.Code;}
                if(!diagnostic)using(var identity=WindowsIdentity.GetCurrent()) if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException("后台需要管理员或 SYSTEM 权限。");
                using(var singleton=ControlMemory.OpenLock(diagnostic?ControlMemory.DiagnosticPrefix+"Background.v1":"Global\\CMP90HX.Control.Background.v1",diagnostic)) {
                    bool owned=false;try {
                        try {owned=singleton.WaitOne(0);} catch(AbandonedMutexException) {owned=true;}
                        if(!owned) {
                            BackendClient.Ensure(args.Contains("--disable-auto-unlock"));
                            if(args.Contains("--startup")) return BackendClient.Execute("Startup",args.Contains("--disable-auto-unlock")?"disabled":null).Code;
                            if(args.Contains("--notify-resume")) return BackendClient.Execute("Resume",args.Contains("--disable-auto-unlock")?"disabled":null).Code;
                            return 0;
                        }
                        using(var host=new BackgroundHost(diagnostic,args.Contains("--disable-auto-unlock"))) return host.Run(args.Contains("--startup"),args.Contains("--notify-resume"));
                    } finally {if(owned)singleton.ReleaseMutex();}
                }
            } catch(Exception error) {
                Console.Error.WriteLine(error);
                // Even an early configuration failure must be diagnosable without a window.
                string root=AppData.Root;
                try {Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"background-startup-error.log"),error.ToString());} catch(IOException) {}
                return 1;
            }
        }
    }
    internal sealed class BackgroundHost : IDisposable {
        readonly bool diagnostic; bool disableAutomatic;
        readonly object sync=new object(),logSync=new object();
        readonly ControlMemory memory;
        readonly BlockingCollection<IpcRequest> jobs=new BlockingCollection<IpcRequest>(32);
        readonly ControlState state=new ControlState {BackendRevision=ControlMemory.BackendRevision,Pid=Process.GetCurrentProcess().Id,Instance=Guid.NewGuid().ToString("N")};
        readonly RuntimePaths paths=new RuntimePaths(AppDomain.CurrentDomain.BaseDirectory);
        Thread worker;
        volatile bool stop,suspended;
        int powerReset=1,resumePending;
        DateTime lastResume=DateTime.MinValue,lastPublish=DateTime.MinValue;
        NvidiaPowerApi api;PowerEngine engine;
        bool recover;
        readonly LegacyPowerMigration legacyPower=new LegacyPowerMigration();
        string migrationError;
        DateTime nextRecovery=DateTime.MinValue;
        LogSession session;IDisposable lease;
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint Notify(IntPtr context,uint type,IntPtr setting);
        [StructLayout(LayoutKind.Sequential)] struct Recipient {internal IntPtr Callback,Context;}
        [DllImport("powrprof.dll")] static extern uint PowerRegisterSuspendResumeNotification(uint flags,ref Recipient recipient,out IntPtr registration);
        [DllImport("powrprof.dll")] static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registration);
        Notify callback;IntPtr notification;
        internal BackgroundHost(bool diagnostic,bool disableAutomatic) {
            this.diagnostic=diagnostic;this.disableAutomatic=disableAutomatic;memory=new ControlMemory(true,diagnostic);
            memory.ResetRequest();
            if(!diagnostic) {
                DriverService.ProtectDirectory(PowerStorage.Root);DriverService.ProtectDirectory(paths.Logs);
                session=new LogSession(paths.Logs);lease=RuntimeDeployment.Lease(AppDomain.CurrentDomain.BaseDirectory);
                var prior=PowerStorage.ReadStatus();recover=prior!=null && prior.LimitApplied;
                callback=(context,type,setting)=> {
                    if(type==4) suspended=true;
                    if(type==7 || type==18) {suspended=false;Interlocked.Exchange(ref powerReset,1);Interlocked.Exchange(ref resumePending,1);}
                    return 0;
                };
                var recipient=new Recipient {Callback=Marshal.GetFunctionPointerForDelegate(callback)};
                uint code=PowerRegisterSuspendResumeNotification(2,ref recipient,out notification);
                state.WakeNotifications=code==0;
                Append("WAKE_NOTIFICATION_REGISTER status="+code+"; hardware operations run on the worker, never in the callback.");
                Reload();
                if(!state.WakeNotifications) {state.WakeNotificationError="唤醒通知注册失败："+new System.ComponentModel.Win32Exception((int)code).Message;state.Error=state.WakeNotificationError;Append(state.WakeNotificationError);}
            }
        }
        void Reload() {var options=BackendConfig.Read();var power=PowerStorage.ReadSettings();lock(sync) {state.Options=options;state.Power=power;state.AutoUnlockDisabled=StartupSettings.AutoUnlockDisabled(disableAutomatic,PowerStorage.Root);}}
        void Publish() {lock(sync) {state.Heartbeat=DateTime.UtcNow;memory.Publish(state);}}
        internal int Run(bool startup,bool resume) {
            Publish();worker=new Thread(Work) {IsBackground=false,Name="CMP90HX hardware owner"};worker.Start();
            if(startup) QueueAutomatic("Startup",null);
            if(resume) QueueAutomatic("Resume",null);
            while(!stop) {
                var request=memory.Request();
                if(request!=null && request.Id!=state.Acknowledged) {
                    if(String.IsNullOrEmpty(request.Id) || request.Id.Length>64 || String.IsNullOrEmpty(request.Command) || request.Command.Length>64) Complete(request,1,"请求无效。",null,null);
                    else if(!jobs.TryAdd(request)) Complete(request,1,"后台队列已满，操作没有执行。",null,null);
                    lock(sync) state.Acknowledged=request.Id;
                }
                if(Interlocked.Exchange(ref resumePending,0)!=0) {
                    lock(sync){state.Snapshot=null;state.SnapshotUtc=null;state.UnlockState="唤醒后待读取";}
                    QueueAutomatic("Resume",null);
                }
                if(request!=null || (DateTime.UtcNow-lastPublish).TotalMilliseconds>=250) {Publish();lastPublish=DateTime.UtcNow;}
                Thread.Sleep(50);
            }
            jobs.CompleteAdding();worker.Join();return 0;
        }
        void QueueAutomatic(string command,string data) {jobs.TryAdd(new IpcRequest {Id=Guid.NewGuid().ToString("N"),Command=command,Data=data,SubmittedUtc=DateTime.UtcNow});}
        void Work() {
            while(!stop || jobs.Count>0) {
                IpcRequest request;
                if(jobs.TryTake(out request,250)) Execute(request);
                else if(!diagnostic && !suspended) {
                    try {
                        Reload();
                        if(DateTime.UtcNow>=nextRecovery && NativePnp.Enumerate().Any(IsSessionDevice)) {
                            nextRecovery=DateTime.UtcNow.AddSeconds(30);
                            Execute(new IpcRequest {Id=Guid.NewGuid().ToString("N"),Command="RestoreNvidia"});
                        }
                        Telemetry();

                    } catch(Exception error) {lock(sync) state.Error=error.Message;}
                    Thread.Sleep(750);
                }
            }
        }
        void Append(string line) {
            string text=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")+" "+line+"\r\n";
            lock(sync) {state.Log+=text;if(state.Log.Length>60000)state.Log=state.Log.Substring(state.Log.Length-50000);state.LogRevision++;}
            if(session!=null) lock(logSync) {
                try {
                    string file=Path.Combine(session.DirectoryPath,"background.log");
                    if(File.Exists(file) && new FileInfo(file).Length>4*1024*1024)File.WriteAllText(file,"日志轮换\r\n");
                    File.AppendAllText(file,text);
                }catch(IOException logError){lock(sync)state.Error="日志写入失败："+logError.Message;}
            }
        }
        void Complete(IpcRequest request,int code,string error,string snapshot,string directory) {
            lock(sync) {
                state.Results.Add(new IpcResult {Id=request.Id,Code=code,Error=error,Snapshot=snapshot,Directory=directory,Log=state.JobId==request.Id?(state.Log.Length>16000?state.Log.Substring(state.Log.Length-16000):state.Log):null});
                while(state.Results.Count>8)state.Results.RemoveAt(0);
            }
        }
        void Execute(IpcRequest request) {
            string directory=null,snapshot=null,error=null;int code=0;
            lock(sync) {state.Busy=true;state.JobId=request.Id;state.Operation=request.Command;state.Log="";state.LogRevision++;state.Error=null;}
            try {
                if(diagnostic) {
                    if(request.Command=="Echo")Append(request.Data??"");
                    else if(request.Command=="StopDiagnostic" || request.Command=="Shutdown")stop=true;
                    else if(request.Command!="Ping" && request.Command!="RefreshDevices")throw new IOException("诊断模式禁止硬件、配置操作。");
                } else {
                    if(request.Command=="RefreshDevices") {RefreshDevices();return;}
                    MigrateLegacyPower();
                    Reload();
                    string command=request.Command;
                    if(command=="Startup" || command=="Resume") {
                        if(request.Data=="disabled") {disableAutomatic=true;Reload();}
                        if(command=="Resume") {
                            DateTime received=request.SubmittedUtc==DateTime.MinValue?DateTime.UtcNow:request.SubmittedUtc;
                            if(Math.Abs((received-lastResume).TotalSeconds)<10) {Append("RESUME_DUPLICATE_SKIPPED");return;}
                            lastResume=received;Interlocked.Exchange(ref powerReset,1);
                            lock(sync) {state.Snapshot=null;state.SnapshotUtc=null;state.UnlockState="唤醒后待读取";}
                        }
                        bool unlock=command=="Startup"?state.Options.UnlockOnStartup:state.Options.UnlockOnResume;
                        if(StartupSettings.AutoUnlockDisabled(disableAutomatic || request.Data=="disabled",PowerStorage.Root)) unlock=false;
                        if(!unlock) {Append("AUTO_UNLOCK_DISABLED: configuration; power policy is checked independently.");return;}
                        command="AutoUnlock";
                    }
                    if(command=="Ping") return;
                    if(command=="DisableAutomatic") {disableAutomatic=true;Reload();Append("AUTO_UNLOCK_DISABLED: frontend startup switch; until backend restart.");return;}
                    if(command=="Unlock" || command=="AutoUnlock" || command=="Status" || command=="Verify") lock(sync){state.Snapshot=null;state.SnapshotUtc=null;state.UnlockState="正在"+command;}
                    directory=Path.Combine(session.DirectoryPath,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
                    RestoreNvidiaIfNeeded(directory);
                    ReleasePower();
                    switch(command) {
                        case "RestoreNvidia":break;
                        case "SwitchBackend":
                            if(request.Data!=state.Options.Backend)throw new IOException("当前模式已经变化，请刷新后再次切换。");
                            var switched=ControlMemory.Json().Deserialize<ControlOptions>(ControlMemory.Json().Serialize(state.Options));
                            string previous=switched.Backend;switched.Backend=previous=="Native"?"Session":"Native";
                            RemoveUnlockDriver(previous);BackendConfig.Save(switched);Reload();
                            if(state.Options.Backend!=switched.Backend)throw new IOException("切换模式后配置校验失败。");
                            lock(sync){state.Snapshot=null;state.SnapshotUtc=null;state.UnlockState="模式已切换，请手动安装 "+switched.Backend+" 解锁驱动。";}
                            Append("BACKEND_SWITCHED: "+previous+" -> "+state.Options.Backend+"; configuration saved; install unlock driver manually.");break;
                        case "SetOptions":
                            var options=ControlMemory.Json().Deserialize<ControlOptions>(request.Data);if(options==null)throw new IOException("缺少配置。");options.Validate();
                            if(options.Backend!=state.Options.Backend) {RemoveUnlockDriver(state.Options.Backend);Append("BACKEND_CHANGED: previous unlock driver removed; install the selected driver manually.");}
                            BackendStartup.Update(options.StartOnBoot,paths,()=>BackendConfig.Save(options),Append);Reload();lock(sync){state.Snapshot=null;state.SnapshotUtc=null;state.UnlockState="配置更新后待读取";}break;
                        case "PowerEnable":
                            var settings=ControlMemory.Json().Deserialize<PowerSettings>(request.Data);if(settings==null)throw new IOException("缺少省电设置。");settings.Validate();
                            if(InspectorConflict.Running())throw new IOException("请先关闭 Inspector Multi Display Power Saver。");
                            // The resident backend owns the power policy.
                            settings.Enabled=true;PowerStorage.WriteSettings(settings);Reload();break;
                        case "Shutdown":stop=true;Append("BACKGROUND_SHUTDOWN_SAFE: queued operations complete before exit.");break;
                        case "PowerDisable":var disabled=PowerStorage.ReadSettings();disabled.Enabled=false;PowerStorage.WriteSettings(disabled);Reload();Append("POWER_SAVER_DISABLED");break;
#if ENABLE_ROOT_CERTIFICATES
                        case "CertificatesInstall":CertificateManager.Install(paths,Append);break;
                        case "CertificatesRemove":CertificateManager.Uninstall(Append);break;
#endif
                        case "ReaderInstall":
                            CertificateManager.EnsureInstalled(paths,Append);
                            try {SharedReader.Ensure(paths,Append,true);}
                            catch(IOException signature) {
                                if(signature.Message.Contains("0x800b0100"))throw new IOException("读取驱动尚未签名："+SharedReader.Driver(paths)+"。请先签名 build/reader/CMP90HXReader.sys，运行 build-reader-driver.ps1 -UpdatePinsOnly 后重新编译 Console，再手动安装。",signature);
                                throw;
                            }break;
                        case "ReaderRemove":ReaderService.Uninstall(Append);break;
                        case "UnlockDriverInstall":
                            if(request.Data!=state.Options.Backend)throw new IOException("解锁方式已改变，请重新确认安装。");
                            if(state.Options.Backend=="Native") {SystemDmaProtection.RequireNative(Append);CertificateManager.EnsureInstalled(paths,Append);DriverService.EnsureRunning(paths.DmaDriver,Append,true);}
                            else {CertificateManager.EnsureInstalled(paths,Append);SessionDriverService.Install(Append);}break;
                        case "UnlockDriverRemove":RemoveUnlockDriver(state.Options.Backend);break;
                        case "UninstallAll":
                            var withoutStartup=ControlMemory.Json().Deserialize<ControlOptions>(ControlMemory.Json().Serialize(state.Options));withoutStartup.StartOnBoot=false;
                            BackendStartup.Update(false,paths,()=>BackendConfig.Save(withoutStartup),Append);
                            SessionDriverService.Uninstall(Append);DriverService.Uninstall(Append);ReaderService.Uninstall(Append);
#if ENABLE_ROOT_CERTIFICATES
                            CertificateManager.Uninstall(Append);
#endif
                            var powerOff=PowerStorage.ReadSettings();powerOff.Enabled=false;PowerStorage.WriteSettings(powerOff);stop=true;break;
                        case "Environment":
                            var dma=SystemDmaProtection.Check(Append);
                            if(!dma.HasValue)throw new IOException("无法确认内核 DMA 保护状态，请在系统信息中检查。");
                            if(dma.Value && state.Options.Backend=="Native")throw new IOException("内核 DMA 保护已开启，无法使用 Native 模式，请在设置中切换到 Session。");
                            Append("ENVIRONMENT_CHECK_PASSED: "+(dma.Value?"内核 DMA 保护已开启，Session 模式可用。":"内核 DMA 保护未开启。"));break;
                        case "Check":case "Status":case "Unlock":case "AutoUnlock":case "Verify":
                            if(command=="Status" || command=="Verify" || command=="Unlock" || command=="AutoUnlock") lock(sync){state.Snapshot=null;state.SnapshotUtc=null;state.UnlockState="本次操作待读取";}
                            if((command=="Unlock" || command=="AutoUnlock") && state.Options.Backend=="Native")SystemDmaProtection.RequireNative(Append);

                            code=(command=="Status" || command=="Verify")?ReadSharedStatus(command,directory):state.Options.Backend=="Session"?SessionOperation(command,directory):new NativeWorkflow(paths,new WindowsWorkflowPlatform(paths,Append),Append,conservative:state.Options.ConservativeTiming).Execute(command,directory);
                            string file=Path.Combine(directory,"unlock-status.json");
                            if(code==0 && File.Exists(file)) {
                                snapshot=File.ReadAllText(file);
                                lock(sync) {state.Snapshot=snapshot;state.SnapshotUtc=DateTime.UtcNow;state.UnlockState="已采集";}
                            } else if(command=="Status" || command=="Unlock" || command=="AutoUnlock") lock(sync) {state.Snapshot=null;state.SnapshotUtc=null;state.UnlockState="未取得当前快照";}
                            break;
                        default:throw new IOException("不支持的后台指令："+command);
                    }
                }
            } catch(Exception failure) {error=failure.Message;code=1;Append("BACKGROUND_FAILED: "+failure);}
            finally {
                if(!diagnostic && request.Command!="RefreshDevices")try {RefreshDevices();}catch(Exception metadata){Append("DEVICE_METADATA_UNAVAILABLE: "+metadata.Message);}
                lock(sync) {state.Error=error;Complete(request,code,error,snapshot,directory);state.Busy=false;}
                try{Publish();}catch(IOException publication){Console.Error.WriteLine("IPC publication failed after hardware completion: "+publication.Message);}
            }
        }
        void RemoveUnlockDriver(string backend) {if(backend=="Native")DriverService.Uninstall(Append);else SessionDriverService.Uninstall(Append);}
        int ReadSharedStatus(string command,string directory) {
            if(!ReaderService.Installed())throw new IOException("READER_DRIVER_NOT_INSTALLED: 请在驱动管理中手动安装读取驱动。");
            var target=NativePnp.Unique();if(target.Problem!=0)throw new IOException("当前显卡不是 Code 0，暂停实时状态读取。");
            string file=Path.Combine(directory,"unlock-status.json");
            int result=SharedReader.Capture(paths,target.Bdf,file,Append);if(result!=0)return result;
            var sample=UnlockSnapshot.Parse(File.ReadAllText(file));
            if(sample.GpuBdf!=target.BusAddress || NativePnp.Problem(target.InstanceId)!=0){File.Delete(file);throw new IOException("采样期间设备状态改变。");}
            bool verified=false;try {NativeWorkflow.ValidateVerified(sample,1,1);verified=true;}catch(IOException){}
            Append(verified?"UNLOCK_STATUS_VERIFIED":"UNLOCK_STATUS_NOT_FULLY_VERIFIED");
            if(command=="Verify" && !verified)return 1;return 0;
        }
        string SessionWorker() {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"session","CMP90HXUnlocker.exe");
        }
        int SessionOperation(string command,string directory) {
            string worker=SessionWorker();if(!File.Exists(worker))throw new FileNotFoundException("缺少 Session 成品包，请放入 Console/backends/session 并重新编译 Console。",worker);
            BackendPackagePolicy.VerifySession(Path.GetDirectoryName(worker));
            if(command=="Check") {Append("SESSION_WORKER_AVAILABLE version="+BackendPackagePolicy.SessionVersion+" "+worker);return 0;}
            string installedInf=SessionDriverService.RequireInstalled();
            var timer=Stopwatch.StartNew();GpuDevice target=null;
            do {try {target=NativePnp.Unique();break;}catch(IOException) {if(command!="AutoUnlock")throw;Thread.Sleep(500);}}while(timer.ElapsedMilliseconds<30000);
            if(target==null)throw new IOException("等待 90HX 就绪超时。");
            if(command=="AutoUnlock" && target.Problem==0) {
                try {
                    if(ReadSharedStatus("Status",directory)==0) {
                        NativeWorkflow.ValidateVerified(UnlockSnapshot.Parse(File.ReadAllText(Path.Combine(directory,"unlock-status.json"))),1,1);
                        Append("AUTO_UNLOCK_ALREADY_VERIFIED: shared read-only current snapshot; no driver rebind.");return 0;
                    }
                }catch(Exception unavailable){Append("AUTO_UNLOCK_LIVE_READ_UNAVAILABLE: "+unavailable.Message);}
                string stale=Path.Combine(directory,"unlock-status.json");if(File.Exists(stale))File.Delete(stale);
            }
            int result=RunProcess(worker,"session-unlock --session-inf "+WindowsWorkflowPlatform.Quote(installedInf)+" --bdf "+target.Bdf+" --observe-seconds 0 --restore-mode update --log "+WindowsWorkflowPlatform.Quote(Path.Combine(directory,"session.log")));
            if(result!=0) {
                string sessionLog=Path.Combine(directory,"session.log");
                if(File.Exists(sessionLog) && File.ReadAllText(sessionLog).Contains("Driver binding requires reboot; no unlock IOCTL will be submitted.")) {
                    bool restored=File.ReadAllText(sessionLog).Contains("SESSION_RESTORE_COMPLETE") && !File.Exists(Path.Combine(PowerStorage.Root,"session-restore.json"));
                    throw new IOException("SESSION_DRIVER_UPDATE_REQUIRES_REBOOT: Windows 要求重启后才能切换到 Session 驱动，本次未提交解锁。"+(restored?"NVIDIA 已恢复，恢复记录已清除；请重启 Windows 后再解锁，无需 session-restore。":"请先检查 NVIDIA 和 session-restore.json 的恢复状态，不要重复解锁。"));
                }
            }
            if(result==0) {
                Append("SESSION_UNLOCK_COMPLETE: unlock transaction succeeded and NVIDIA restored.");
                try {if(ReadSharedStatus("Status",directory)!=0)Append("POST_UNLOCK_READ_UNAVAILABLE: reader returned failure.");}catch(Exception read){Append("POST_UNLOCK_READ_UNAVAILABLE: "+read.Message);}
            }return result;
        }
        int RunProcess(string exe,string arguments) {
            using(var process=new Process {StartInfo=new ProcessStartInfo(exe,arguments) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=System.Text.Encoding.UTF8,StandardErrorEncoding=System.Text.Encoding.UTF8,WorkingDirectory=Path.GetDirectoryName(exe)}}) {
                process.OutputDataReceived+=(s,e)=>{if(e.Data!=null)Append(e.Data);};process.ErrorDataReceived+=(s,e)=>{if(e.Data!=null)Append(e.Data);};
                process.Start();process.BeginOutputReadLine();process.BeginErrorReadLine();process.WaitForExit();return process.ExitCode;
            }
        }
        static bool IsSessionDevice(GpuDevice device) {return String.Equals(device.Service,"CMP90HXUnlockSession",StringComparison.OrdinalIgnoreCase);}
        static string BindingName(GpuDevice device) {
            if(device==null)return "无设备";
            if(IsSessionDevice(device))return "Session";
            return String.Equals(device.Service,"nvlddmkm",StringComparison.OrdinalIgnoreCase)?"NVIDIA":"未知";
        }
        void RestoreNvidiaIfNeeded(string directory) {
            var devices=NativePnp.Enumerate();
            if(!devices.Any(IsSessionDevice))return;
            if(devices.Count!=1)throw new IOException("检测到 Session 接管，但显卡不唯一，无法自动恢复 NVIDIA。");
            var target=devices[0];
            if(!File.Exists(Path.Combine(PowerStorage.Root,"session-restore.json")))
                throw new IOException("SESSION_RECOVERY_RECORD_MISSING: 显卡由 Session 接管，但缺少原驱动恢复记录，无法安全自动恢复 NVIDIA。");
            string worker=SessionWorker();
            BackendPackagePolicy.VerifySession(Path.GetDirectoryName(worker));
            // Invalidate cached NVAPI handles across a function-driver transition. Keep persisted power recovery intent.
            if(api!=null)api.Dispose();api=null;engine=null;
            Append("SESSION_AUTO_RESTORE_BEGIN: 显卡当前由 Session 接管，先检查 DMA 安全状态并恢复 NVIDIA，再继续原操作。");
            Publish();
            string log=Path.Combine(directory,"session-auto-restore.log");
            int result=RunProcess(worker,"session-restore --bdf "+target.Bdf+" --log "+WindowsWorkflowPlatform.Quote(log));
            if(result!=0) {
                string details=File.Exists(log)?File.ReadAllText(log):"";
                throw new IOException(SessionRecoveryFailure.ClassifyWorkerLog(details).Message);
            }
            var restored=NativePnp.Unique();
            if(restored.InstanceId!=target.InstanceId || restored.Problem!=0 || BindingName(restored)!="NVIDIA")
                throw new IOException("SESSION_AUTO_RESTORE_FAILED: NVIDIA 尚未正常接管，本次操作已停止。");
            Interlocked.Exchange(ref powerReset,1);
            Append("SESSION_AUTO_RESTORE_COMPLETE: NVIDIA Code 0，继续原操作。");
        }
        static string ShortDriverState(string value) {
            if(String.IsNullOrEmpty(value))return "待读取";
            if(value.Contains("重启"))return "待重启";
            if(value.Contains("其他版本"))return "待更新";
            if(value.StartsWith("未安装"))return "未安装";
            if(value.StartsWith("运行中"))return "运行中";
            if(value.StartsWith("已安装"))return "已安装";
            return "异常";
        }
        void RefreshDevices() {
            var devices=NativePnp.Enumerate();var certificates=CertificateManager.Installed();string nativeDriver=DriverService.CurrentState(),readerDriver=ReaderService.CurrentState(),sessionDriver=SessionDriverService.CurrentState();
            lock(sync) {state.DeviceCount=devices.Count;state.GpuName=devices.Count>0?devices[0].Name:"未发现 90HX";state.ProblemCode=devices.Count>0?(int?)devices[0].Problem:null;state.DeviceDriver=devices.Count==1?BindingName(devices[0]):"无唯一设备";state.DriverState=state.Options.Backend+"："+ShortDriverState(state.Options.Backend=="Native"?nativeDriver:sessionDriver)+" · "+state.DeviceDriver+"\n读取："+ShortDriverState(readerDriver);state.Certificates=certificates;state.ReaderDriverState=readerDriver;state.NativeDriverState=nativeDriver;state.SessionDriverState=sessionDriver;}
        }
        void ReleasePower() {
            if(NativePnp.Enumerate().Any(IsSessionDevice)) {
                if(api!=null)api.Dispose();api=null;engine=null;
                lock(sync)state.PowerStatus=new PowerStatus {Policy="等待 NVIDIA 恢复",HeartbeatUtc=DateTime.UtcNow,LimitApplied=recover};
                return;
            }
            if(recover && engine==null) {api=new NvidiaPowerApi();engine=new PowerEngine(api,limited=> {recover=limited;PowerStorage.WriteStatus(new PowerStatus {LimitApplied=limited,HeartbeatUtc=DateTime.UtcNow});},true);}
            if(engine!=null) {using(var gate=new PowerHardwareGate())engine.Release();}
            if(api!=null)api.Dispose();api=null;engine=null;
            lock(sync) state.PowerStatus=new PowerStatus {Policy="已暂停 · 等待硬件操作",HeartbeatUtc=DateTime.UtcNow,LimitApplied=recover};
        }
        void Telemetry() {
            MigrateLegacyPower();Reload();
            RefreshDevices();
            if(NativePnp.Enumerate().Any(IsSessionDevice)) {ReleasePower();return;}
            using(var gate=new PowerHardwareGate()) {
                try {
                    if(api==null) {api=new NvidiaPowerApi();engine=new PowerEngine(api,limited=>{recover=limited;PowerStorage.WriteStatus(new PowerStatus {LimitApplied=limited,HeartbeatUtc=DateTime.UtcNow});},recover);}
                    bool reset=Interlocked.Exchange(ref powerReset,0)!=0;
                    var value=engine.Tick(state.Power,state.Power.HasFullSpeedApp(),InspectorConflict.Running(),reset,DateTime.UtcNow);
                    if(value.Sample==null)value.Sample=api.Read();value.HeartbeatUtc=DateTime.UtcNow;
                    PowerStorage.WriteStatus(value);lock(sync)state.PowerStatus=value;
                } catch(Exception error) {
                    if(api!=null) {try {if(engine!=null)engine.Release();}finally{api.Dispose();api=null;engine=null;}}
                    lock(sync)state.PowerStatus=new PowerStatus {Policy="等待设备 / 驱动恢复",Detail=error.Message,HeartbeatUtc=DateTime.UtcNow,LimitApplied=recover};
                }
            }
        }
        void MigrateLegacyPower() {
            try {
                if(legacyPower.Run(Append)) {
                    var prior=PowerStorage.ReadStatus();recover=prior!=null && prior.LimitApplied;
                    Interlocked.Exchange(ref powerReset,1);Reload();
                }
                migrationError=null;
            } catch(Exception error) {
                lock(sync)state.PowerStatus=new PowerStatus {Policy="旧省电迁移暂缓",Detail=error.Message,HeartbeatUtc=DateTime.UtcNow,LimitApplied=recover};
                if(migrationError!=error.Message) {migrationError=error.Message;Append("LEGACY_POWER_MIGRATION_FAILED: "+error.Message);}
                throw;
            }
        }
        public void Dispose() {
            stop=true;if(worker!=null && worker.IsAlive){jobs.CompleteAdding();worker.Join();}
            if(notification!=IntPtr.Zero)PowerUnregisterSuspendResumeNotification(notification);GC.KeepAlive(callback);
            if(!diagnostic) {try {ReleasePower();}finally {if(lease!=null)lease.Dispose();if(session!=null)session.Dispose();}}
            memory.Dispose();jobs.Dispose();
        }
    }
}
