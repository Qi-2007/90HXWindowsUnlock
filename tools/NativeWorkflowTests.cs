using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace CMP90HX.Control
{
    internal sealed class FakePowerGpu : IPowerGpu
    {
        internal PowerSample Sample=new PowerSample {Pstate=0,InstanceId="TEST_90HX"};
        internal readonly List<int> Writes=new List<int>();
        internal bool FailP8,FailRelease;
        public PowerSample Read() {return Sample;}
        public void Limit(int state) {Writes.Add(state);if(state==8 && FailP8 || state==0 && FailRelease) throw new IOException("mock NVAPI failure");}
        public void Dispose() { }
    }
    internal sealed class FakePlatform : IWorkflowPlatform
    {
        internal uint Code;
        internal int Exit,PostFailureArena,Disables,Enables,DriverCalls,TargetCalls,SnapshotCalls;
        internal bool Crash,DisableFails,EnableFails,BadVerify,BadStatus,ConservativeArgument,MissingDependencies,BaselineLocked;
        internal readonly List<string> Commands=new List<string>();
        int arenaCalls;
        public GpuDevice Target() { TargetCalls++; return new GpuDevice {InstanceId="TEST_90HX",BusAddress=512,Bdf="02:00.0",Problem=Code,Name="Fake"}; }
        public uint Problem(string id) { return Code; }
        public void Enable(string id,bool enabled) {
            if(enabled) { Enables++; if(EnableFails) throw new IOException("mock re-enable failure"); Code=0; }
            else { Disables++; if(DisableFails) throw new IOException("mock disable failure"); Code=22; }
        }
        public void WaitDisabled(string id) { if(Code!=22) throw new IOException("mock not disabled"); }
        public void Delay(int ms) { }
        public void EnsureDriver() { DriverCalls++; }
        public void CheckEnvironmentDependencies() { if(MissingDependencies) throw new IOException("mock missing certificate/driver"); }
        internal static string Json(bool bad)
        {
            return "{\"Schema\":1,\"DeviceId\":571281630,\"GpuBdf\":512,\"Gpu\":{\"Status\":258},\"Bridge\":{\"Status\":258},\"Registers\":[{\"Offset\":8534044,\"Value\":2290649224},{\"Offset\":8534048,\"Value\":8},{\"Offset\":8534064,\"Value\":"+(bad?"0":"4")+"}]}";
        }
        public int Run(params string[] args) {
            Commands.Add(args[0]);
            if(args[0]=="arena-info") return ++arenaCalls>1?PostFailureArena:0;
            if(args[0]=="full-unlock") {
                ConservativeArgument=args.Contains("--conservative");
                if(Crash) throw new IOException("mock worker crash");
                if(Exit==0) File.WriteAllText(args[Array.IndexOf(args,"--out")+1],"{\"After\":"+Json(false)+"}");
                return Exit;
            }
            if(args[0]=="snapshot" || args[0]=="bridge-probe") {
                SnapshotCalls++;
                File.WriteAllText(args[Array.IndexOf(args,"--out")+1],Json(BadStatus || BaselineLocked && SnapshotCalls==1 || BadVerify && SnapshotCalls>1));
            }
            return 0;
        }
    }
    internal static class NativeWorkflowTests
    {
        static int count;
        static void Require(bool condition,string name) { if(!condition) throw new Exception(name); Console.WriteLine("PASS "+name); count++; }
        static int Run(RuntimePaths paths,FakePlatform platform,string mode,out List<string> log,bool conservative=false)
        {
            var lines=new List<string>(); log=lines;
            string dir=Path.Combine(Path.GetTempPath(),"CMP90HX-native-tests",Guid.NewGuid().ToString("N"));
            return new NativeWorkflow(paths,platform,s=>lines.Add(s),(core,dma)=>{},conservative).Execute(mode,dir);
        }
        static int Main(string[] args)
        {
            if(args.Length==2 && args[0]=="--probe-sync") {
                try {using(var mutex=SharedSynchronization.OpenMutex(SharedSynchronization.HardwareName)) File.WriteAllText(args[1],"SHARED_MUTEX_ACCESS_PASSED");return 0;}
                catch(Exception error) {File.WriteAllText(args[1],error.ToString());return 1;}
            }
            if(args.Length==1 && args[0]=="--probe-power") {
                try {using(var api=new NvidiaPowerApi()) Console.WriteLine(new JavaScriptSerializer().Serialize(api.Read()));Console.WriteLine("INSPECTOR_CONFLICT="+InspectorConflict.Running());return 0;}
                catch(Exception error) {Console.Error.WriteLine(error);return 1;}
            }
            if(args.Length>0 && args[0]=="--echo") {
                Console.Write(new JavaScriptSerializer().Serialize(args.Skip(1).ToArray())); return 0;
            }
            try {
                TestSynchronizationAndCleanup();
                TestPower();
                var paths=new RuntimePaths(AppDomain.CurrentDomain.BaseDirectory); List<string> log;
                var good=new FakePlatform(); int code=Run(paths,good,"Unlock",out log);
                Require(code==0 && good.Disables==1 && good.Enables==1 && good.SnapshotCalls==2 && log.Count(s=>s.StartsWith("VERIFY_SAMPLE="))==1 && log.Any(s=>s.StartsWith("FULL_UNLOCK_VERIFIED_AFTER")),"enabled unlock restores then verifies exactly one snapshot");
                Require(!good.ConservativeArgument,"unlock uses readiness-gated fast timing by default");
                var compatible=new FakePlatform();code=Run(paths,compatible,"Unlock",out log,true);
                Require(code==0 && compatible.ConservativeArgument,"compatibility option selects conservative timing");
                var disabled=new FakePlatform {Code=22}; code=Run(paths,disabled,"Unlock",out log);
                Require(code==0 && disabled.Disables==0 && disabled.Enables==0 && log.Contains("FULL_UNLOCK_VERIFIED_WHILE_DISABLED_ONLY"),"already-disabled target remains disabled");
                foreach(var failed in new[]{new FakePlatform{Exit=6},new FakePlatform{Exit=1,PostFailureArena=6},new FakePlatform{Crash=true,PostFailureArena=6}}) {
                    code=Run(paths,failed,"Unlock",out log);
                    Require(code!=0 && failed.Disables==1 && failed.Enables==0 && log.Any(s=>s.StartsWith("DMA_RETAINED")),"pending/crashed DMA cannot re-enable NVIDIA");
                }
                var cleanFailure=new FakePlatform{Exit=1}; code=Run(paths,cleanFailure,"Unlock",out log);
                Require(code!=0 && cleanFailure.Enables==1,"failed unlock restores when DMA ownership is clean");
                var disableFailure=new FakePlatform{DisableFails=true};code=Run(paths,disableFailure,"Unlock",out log);
                Require(code!=0 && disableFailure.Enables==1 && !disableFailure.Commands.Contains("full-unlock"),"disable failure refuses firmware and attempts restoration");
                var restoreFailure=new FakePlatform{EnableFails=true};code=Run(paths,restoreFailure,"Unlock",out log);
                Require(code!=0 && restoreFailure.Enables==1 && restoreFailure.SnapshotCalls==1,"restore failure is not reported as success or retried");
                var lost=new FakePlatform{BadVerify=true};code=Run(paths,lost,"Unlock",out log);
                Require(code!=0 && !log.Any(s=>s.StartsWith("FULL_UNLOCK_VERIFIED_AFTER")),"lost overrides after NVIDIA reattach fail verification");
                var status=new FakePlatform();code=Run(paths,status,"Status",out log);
                Require(code==0 && status.DriverCalls==1 && status.Disables==0 && status.Enables==0 && status.Commands.SequenceEqual(new[]{"snapshot"}) && log.Contains("UNLOCK_STATUS_VERIFIED"),"combined status ensures unified driver and reads once without PnP change");
                var partiallyLocked=new FakePlatform{BadStatus=true};code=Run(paths,partiallyLocked,"Status",out log);
                Require(code==0 && partiallyLocked.SnapshotCalls==1 && log.Contains("UNLOCK_STATUS_NOT_FULLY_VERIFIED") && !log.Contains("UNLOCK_STATUS_VERIFIED"),"status displays a locked value without claiming verification passed");
                var manual=new FakePlatform();code=Run(paths,manual,"Verify",out log);
                Require(code==0 && manual.SnapshotCalls==1 && manual.DriverCalls==1,"verification uses a single snapshot without redundant baseline read");
                var check=new FakePlatform();code=Run(paths,check,"Check",out log);
                Require(code==0 && check.DriverCalls==0 && check.TargetCalls==0 && check.Commands.SequenceEqual(new[]{"self-test","core-test"}),"package check performs mock tests only");
                var environment=new FakePlatform();code=Run(paths,environment,"Environment",out log);
                Require(code==0 && environment.DriverCalls==1 && environment.Disables==0 && environment.Enables==0 && environment.Commands.SequenceEqual(new[]{"self-test","core-test","arena-info","dma-test","bridge-probe"}) && log.Contains("ENVIRONMENT_CHECK_PASSED"),"environment combines core tests dependency checks and read-only hardware preflight");
                var missing=new FakePlatform {MissingDependencies=true};code=Run(paths,missing,"Environment",out log);
                Require(code!=0 && missing.Commands.Count==0 && missing.DriverCalls==0,"missing certificate or driver refuses hardware preflight");
                var automatic=new FakePlatform();code=Run(paths,automatic,"AutoUnlock",out log);
                Require(code==0 && automatic.Disables==0 && automatic.Enables==0 && automatic.SnapshotCalls==1 && !automatic.Commands.Contains("full-unlock") && log.Any(s=>s.StartsWith("AUTO_UNLOCK_ALREADY_VERIFIED")),"automatic invocation skips an already unlocked GPU after one snapshot");
                var locked=new FakePlatform {BaselineLocked=true};code=Run(paths,locked,"AutoUnlock",out log);
                Require(code==0 && locked.Disables==1 && locked.Enables==1 && locked.SnapshotCalls==2 && locked.Commands.Count(s=>s=="full-unlock")==1,"automatic locked GPU executes once then verifies once");
                var autoDisabled=new FakePlatform {Code=22};code=Run(paths,autoDisabled,"AutoUnlock",out log);
                Require(code!=0 && autoDisabled.Disables==0 && autoDisabled.Enables==0 && !autoDisabled.Commands.Contains("full-unlock"),"automatic task does not override a disabled GPU");
                var order=new List<string>();bool certificates=false,driver=false;
                EnvironmentSetup.Prepare(()=>order.Add("validate"),()=>certificates,()=>{order.Add("certificates");certificates=true;},()=>driver,
                    ()=>{order.Add("driver");driver=true;},()=>{order.Add("environment");return 0;},()=>order.Add("register"),s=>{});
                Require(order.SequenceEqual(new[]{"validate","certificates","driver","environment","register"}),"task registration occurs only after repairing certificates driver and passing environment check");
                bool registered=false,setupFailed=false;
                try {EnvironmentSetup.Prepare(()=>{},()=>true,()=>{},()=>true,()=>{},()=>1,()=>registered=true,s=>{});} catch(IOException) {setupFailed=true;}
                Require(setupFailed && !registered,"failed environment check cannot register auto-unlock task");
                setupFailed=false;
                try {EnvironmentSetup.Prepare(()=>{},()=>false,()=>{},()=>true,()=>{},()=>0,()=>registered=true,s=>{});} catch(IOException) {setupFailed=true;}
                Require(setupFailed && !registered,"certificate repair must actually succeed before task registration");
                string xml=TaskManagement.Xml(@"C:\ProgramData\CMP90HX\A & B\CMP90HXControl.exe");
                var doc=new XmlDocument();doc.LoadXml(xml);var ns=new XmlNamespaceManager(doc.NameTable);ns.AddNamespace("t",doc.DocumentElement.NamespaceURI);
                Require(doc.SelectNodes("//t:BootTrigger",ns).Count==1 && doc.SelectNodes("//t:EventTrigger",ns).Count==1 && doc.SelectNodes("//t:Delay",ns).Count==0 && doc.SelectSingleNode("//t:Subscription",ns).InnerText.Contains("EventID=107") && !doc.SelectSingleNode("//t:Subscription",ns).InnerText.Contains("EventID=42") && doc.SelectSingleNode("//t:UserId",ns).InnerText=="S-1-5-18" && doc.SelectSingleNode("//t:Arguments",ns).InnerText=="--auto-unlock" && doc.SelectSingleNode("//t:AllowHardTerminate",ns).InnerText=="false" && doc.SelectSingleNode("//t:ExecutionTimeLimit",ns).InnerText=="PT0S","task definition uses boot wake SYSTEM without fixed delays or forced DMA termination");
                doc.LoadXml(TaskManagement.PowerXml(paths.Worker));
                Require(doc.SelectNodes("//t:BootTrigger",ns).Count==1 && doc.SelectNodes("//t:EventTrigger",ns).Count==0 && doc.SelectNodes("//t:Delay",ns).Count==0 && doc.SelectSingleNode("//t:UserId",ns).InnerText=="S-1-5-18" && doc.SelectSingleNode("//t:Arguments",ns).InnerText=="--power-monitor" && doc.SelectSingleNode("//t:AllowHardTerminate",ns).InnerText=="false" && doc.SelectSingleNode("//t:ExecutionTimeLimit",ns).InnerText=="PT0S","power task stays resident as SYSTEM with no forced termination");
                TaskManagement.ValidateDefinition(paths.Worker);
                TaskManagement.ValidateDefinition(paths.Worker,true);
                Require(true,"Windows Task Scheduler accepts the generated definition without registering a task");
                Console.WriteLine("READ_ONLY_TASK "+TaskManagement.Query().Text);
                CertificateManager.ValidateFiles(paths);
                Require(true,"bundled certificate hashes and thumbprints match pinned inputs");
                string logRoot=Path.Combine(Path.GetTempPath(),"CMP90HX-log-tests",Guid.NewGuid().ToString("N"));
                var firstSession=new LogSession(logRoot);File.WriteAllText(Path.Combine(firstSession.DirectoryPath,"first.log"),"active");
                Directory.SetCreationTimeUtc(firstSession.DirectoryPath,DateTime.UtcNow.AddMinutes(-2));
                var nextSession=new LogSession(logRoot);
                Require(File.Exists(Path.Combine(firstSession.DirectoryPath,"first.log")),"startup cleanup preserves logs owned by a live session");
                firstSession.Dispose();nextSession.Dispose();
                // Windows clock granularity may give adjacent sessions the same timestamp.
                Directory.SetCreationTimeUtc(nextSession.DirectoryPath,DateTime.UtcNow.AddMinutes(-2));
                using(var lastSession=new LogSession(logRoot)) {
                    Require(!Directory.Exists(firstSession.DirectoryPath) && !Directory.Exists(nextSession.DirectoryPath),"next startup removes previous inactive logs");
                }
                var snapshot=UnlockSnapshot.Parse(FakePlatform.Json(false));
                NativeWorkflow.ValidateVerified(snapshot,16,16);
                bool rejected=false;try {NativeWorkflow.ValidateVerified(snapshot,32,16);}catch(IOException){rejected=true;}
                Require(rejected,"link width loss is rejected");
                string[] tricky={"space value","quote\"value",@"C:\ends with slash\",""};
                using(var proc=new Process {StartInfo=new ProcessStartInfo {FileName=Process.GetCurrentProcess().MainModule.FileName,Arguments="--echo "+String.Join(" ",tricky.Select(WindowsWorkflowPlatform.Quote)),UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true}}) {
                    proc.Start();string output=proc.StandardOutput.ReadToEnd();proc.WaitForExit();
                    Require(new JavaScriptSerializer().Deserialize<string[]>(output).SequenceEqual(tricky),"worker arguments preserve spaces quotes and trailing backslashes");
                }
                if(args.Length==1) {
                    SignatureTrust.Verify(args[0]); Console.WriteLine("SIGNED_DMA_AUTHENTICODE_PASSED");
                    foreach(var device in NativePnp.Enumerate()) Console.WriteLine("READ_ONLY_PNP "+device.Bdf+" Code="+device.Problem+" "+device.Name);
                    Console.WriteLine("READ_ONLY_SERVICE "+DriverService.CurrentState());
                }
                Console.WriteLine("NATIVE_WORKFLOW_TESTS_PASSED count="+count); return 0;
            } catch(Exception error) { Console.Error.WriteLine(error);return 1; }
        }
        static void TestSynchronizationAndCleanup()
        {
            string name=@"Local\CMP90HX-sync-test-"+Guid.NewGuid().ToString("N");
            var security=new MutexSecurity();
            using(var user=WindowsIdentity.GetCurrent()) security.AddAccessRule(new MutexAccessRule(user.User,MutexRights.Synchronize|MutexRights.Modify,AccessControlType.Allow));
            bool created;
            using(var original=new Mutex(false,name,out created,security))
            using(var opened=SharedSynchronization.OpenMutex(name)) {
                bool acquired=opened.WaitOne(0);if(acquired) opened.ReleaseMutex();
                Require(acquired,"existing mutex opens with wait/release rights without demanding FullControl");
                original.WaitOne();bool otherAcquired=true;
                var thread=new Thread(()=>{otherAcquired=opened.WaitOne(0);if(otherAcquired) opened.ReleaseMutex();});thread.Start();thread.Join();original.ReleaseMutex();
                Require(!otherAcquired,"restricted mutex still serializes independent threads");
            }
            using(var fresh=SharedSynchronization.OpenMutex(name)) {
                var rules=fresh.GetAccessControl().GetAccessRules(true,false,typeof(SecurityIdentifier)).Cast<MutexAccessRule>().ToArray();
                Require(new[]{WellKnownSidType.LocalSystemSid,WellKnownSidType.BuiltinAdministratorsSid}.All(t=>rules.Any(r=>r.IdentityReference.Equals(new SecurityIdentifier(t,null)) && (r.MutexRights&MutexRights.FullControl)==MutexRights.FullControl)),"new shared mutex explicitly grants SYSTEM and Administrators full access");
            }
            string root=Path.Combine(Path.GetTempPath(),"CMP90HX-cleanup-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            string unused=Path.Combine(root,"1.2.0-aaaaaaaaaaaaaaaa"),referenced=Path.Combine(root,"1.2.1-bbbbbbbbbbbbbbbb"),running=Path.Combine(root,"1.2.1-cccccccccccccccc"),unknown=Path.Combine(root,"user-files");
            foreach(string directory in new[]{unused,referenced,running,unknown}) {Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,"CMP90HXControl.exe"),"fixture");}
            using(var lease=new FileStream(Path.Combine(running,".in-use"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.ReadWrite)) {
                RuntimeDeployment.Clean(root,new[]{Path.Combine(referenced,"CMP90HXControl.exe")},s=>{});
                Require(!Directory.Exists(unused),"task uninstall cleanup removes unreferenced runtime copies");
                Require(Directory.Exists(referenced) && Directory.Exists(running) && Directory.Exists(unknown),"cleanup preserves task references active runtime leases and unrelated directories");
            }
            RuntimeDeployment.Clean(root,new string[0],s=>{});
            Require(!Directory.Exists(referenced) && !Directory.Exists(running) && Directory.Exists(unknown),"deferred cleanup removes released runtimes after references disappear");
        }
        static void TestPower()
        {
            DateTime start=new DateTime(2026,10,3,0,0,0,DateTimeKind.Utc);
            var settings=new PowerSettings();var gpu=new FakePowerGpu();var intents=new List<bool>();
            var engine=new PowerEngine(gpu,b=>intents.Add(b));
            engine.Tick(settings,false,false,false,start);
            Require(gpu.Writes.Count==0,"power is opt-in; default disabled policy never writes P-state");
            settings.Enabled=true;
            engine.Tick(settings,false,false,false,start);
            engine.Tick(settings,false,false,false,start.AddSeconds(9));
            Require(gpu.Writes.Count==0,"brief idle interval cannot apply P8");
            engine.Tick(settings,false,false,false,start.AddSeconds(10));
            engine.Tick(settings,false,false,false,start.AddSeconds(11));
            Require(gpu.Writes.SequenceEqual(new[]{8}) && intents.SequenceEqual(new[]{true}),"continuous idle applies one P8 limit and records ownership");
            gpu.Sample.Gpu=settings.Threshold;
            engine.Tick(settings,false,false,false,start.AddSeconds(12));
            Require(!engine.Limited && gpu.Writes.SequenceEqual(new[]{8,0}),"threshold reached releases P8 immediately");
            gpu.Sample.Gpu=0;engine.Tick(settings,false,false,false,start.AddSeconds(13));
            engine.Tick(settings,false,false,false,start.AddSeconds(22));
            Require(!engine.Limited,"load recovery restarts the entire idle interval");
            engine.Tick(settings,false,false,false,start.AddSeconds(23));gpu.Sample.Video=1;
            engine.Tick(settings,false,false,false,start.AddSeconds(24));
            Require(!engine.Limited && gpu.Writes.Last()==0,"any video activity releases the limit");
            gpu.Sample.Video=0;engine.Tick(settings,false,false,false,start.AddSeconds(25));engine.Tick(settings,false,false,false,start.AddSeconds(35));
            engine.Tick(settings,true,false,false,start.AddSeconds(36));
            Require(!engine.Limited && gpu.Writes.Last()==0,"full-speed application releases the limit");
            engine.Tick(settings,false,false,false,start.AddSeconds(37));engine.Tick(settings,false,false,false,start.AddSeconds(47));
            var paused=engine.Tick(settings,false,true,false,start.AddSeconds(48));
            engine.Tick(settings,false,false,false,start.AddSeconds(49));engine.Tick(settings,false,false,false,start.AddSeconds(58));
            Require(!paused.LimitApplied && !engine.Limited,"unlock pause releases the limit and resumes with a fresh idle timer");
            engine.Tick(settings,false,false,false,start.AddSeconds(59));
            engine.Tick(settings,false,false,true,start.AddSeconds(60));
            Require(!engine.Limited,"resume notification releases old limits before restarting idle detection");
            engine.Tick(settings,false,false,false,start.AddSeconds(70));settings.Enabled=false;
            engine.Tick(settings,false,false,false,start.AddSeconds(71));
            Require(!engine.Limited && gpu.Writes.Last()==0 && intents.Last()==false,"disabling restores automatic policy and clears ownership");
            settings.Enabled=true;var failedGpu=new FakePowerGpu {FailP8=true};bool intent=false;
            var failed=new PowerEngine(failedGpu,b=>intent=b);failed.Tick(settings,false,false,false,start);
            bool rejected=false;try {failed.Tick(settings,false,false,false,start.AddSeconds(10));} catch(IOException) {rejected=true;}
            Require(rejected && !failed.Limited && !intent && failedGpu.Writes.SequenceEqual(new[]{8,0}),"failed P8 write attempts restoration and cannot report success");
            failedGpu.FailRelease=true;failed.Tick(settings,false,false,false,start.AddSeconds(11));
            rejected=false;try {failed.Tick(settings,false,false,false,start.AddSeconds(21));} catch(IOException) {rejected=true;}
            Require(rejected && failed.Limited && intent,"failed restoration retains ownership for recovery");
            failedGpu.FailRelease=false;failed.Release();
            Require(!failed.Limited && !intent,"retry can release a retained limit");
            var recoveredGpu=new FakePowerGpu();var recovered=new PowerEngine(recoveredGpu,b=>{},true);
            recovered.Tick(settings,false,false,true,start);
            Require(recoveredGpu.Writes.SequenceEqual(new[]{0}) && !recovered.Limited,"crash recovery restores automatic policy before idle detection");
            var policy=new IdlePowerPolicy();policy.WantsP8(settings,gpu.Sample,false,start);
            Require(!policy.WantsP8(settings,gpu.Sample,false,start.AddSeconds(-20)) && !policy.WantsP8(settings,gpu.Sample,false,start.AddSeconds(-11)),"clock rollback cannot bypass idle waiting");
            settings.FullSpeedApps=Process.GetCurrentProcess().ProcessName+".exe";
            Require(settings.HasFullSpeedApp(),"exception list resolves running process names including exe suffix");
        }
    }
}
