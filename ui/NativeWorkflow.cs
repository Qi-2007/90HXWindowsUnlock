using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CMP90HX.Control
{
    internal sealed class RuntimePaths
    {
        internal const string Version="1.2.4";
        // Updated by update-release-hashes.ps1 before the GUI is compiled.
        internal const string Gen2Hash="58C770D6330D42DF56EC84F986B873FD75C6FBB3F8A10650A810627CA5240CF3";
        internal const string DmaHash="96C6EEA49B73CC03B8B9420A928F72BBC038AC817C36D9227D01480E69AC37DD";
        internal const string CoreHash="B533B7B245ED606C151CA336B9A6BACEBE935E3EC478246E879C668A4DD98A6A";
        internal readonly string Root, Worker, Drivers, DmaDriver, Core, Logs, Certificates;
        internal readonly bool Packaged, Background;
        internal RuntimePaths(string directory)
        {
            directory=Path.GetFullPath(directory);
            Packaged=File.Exists(Path.Combine(directory,"runtime","CMP90HXUnlocker.exe"));
            Root=Packaged?directory:Path.GetFullPath(Path.Combine(directory,".."));
            Background=Packaged && RuntimeDeployment.IsBackgroundDirectory(Root);
            Worker=Packaged?Path.Combine(Root,"runtime","CMP90HXUnlocker.exe"):Path.Combine(Root,"build","CMP90HXUnlocker.exe");
            Drivers=Path.Combine(Root,"drivers");
            DmaDriver=Background?DriverService.StoredDriver:Packaged?Path.Combine(Root,"driver","CMP90HXDma.sys"):Path.Combine(Root,"build","dma-driver","CMP90HXDma.sys");
            Certificates=Packaged?Path.Combine(Root,"driver","cert"):CertificatePolicy.SourceDirectory;
            Core=Packaged?Path.Combine(Root,"core","nvpermissive-core.o"):Path.Combine(Root,"vendor","nvpermissive-dist-469dc0c","obj","nvpermissive-core.o");
            Logs=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"CMP90HX","Logs");
        }
        internal static string Hash(string path)
        {
            using(SHA256 sha=SHA256.Create()) using(FileStream stream=File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");
        }
        internal static void RequireHash(string path,string hash)
        {
            if(!File.Exists(path)) throw new FileNotFoundException("缺少运行文件："+path,path);
            if(!String.Equals(Hash(path),hash,StringComparison.OrdinalIgnoreCase)) throw new IOException("文件哈希不匹配："+path);
        }
        internal void Validate(bool requireCore,bool requireDma)
        {
            if(!File.Exists(Worker)) throw new FileNotFoundException("缺少硬件工作进程："+Worker);
            if(Gen2Hash.Length!=64) throw new IOException("运行文件哈希尚未更新，请运行 update-release-hashes.ps1 后重新构建 GUI。");
            RequireHash(Worker,Gen2Hash);
            if(Packaged) {
                var entries=new JavaScriptSerializer().Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(Root,"files.sha256.json")));
                foreach(string relative in new[]{"runtime/CMP90HXUnlocker.exe","runtime/CMP90HXUnlocker.exe.config","CMP90HXControl.exe","CMP90HXControl.exe.config"}) {
                    if(entries==null || !entries.ContainsKey(relative)) throw new IOException("运行包清单缺少 "+relative);
                    RequireHash(Path.Combine(Root,relative.Replace('/',Path.DirectorySeparatorChar)),entries[relative]);
                }
            } else {
                string stamp=Path.Combine(Root,"build","CMP90HXUnlocker.validated.sha256");
                RequireHash(Worker,File.ReadAllText(stamp).Trim());
            }
            if(requireCore) RequireHash(Core,CoreHash);
            if(requireDma) {
                if(DmaHash.Length!=64) throw new IOException("驱动哈希尚未更新，请签名后运行 update-release-hashes.ps1。");
                if(Background) CertificateManager.RequireInstalled(); else CertificateManager.ValidateFiles(this);
                RequireHash(DmaDriver,DmaHash); SignatureTrust.Verify(DmaDriver);
            }
        }
    }

    internal interface IWorkflowPlatform
    {
        GpuDevice Target();
        uint Problem(string id);
        void Enable(string id,bool enabled);
        void WaitDisabled(string id);
        void Delay(int milliseconds);
        bool DriverInstalled();
        void EnsureDriver(bool installIfMissing=true);
        void CheckEnvironmentDependencies();
        int Run(params string[] args);
    }
    internal sealed class WindowsWorkflowPlatform : IWorkflowPlatform
    {
        readonly RuntimePaths paths; readonly Action<string> log;
        internal WindowsWorkflowPlatform(RuntimePaths paths,Action<string> log) { this.paths=paths; this.log=log; }
        public GpuDevice Target() { return NativePnp.Unique(); }
        public uint Problem(string id) { return NativePnp.Problem(id); }
        public void Enable(string id,bool enabled) { NativePnp.Enable(id,enabled); }
        public void WaitDisabled(string id) { NativePnp.WaitDisabled(id); }
        public void Delay(int milliseconds) { Thread.Sleep(milliseconds); }
        public bool DriverInstalled() { return DriverService.Installed(); }
        public void EnsureDriver(bool installIfMissing=true) { CertificateManager.RequireInstalled(); DriverService.EnsureRunning(paths.DmaDriver,log,installIfMissing); }
        public void CheckEnvironmentDependencies()
        {
            bool[] installed=CertificateManager.Installed();
            for(int i=0;i<installed.Length;i++) log("CERTIFICATE_STATUS "+CertificateManager.Thumbprints[i]+" installed="+installed[i]);
            log("DMA_DRIVER_STATUS "+DriverService.CurrentState());
            CertificateManager.RequireInstalled(); DriverService.RequireInstalled();
        }
        internal static string Quote(string argument)
        {
            StringBuilder result=new StringBuilder("\""); int slashes=0;
            foreach(char value in argument) {
                if(value=='\\') { slashes++; continue; }
                if(value=='"') result.Append('\\',slashes*2+1);
                else result.Append('\\',slashes);
                result.Append(value); slashes=0;
            }
            result.Append('\\',slashes*2).Append('"'); return result.ToString();
        }
        public int Run(params string[] args)
        {
            using(Process process=new Process { StartInfo=new ProcessStartInfo {
                FileName=paths.Worker,Arguments=String.Join(" ",args.Select(Quote)),WorkingDirectory=paths.Root,
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
                StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8 } }) {
                process.OutputDataReceived+=(s,e)=>{ if(e.Data!=null) log(e.Data); };
                process.ErrorDataReceived+=(s,e)=>{ if(e.Data!=null) log(e.Data); };
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                // Hardware work is never killed on a timeout: DMA may still own buffers.
                process.WaitForExit(); return process.ExitCode;
            }
        }
    }
    internal sealed class NativeWorkflow
    {
        readonly RuntimePaths paths; readonly IWorkflowPlatform platform; readonly Action<string> log;
        readonly Action<bool,bool> validate;
        readonly bool conservative;
        string directory;
        internal NativeWorkflow(RuntimePaths paths,IWorkflowPlatform platform,Action<string> log,Action<bool,bool> validate=null,bool conservative=false)
        { this.paths=paths; this.platform=platform; this.log=log; this.validate=validate??paths.Validate; this.conservative=conservative; }
        void Require(int exit,string action) { if(exit!=0) throw new IOException(action+" failed: exit="+exit); }
        int Arena() { return platform.Run("arena-info","--physical-dma-experiment","--log",Path.Combine(directory,"arena.log")); }
        UnlockSnapshot Snapshot(GpuDevice target,string filename)
        {
            string output=Path.Combine(directory,filename);
            Require(platform.Run("snapshot","--drivers",paths.Drivers,"--bdf",target.Bdf,"--out",output,"--log",Path.Combine(directory,"snapshot.log")),"GPU snapshot");
            UnlockSnapshot snapshot=UnlockSnapshot.Parse(File.ReadAllText(output));
            if(snapshot.GpuBdf!=target.BusAddress) throw new IOException("快照 BDF 与选定设备不一致。");
            return snapshot;
        }
        internal static void ValidateVerified(UnlockSnapshot snapshot,int gpuWidth,int bridgeWidth)
        {
            foreach(var entry in new[]{new {Link=snapshot.Gpu,Width=gpuWidth},new {Link=snapshot.Bridge,Width=bridgeWidth}}) {
                if(entry.Link==null || !entry.Link.Status.HasValue || entry.Link.Status>=65535) throw new IOException("PCIe 状态无法读取。");
                uint status=entry.Link.Status.Value;
                if((status&15)!=2 || (status&0x800)!=0 || ((status>>4)&63)<Math.Max(1,entry.Width))
                    throw new IOException("Gen2、链路宽度或训练状态验证未通过。");
            }
            if(snapshot.FunctionState(0x82381c,0x88888888,0x823820,8)!="已解锁" || snapshot.FunctionState(0x823830,4)!="已解锁")
                throw new IOException("计算/图形解锁值未保持。");
        }
        void Verify(GpuDevice target,int gpuWidth,int bridgeWidth)
        {
            platform.Delay(conservative?3000:200);
            bool ready=false;
            for(int poll=0;poll<150;poll++) {
                uint problem=platform.Problem(target.InstanceId);
                if(problem==0) { ready=true; break; }
                if(problem==43) break;
                platform.Delay(200);
            }
            if(!ready) throw new IOException("NVIDIA/PnP 未正常接管显卡。");
            var state=Snapshot(target,"unlock-status.json");
            if(platform.Problem(target.InstanceId)!=0) throw new IOException("采样期间 PnP 状态已改变。");
            ValidateVerified(state,gpuWidth,bridgeWidth);
            log("VERIFY_SAMPLE=1 GPU="+UnlockSnapshot.DescribeLink(state.Gpu)+" BRIDGE="+UnlockSnapshot.DescribeLink(state.Bridge));
            log("FULL_UNLOCK_VERIFIED_AFTER_NVIDIA_REATTACH: Code 0, Gen2, width and overrides verified in one snapshot.");
        }
        GpuDevice WaitTarget()
        {
            for(int poll=0;poll<150;poll++) {
                try { return platform.Target(); }
                catch(IOException) { if(poll==149) throw; }
                if(poll==0) log("AUTO_WAIT_DEVICE: 等待 90HX 设备枚举完成。");
                platform.Delay(200);
            }
            throw new IOException("自动解锁等待设备超时。");
        }
        internal int Execute(string mode,string runDirectory)
        {
            directory=runDirectory; Directory.CreateDirectory(directory);
            using(Mutex mutex=platform is WindowsWorkflowPlatform && mode!="Check"?
                SharedSynchronization.OpenMutex(SharedSynchronization.HardwareName):new Mutex(false)) {
                bool owned=false;
                IDisposable powerPause=null;
                try {
                    try { owned=mutex.WaitOne(1000); } catch(AbandonedMutexException) { owned=true; }
                    if(!owned) throw new IOException("另一项工作流正在运行，请等待完成。");
                    if(!new[]{"Check","Status","Install","Preflight","Environment","AutoUnlock","Unlock","Verify"}.Contains(mode)) throw new ArgumentException("Unknown workflow.");
                    log("NATIVE_WORKFLOW_BEGIN mode="+mode);
                    if(mode=="Status" && !platform.DriverInstalled()) {
                        log("UNLOCK_STATUS_DRIVER_NOT_INSTALLED: 尚未安装 CMP90HXDma，无法读取解锁状态。请先在驱动管理中安装驱动。");
                        return 0;
                    }
                    bool automatic=mode=="AutoUnlock",environment=mode=="Environment";
                    if(mode=="Unlock" || automatic) log("GUI_UNLOCK_CORE=469dc0c timing="+(conservative?"conservative":"fast"));
                    if(environment || automatic) platform.CheckEnvironmentDependencies();
                    validate(mode=="Check" || mode=="Unlock" || environment || automatic,true);
                    if(mode=="Check") {
                        Require(platform.Run("self-test"),"Managed self-tests");
                        Require(platform.Run("core-test","--core",paths.Core),"Pinned core mock tests");
                        log("Full-test inputs validated. No firmware-variable read, driver load, PnP change or GPU access performed.");
                        return 0;
                    }
                    if(mode=="Install") { platform.EnsureDriver(); Require(Arena(),"DMA backend validation"); log("DMA_DRIVER_READY"); return 0; }
                    if(environment) {
                        Require(platform.Run("self-test"),"Managed self-tests");
                        Require(platform.Run("core-test","--core",paths.Core),"Pinned core mock tests");
                    }
                    GpuDevice target=automatic?WaitTarget():platform.Target(); log("Target: "+target.InstanceId+" / BDF="+target.Bdf);
                    if(platform is WindowsWorkflowPlatform) powerPause=PowerCoordination.Pause();
                    if(mode=="Status") {
                        platform.EnsureDriver(false);
                        var state=Snapshot(target,"unlock-status.json");
                        bool verified=false;
                        if(platform.Problem(target.InstanceId)==0) {
                            try { ValidateVerified(state,1,1); verified=true; } catch(IOException) { }
                        }
                        log(verified?"UNLOCK_STATUS_VERIFIED":"UNLOCK_STATUS_NOT_FULLY_VERIFIED");
                        log("UNLOCK_STATUS_CAPTURED"); return 0;
                    }
                    if(mode=="Verify") {
                        platform.EnsureDriver(false);
                        Verify(target,1,1); return 0;
                    }
                    platform.EnsureDriver(); Require(Arena(),"DMA backend validation");
                    if(mode=="Preflight" || environment) {
                        Require(platform.Run("dma-test","--drivers",paths.Drivers,"--physical-dma-experiment","--log",Path.Combine(directory,"dma-test.log")),"DMA mapping test");
                        Require(platform.Run("bridge-probe","--drivers",paths.Drivers,"--bdf",target.Bdf,"--out",Path.Combine(directory,"unlock-status.json"),"--log",Path.Combine(directory,"bridge-probe.log")),"Read-only bridge preflight");
                        log(environment?"ENVIRONMENT_CHECK_PASSED":"PREFLIGHT_PASSED"); return 0;
                    }
                    bool wasEnabled=platform.Problem(target.InstanceId)!=22;
                    if(automatic && !wasEnabled) throw new IOException("AUTO_REFUSED_DISABLED_DEVICE: 显卡已停用，需手动确认后解锁。");
                    UnlockSnapshot baseline=wasEnabled?Snapshot(target,"full-before.json"):null;
                    if(automatic && platform.Problem(target.InstanceId)==0) {
                        bool already=false;
                        try { ValidateVerified(baseline,1,1); already=true; } catch(IOException) { }
                        if(already) {
                            File.Copy(Path.Combine(directory,"full-before.json"),Path.Combine(directory,"unlock-status.json"),true);
                            log("AUTO_UNLOCK_ALREADY_VERIFIED: 当前状态已解锁，无需重复操作。"); return 0;
                        }
                    }
                    bool restore=false,attempted=false; int unlockExit=1;
                    try {
                        if(wasEnabled) { restore=true; platform.Enable(target.InstanceId,false); }
                        platform.WaitDisabled(target.InstanceId);
                        var current=platform.Target();
                        if(current.InstanceId!=target.InstanceId || current.BusAddress!=target.BusAddress) throw new IOException("Target changed before unlock.");
                        attempted=true;
                        var arguments=new List<string> {"full-unlock","--core",paths.Core,"--drivers",paths.Drivers,"--bdf",target.Bdf,
                            "--out",Path.Combine(directory,"full-unlock.json"),"--log",Path.Combine(directory,"full-unlock.log"),"--physical-dma-experiment"};
                        if(conservative) arguments.Add("--conservative");
                        unlockExit=platform.Run(arguments.ToArray());
                    } finally {
                        bool retained=unlockExit==6;
                        if(attempted && unlockExit!=0) {
                            try { retained=retained || Arena()!=0; } catch { retained=true; }
                        }
                        if(retained) log("DMA_RETAINED: keep 90HX disabled; cold boot before retrying.");
                        else if(restore) { log("Re-enabling the original 90HX PnP device."); platform.Enable(target.InstanceId,true); }
                    }
                    Require(unlockExit,"Full unlock");
                    if(!wasEnabled) {
                        // Reuse only this run's After snapshot, without touching PnP again.
                        var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(directory,"full-unlock.json")));
                        if(data==null || !data.ContainsKey("After")) throw new IOException("Missing full-unlock After snapshot.");
                        File.WriteAllText(Path.Combine(directory,"unlock-status.json"),new JavaScriptSerializer().Serialize(data["After"]),Encoding.UTF8);
                        log("FULL_UNLOCK_VERIFIED_WHILE_DISABLED_ONLY"); return 0;
                    }
                    Verify(target,Width(baseline.Gpu),Width(baseline.Bridge)); return 0;
                } catch(Exception error) { log("FAILED: "+error.Message); return 1; }
                finally { if(powerPause!=null) powerPause.Dispose();if(owned) mutex.ReleaseMutex(); }
            }
        }
        static int Width(UnlockSnapshot.Link link) { return link!=null && link.Status.HasValue ? (int)((link.Status.Value>>4)&63) : 1; }
    }
}
