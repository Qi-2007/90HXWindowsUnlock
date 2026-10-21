using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CMP90HX.Control {
    public sealed class ControlOptions {
        public string Backend {get;set;}
        public bool UnlockOnStartup {get;set;}
        public bool StartOnBoot {get;set;}
        public bool UnlockOnResume {get;set;}
        public bool ReadUnlockOnConnect {get;set;}
        public bool ConservativeTiming {get;set;}
        public ControlOptions() {Backend="Native";UnlockOnStartup=true;UnlockOnResume=true;ReadUnlockOnConnect=false;}
        public void Validate() {if(Backend!="Native" && Backend!="Session") throw new IOException("方案必须为 Native 或 Session。");}
    }
    public sealed class IpcRequest {
        public string Id {get;set;}
        public string Command {get;set;}
        public string Data {get;set;}
        public bool Conservative {get;set;}
        public DateTime SubmittedUtc {get;set;}
    }
    public sealed class IpcResult {
        public string Id {get;set;}
        public int Code {get;set;}
        public string Error {get;set;}
        public string Snapshot {get;set;}
        public string Directory {get;set;}
        public string Log {get;set;}
    }
    public sealed class ControlState {
        public int Protocol {get;set;}
        public int BackendRevision {get;set;}
        public bool RootCertificateSupport {get;set;}
        public int Pid {get;set;}
        public string Instance {get;set;}
        public DateTime Heartbeat {get;set;}
        public bool Busy {get;set;}
        public string JobId {get;set;}
        public string Operation {get;set;}
        public string Acknowledged {get;set;}
        public string Log {get;set;}
        public long LogRevision {get;set;}
        public string GpuName {get;set;}
        public int DeviceCount {get;set;}
        public int? ProblemCode {get;set;}
        public string DriverState {get;set;}
        public string DeviceDriver {get;set;}
        public string UnlockState {get;set;}
        public string Snapshot {get;set;}
        public DateTime? SnapshotUtc {get;set;}
        public string Error {get;set;}
        public bool[] Certificates {get;set;}
        public string ReaderDriverState {get;set;}
        public string NativeDriverState {get;set;} public string SessionDriverState {get;set;} public string WakeNotificationError {get;set;}
        public bool WakeNotifications {get;set;}
        public bool AutoUnlockDisabled {get;set;}
        public ControlOptions Options {get;set;}
        public PowerSettings Power {get;set;}
        public PowerStatus PowerStatus {get;set;}
        public List<IpcResult> Results {get;set;}
        public ControlState() {Protocol=2;RootCertificateSupport=ControlMemory.RootCertificateSupport;Log="";DriverState="待读取";UnlockState="未读取";Results=new List<IpcResult>();Certificates=new bool[0];Options=new ControlOptions();Power=new PowerSettings();}
    }
    // All named objects are machine-wide and restricted to administrators and SYSTEM.
    // Separate request/state locks let telemetry continue while hardware work is running.
    internal sealed class ControlMemory : IDisposable {
        internal const int BackendRevision=10;
#if ENABLE_ROOT_CERTIFICATES
        internal const bool RootCertificateSupport=true;
#else
        internal const bool RootCertificateSupport=false;
#endif
        internal static string DiagnosticPrefix {get{return "Local\\CMP90HX.Control.Diagnostics."+WindowsIdentity.GetCurrent().User.Value+".";}}
        internal static Mutex OpenLock(string name,bool diagnostic) {
            if(!diagnostic)return SharedSynchronization.OpenMutex(name);
            var acl=new MutexSecurity();acl.AddAccessRule(new MutexAccessRule(WindowsIdentity.GetCurrent().User,MutexRights.FullControl,AccessControlType.Allow));
            bool created;return new Mutex(false,name,out created,acl);
        }
        const int RequestBytes=65536, Capacity=1048576;
        readonly MemoryMappedFile mapping;
        readonly MemoryMappedViewAccessor view;
        readonly Mutex packetLock,requestLock;
        internal static JavaScriptSerializer Json() {return new JavaScriptSerializer {MaxJsonLength=900000,RecursionLimit=32};}
        internal ControlMemory(bool server,bool diagnostic=false) {
            // Production servers ALWAYS use Global + administrators/SYSTEM ACL.
            // Only diagnostic servers use a per-user Local namespace and reject every hardware command.
            if(!server)diagnostic=Environment.GetEnvironmentVariable("CMP90HX_IPC_DIAGNOSTIC")=="1";
            string prefix=diagnostic?DiagnosticPrefix:"Global\\CMP90HX.Control.";
            string name=prefix+"Memory.v1";
            packetLock=OpenLock(prefix+"Packet.v1",diagnostic);
            requestLock=OpenLock(prefix+"Request.v1",diagnostic);
            try {
                if(server) {
                    var acl=new MemoryMappedFileSecurity();
                    foreach(var sid in new[]{WellKnownSidType.LocalSystemSid,WellKnownSidType.BuiltinAdministratorsSid})
                        acl.AddAccessRule(new AccessRule<MemoryMappedFileRights>(new SecurityIdentifier(sid,null),MemoryMappedFileRights.FullControl,AccessControlType.Allow));
                    if(diagnostic)acl.AddAccessRule(new AccessRule<MemoryMappedFileRights>(WindowsIdentity.GetCurrent().User,MemoryMappedFileRights.FullControl,AccessControlType.Allow));
                    mapping=MemoryMappedFile.CreateOrOpen(name,Capacity,MemoryMappedFileAccess.ReadWrite,MemoryMappedFileOptions.None,acl,HandleInheritability.None);
                } else mapping=MemoryMappedFile.OpenExisting(name,MemoryMappedFileRights.ReadWrite);
                view=mapping.CreateViewAccessor(0,Capacity,MemoryMappedFileAccess.ReadWrite);
            } catch {Dispose();throw;}
        }
        static bool Acquire(Mutex mutex,int milliseconds) {try {return mutex.WaitOne(milliseconds);} catch(AbandonedMutexException) {return true;}}
        T Read<T>(long offset,int capacity) {
            if(!Acquire(packetLock,5000)) throw new IOException("共享内存读取超时。");
            try {
                int bytes=view.ReadInt32(offset);
                if(bytes==0) return default(T);
                if(bytes<0 || bytes>capacity-4) throw new IOException("共享内存数据长度无效。");
                var buffer=new byte[bytes];view.ReadArray(offset+4,buffer,0,bytes);
                return Json().Deserialize<T>(Encoding.UTF8.GetString(buffer));
            } finally {packetLock.ReleaseMutex();}
        }
        void Write(long offset,int capacity,object value) {
            var bytes=Encoding.UTF8.GetBytes(Json().Serialize(value));
            if(bytes.Length>capacity-4) throw new IOException("共享内存数据超过协议限制。");
            if(!Acquire(packetLock,5000)) throw new IOException("共享内存写入超时。");
            try {view.Write(offset,0);view.WriteArray(offset+4,bytes,0,bytes.Length);view.Write(offset,bytes.Length);}
            finally {packetLock.ReleaseMutex();}
        }
        internal ControlState State() {var state=Read<ControlState>(RequestBytes,Capacity-RequestBytes);if(state!=null && state.Protocol!=2) throw new IOException("后台通信版本不匹配，请退出旧后台后重新启动。");return state;}
        internal void Publish(ControlState state) {Write(RequestBytes,Capacity-RequestBytes,state);}
        internal IpcRequest Request() {return Read<IpcRequest>(0,RequestBytes);}
        internal void ResetRequest() {if(!Acquire(packetLock,5000)) throw new IOException("通信锁超时。");try {view.Write(0,0);} finally {packetLock.ReleaseMutex();}}
        internal void Submit(IpcRequest request) {
            if(!Acquire(requestLock,10000)) throw new IOException("后台请求入口忙，请重试。");
            try {
                var state=State();
                if(state==null || (DateTime.UtcNow-state.Heartbeat).TotalSeconds>5) throw new IOException("后台未响应。");
                string instance=state.Instance;
                Write(0,RequestBytes,request);
                var timeout=System.Diagnostics.Stopwatch.StartNew();
                while(timeout.ElapsedMilliseconds<10000) {
                    state=State();
                    if(state!=null && state.Instance==instance && state.Acknowledged==request.Id) return;
                    if(state==null || state.Instance!=instance) throw new IOException("后台已重启，请检查执行状态后重试。");
                    Thread.Sleep(25);
                }
                throw new IOException("后台未确认请求；请检查状态，勿重复提交硬件操作。");
            } finally {requestLock.ReleaseMutex();}
        }
        public void Dispose() {if(view!=null)view.Dispose();if(mapping!=null)mapping.Dispose();if(packetLock!=null)packetLock.Dispose();if(requestLock!=null)requestLock.Dispose();}
    }
}
