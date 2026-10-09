using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace CMP90HX.Control
{
    internal static class DriverService
    {
        const string Name="CMP90HXDma";
        [StructLayout(LayoutKind.Sequential)] struct Status { internal uint Type,State,Controls,Win32Exit,ServiceExit,Checkpoint,WaitHint; }
        [StructLayout(LayoutKind.Sequential)] struct Config { internal uint Type,Start,Error; internal IntPtr Path,Group; internal uint Tag; internal IntPtr Dependencies,User,Display; }
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr OpenSCManager(string machine,string database,uint access);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr OpenService(IntPtr manager,string name,uint access);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateService(IntPtr manager,string name,string display,uint access,uint type,uint start,uint error,string path,string group,IntPtr tag,string dependencies,string user,string password);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool ChangeServiceConfig(IntPtr service,uint type,uint start,uint error,string path,string group,IntPtr tag,string dependencies,string user,string password,string display);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool StartService(IntPtr service,uint count,IntPtr args);
        [DllImport("advapi32.dll",SetLastError=true)] static extern bool QueryServiceStatus(IntPtr service,out Status status);
        [DllImport("advapi32.dll",SetLastError=true)] static extern bool DeleteService(IntPtr service);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryServiceConfig(IntPtr service,IntPtr buffer,uint size,out uint needed);
        [DllImport("advapi32.dll")] static extern bool CloseServiceHandle(IntPtr handle);
        static Win32Exception Error(string action) { return new Win32Exception(Marshal.GetLastWin32Error(),action+": "+new Win32Exception(Marshal.GetLastWin32Error()).Message); }
        static Status Read(IntPtr service) { Status value; if(!QueryServiceStatus(service,out value)) throw Error("QueryServiceStatus"); return value; }
        static Config ReadConfig(IntPtr service,out string path)
        {
            uint size; QueryServiceConfig(service,IntPtr.Zero,0,out size);
            if(size==0 || size>65536) throw Error("QueryServiceConfig size");
            IntPtr buffer=Marshal.AllocHGlobal((int)size);
            try {
                if(!QueryServiceConfig(service,buffer,size,out size)) throw Error("QueryServiceConfig");
                Config result=(Config)Marshal.PtrToStructure(buffer,typeof(Config));
                path=Marshal.PtrToStringUni(result.Path); return result;
            } finally { Marshal.FreeHGlobal(buffer); }
        }
        internal static string CurrentState()
        {
            IntPtr manager=OpenSCManager(null,null,1); if(manager==IntPtr.Zero) throw Error("OpenSCManager");
            try {
                IntPtr service=OpenService(manager,Name,4);
                if(service==IntPtr.Zero) {
                    if(Marshal.GetLastWin32Error()==1060) return "未安装";
                    if(Marshal.GetLastWin32Error()==1072) return "已卸载 · 重启后生效";
                    throw Error("OpenService");
                }
                try { return Read(service).State==4 ? "运行中" : "已安装 · 操作时启动"; }
                finally { CloseServiceHandle(service); }
            } finally { CloseServiceHandle(manager); }
        }
        internal static bool Installed()
        {
            string state=CurrentState();
            return state=="运行中" || state=="已安装 · 操作时启动";
        }
        internal static void RequireInstalled()
        {
            if(!Installed()) throw new IOException("DMA_DRIVER_NOT_INSTALLED: 请在驱动管理中安装驱动。");
        }
        internal static void Uninstall(Action<string> log)
        {
            IntPtr manager=OpenSCManager(null,null,1); if(manager==IntPtr.Zero) throw Error("OpenSCManager");
            try {
                IntPtr service=OpenService(manager,Name,0x10007);
                if(service==IntPtr.Zero) {
                    int error=Marshal.GetLastWin32Error();
                    if(error==1060 || error==1072) { log("DMA_DRIVER_UNINSTALL: 服务已移除，已加载的驱动仍保留到重启。"); return; }
                    throw Error("OpenService");
                }
                try {
                    string path; if(ReadConfig(service,out path).Type!=1) throw new IOException("拒绝删除同名的非内核服务。");
                    bool active=Read(service).State!=1;
                    if(!ChangeServiceConfig(service,uint.MaxValue,4,uint.MaxValue,null,null,IntPtr.Zero,null,null,null,null)) throw Error("Disable CMP90HXDma");
                    if(!DeleteService(service)) throw Error("DeleteService CMP90HXDma");
                    log(active?"DMA_DRIVER_UNINSTALL: 服务注册已移除；当前驱动不支持卸载，重启后生效。":"DMA_DRIVER_UNINSTALL: 驱动服务已卸载。");
                } finally { CloseServiceHandle(service); }
            } finally { CloseServiceHandle(manager); }
        }
        static string NormalizeDriverPath(string path)
        {
            path=path.Trim().Trim('"');
            if(path.StartsWith(@"\??\",StringComparison.Ordinal)) path=path.Substring(4);
            if(path.StartsWith(@"\SystemRoot\",StringComparison.OrdinalIgnoreCase)) path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),path.Substring(12));
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        }
        internal static void ProtectDirectory(string path)
        {
            if(Directory.Exists(path) && (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
                throw new IOException("Driver storage must not be a junction or symbolic link: "+path);
            Directory.CreateDirectory(path);
            DirectorySecurity acl=new DirectorySecurity();
            acl.SetAccessRuleProtection(true,false);
            foreach(var sid in new[]{WellKnownSidType.LocalSystemSid,WellKnownSidType.BuiltinAdministratorsSid})
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid,null),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null),FileSystemRights.ReadAndExecute,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(acl);
        }
        internal static bool SameDriverImage(string path,string expectedHash)
        {
            return File.Exists(path) && String.Equals(RuntimePaths.Hash(path),expectedHash,StringComparison.OrdinalIgnoreCase);
        }
        internal static string StoreRoot {get {return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"CMP90HX","DriverStore");}}
        internal static string StoredDriver {get {return Path.Combine(StoreRoot,RuntimePaths.DmaHash,"CMP90HXDma.sys");}}
        internal static void CleanStore(string root,string keep,string active,Action<string> log)
        {
            root=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if(!Directory.Exists(root)) return;
            if((File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0) throw new IOException("拒绝清理链接目录。");
            foreach(string directory in Directory.GetDirectories(root)) {
                string path=Path.GetFullPath(directory),prefix=path+Path.DirectorySeparatorChar;
                if(!String.Equals(Path.GetDirectoryName(path),root,StringComparison.OrdinalIgnoreCase) ||
                    !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path),@"^[0-9a-fA-F]{64}$") ||
                    (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0 ||
                    Path.GetFullPath(keep).StartsWith(prefix,StringComparison.OrdinalIgnoreCase) ||
                    !String.IsNullOrEmpty(active) && Path.GetFullPath(active).StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) continue;
                // Only remove recognized, flat driver-version directories. Never follow links.
                var files=Directory.GetFiles(path);
                if(Directory.GetDirectories(path).Length!=0 || Array.Exists(files,file=>
                    (File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0 ||
                    !(String.Equals(Path.GetFileName(file),"CMP90HXDma.sys",StringComparison.OrdinalIgnoreCase) ||
                      String.Equals(Path.GetFileName(file),"CMP90HXDmaSigned.sys",StringComparison.OrdinalIgnoreCase)))) continue;
                try {foreach(string file in files) File.Delete(file);Directory.Delete(path);log("DMA_DRIVER_VERSION_REMOVED "+path);}
                catch(IOException) {log("DMA_DRIVER_VERSION_RETAINED: 文件仍被使用，重启后再清理 "+path);}
                catch(UnauthorizedAccessException) {log("DMA_DRIVER_VERSION_RETAINED: 当前无法清理 "+path);}
            }
        }
        internal static void EnsureRunning(string source,Action<string> log,bool installIfMissing=true)
        {
            using(var deployment=new DeploymentLock()) EnsureRunningLocked(source,log,installIfMissing);
        }
        static void EnsureRunningLocked(string source,Action<string> log,bool installIfMissing)
        {
            RuntimePaths.RequireHash(source,RuntimePaths.DmaHash);
            SignatureTrust.Verify(source);
            // Inspect the loaded image before writing another version into DriverStore.
            IntPtr probeManager=OpenSCManager(null,null,1);if(probeManager==IntPtr.Zero) throw Error("OpenSCManager");
            string active=null;
            try {
                IntPtr probe=OpenService(probeManager,Name,5);
                if(probe==IntPtr.Zero) {
                    if(Marshal.GetLastWin32Error()!=1060) throw Error("OpenService");
                    if(!installIfMissing) throw new IOException("DMA_DRIVER_NOT_INSTALLED: 请先安装驱动。");
                } else try {
                    string configured;if(ReadConfig(probe,out configured).Type!=1) throw new IOException("同名服务不是内核驱动。");
                    if(Read(probe).State!=1) {
                        active=NormalizeDriverPath(configured);
                        if(!SameDriverImage(active,RuntimePaths.DmaHash))
                            throw new IOException("DMA_DRIVER_UPDATE_REQUIRES_REBOOT: 当前加载的驱动与发布包不同，请重启后更新。");
                    }
                } finally {CloseServiceHandle(probe);}
            } finally {CloseServiceHandle(probeManager);}
            string appRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"CMP90HX");
            ProtectDirectory(appRoot);
            string storeRoot=Path.Combine(appRoot,"DriverStore");
            ProtectDirectory(storeRoot);
            string store=Path.Combine(storeRoot,RuntimePaths.DmaHash); ProtectDirectory(store);
            string driver=Path.Combine(store,"CMP90HXDma.sys");
            if(File.Exists(driver) && (File.GetAttributes(driver)&FileAttributes.ReparsePoint)!=0) throw new IOException("Driver file must not be a symbolic link.");
            if(!File.Exists(driver)) File.Copy(source,driver,false);
            RuntimePaths.RequireHash(driver,RuntimePaths.DmaHash);
            SignatureTrust.Verify(driver);
            CleanStore(storeRoot,driver,active,log);
            IntPtr manager=OpenSCManager(null,null,3); if(manager==IntPtr.Zero) throw Error("OpenSCManager");
            try {
                IntPtr service=OpenService(manager,Name,0x17);
                if(service==IntPtr.Zero) {
                    if(Marshal.GetLastWin32Error()!=1060) throw Error("OpenService");
                    if(!installIfMissing) throw new IOException("DMA_DRIVER_NOT_INSTALLED: 请在驱动管理中安装驱动后再读取解锁状态。");
                    service=CreateService(manager,Name,"CMP 90HX DMA",0x17,1,3,1,@"\??\"+driver,null,IntPtr.Zero,null,null,null);
                    if(service==IntPtr.Zero) throw Error("CreateService CMP90HXDma");
                    log("DMA_DRIVER_INSTALLED path="+driver);
                }
                try {
                    string configured; Config config=ReadConfig(service,out configured);
                    if(config.Type!=1) throw new IOException("CMP90HXDma service is not a kernel driver; refusing to replace it.");
                    string installed=NormalizeDriverPath(configured);
                    Status state=Read(service);
                    if(!String.Equals(installed,driver,StringComparison.OrdinalIgnoreCase)) {
                        if(state.State!=1) {
                            if(!SameDriverImage(installed,RuntimePaths.DmaHash)) {
                                log("DMA_DRIVER_IMAGE_MISMATCH installed="+installed+" expectedSha256="+RuntimePaths.DmaHash);
                                throw new IOException("DMA_DRIVER_UPDATE_REQUIRES_REBOOT: the active service image differs from this package. Reboot before switching drivers.");
                            }
                            log("DMA_DRIVER_SAME_IMAGE_REUSED: SHA256 matches; service path retained.");
                        } else {
                            if(!ChangeServiceConfig(service,uint.MaxValue,3,uint.MaxValue,@"\??\"+driver,null,IntPtr.Zero,null,null,null,null)) throw Error("ChangeServiceConfig CMP90HXDma");
                            log("DMA_DRIVER_PATH_UPDATED (previous driver stopped)");
                        }
                    }
                    if(state.State!=4 && !StartService(service,0,IntPtr.Zero)) {
                        int error=Marshal.GetLastWin32Error();
                        if(error!=1056) throw new Win32Exception(error,"CMP90HXDma start failed ("+error+"): "+new Win32Exception(error).Message);
                    }
                    for(int poll=0;poll<100;poll++) {
                        state=Read(service);
                        if(state.State==4) { log("DMA_DRIVER_RUNNING; retained for this Windows boot."); return; }
                        if(state.State==1) throw new Win32Exception((int)state.Win32Exit,"CMP90HXDma stopped during start; Win32Exit="+state.Win32Exit);
                        Thread.Sleep(100);
                    }
                    throw new IOException("CMP90HXDma start timed out. Service retained for inspection.");
                } finally { CloseServiceHandle(service); }
            } finally { CloseServiceHandle(manager); }
        }
    }
    internal static class SignatureTrust
    {
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct FileInfo { internal uint Size; internal IntPtr Path,File,Subject; }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct TrustData {
            internal uint Size; internal IntPtr Policy,Sip; internal uint UI,Revocation,Choice; internal IntPtr File;
            internal uint Action; internal IntPtr State,Url; internal uint Flags,Context; internal IntPtr Settings;
        }
        [DllImport("wintrust.dll",ExactSpelling=true)] static extern int WinVerifyTrust(IntPtr window,ref Guid action,ref TrustData data);
        internal static void Verify(string path)
        {
            IntPtr filename=Marshal.StringToCoTaskMemUni(path), file=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FileInfo)));
            TrustData data=new TrustData(); Guid action=new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            try {
                Marshal.StructureToPtr(new FileInfo {Size=(uint)Marshal.SizeOf(typeof(FileInfo)),Path=filename},file,false);
                data=new TrustData { Size=(uint)Marshal.SizeOf(typeof(TrustData)),UI=2,Choice=1,File=file,Action=1,Flags=0x1000 };
                int status=WinVerifyTrust(new IntPtr(-1),ref action,ref data);
                if(status!=0) throw new IOException("Driver Authenticode verification failed: 0x"+status.ToString("x8"));
            } finally {
                if(data.Size!=0) { data.Action=2; WinVerifyTrust(new IntPtr(-1),ref action,ref data); }
                Marshal.FreeHGlobal(file); Marshal.FreeCoTaskMem(filename);
            }
        }
    }
}
