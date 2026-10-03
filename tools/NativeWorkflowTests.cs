using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;

namespace CMP90HX.Control
{
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
            if(args.Length>0 && args[0]=="--echo") {
                Console.Write(new JavaScriptSerializer().Serialize(args.Skip(1).ToArray())); return 0;
            }
            try {
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
                Require(code==0 && status.DriverCalls==0 && status.Disables==0 && status.Enables==0 && status.Commands.SequenceEqual(new[]{"snapshot"}) && log.Contains("UNLOCK_STATUS_VERIFIED"),"combined status reads and verifies once without DMA start or PnP change");
                var partiallyLocked=new FakePlatform{BadStatus=true};code=Run(paths,partiallyLocked,"Status",out log);
                Require(code==0 && partiallyLocked.SnapshotCalls==1 && log.Contains("UNLOCK_STATUS_NOT_FULLY_VERIFIED") && !log.Contains("UNLOCK_STATUS_VERIFIED"),"status displays a locked value without claiming verification passed");
                var manual=new FakePlatform();code=Run(paths,manual,"Verify",out log);
                Require(code==0 && manual.SnapshotCalls==1 && manual.DriverCalls==0,"verification uses a single snapshot without redundant baseline read");
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
                Require(doc.SelectNodes("//t:BootTrigger",ns).Count==1 && doc.SelectNodes("//t:EventTrigger",ns).Count==1 && doc.SelectSingleNode("//t:Subscription",ns).InnerText.Contains("EventID=107") && !doc.SelectSingleNode("//t:Subscription",ns).InnerText.Contains("EventID=42") && doc.SelectSingleNode("//t:UserId",ns).InnerText=="S-1-5-18" && doc.SelectSingleNode("//t:Arguments",ns).InnerText=="--auto-unlock" && doc.SelectSingleNode("//t:AllowHardTerminate",ns).InnerText=="false" && doc.SelectSingleNode("//t:ExecutionTimeLimit",ns).InnerText=="PT0S","task definition uses boot wake SYSTEM and never forcibly terminates DMA work");
                TaskManagement.ValidateDefinition(paths.Worker);
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
    }
}
