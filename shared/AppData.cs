using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace CMP90HX.Control {
    internal static class AppData {
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern bool MoveFileEx(string existing,string replacement,uint flags);
        internal static string Root {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"CMP90HX");}}
        internal static string LegacyRoot {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CMP90HX");}}

        internal static void MigrateLegacy() {
            if(Directory.Exists(Root) && IsLink(Root))throw new IOException("数据目录不能是链接："+Root);
            Directory.CreateDirectory(Root);
            var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
            foreach(var sid in new[]{WellKnownSidType.LocalSystemSid,WellKnownSidType.BuiltinAdministratorsSid})
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid,null),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            Directory.SetAccessControl(Root,acl);
            try {MigrateLegacy(LegacyRoot,Root,line=>Trace.WriteLine(line));}
            catch(IOException error){Trace.WriteLine("DATA_MIGRATION_PENDING: "+error.Message);}
            catch(UnauthorizedAccessException error){Trace.WriteLine("DATA_MIGRATION_PENDING: "+error.Message);}
        }
        internal static void MigrateLegacy(string source,string destination,Action<string> log) {
            if(!Directory.Exists(source))return;
            if(IsLink(source) || (Directory.Exists(destination) && IsLink(destination))) {log("DATA_MIGRATION_SKIPPED_LINK "+source);return;}
            Directory.CreateDirectory(destination);
            foreach(string entry in Directory.GetFileSystemEntries(source)) {
                try {
                    if(IsLink(entry)){log("DATA_MIGRATION_SKIPPED_LINK "+entry);continue;}
                    string target=Path.Combine(destination,Path.GetFileName(entry));
                    if(Directory.Exists(entry))MigrateLegacy(entry,target,log);
                    else {
                        if(File.Exists(target)) {
                            if(IsLink(target))throw new IOException("迁移目标不能是链接："+target);
                            // Identical onboarding markers can be merged without repeating the guide.
                            if(SameBytes(entry,target)){File.Delete(entry);continue;}
                            target+=".legacy-"+Guid.NewGuid().ToString("N");
                        }
                        File.Copy(entry,target,false);
                        try {File.Delete(entry);}
                        catch {File.Delete(target);throw;}
                    }
                }catch(IOException error){log("DATA_MIGRATION_PENDING: "+error.Message);}
                catch(UnauthorizedAccessException error){log("DATA_MIGRATION_PENDING: "+error.Message);}
            }
            if(Directory.GetFileSystemEntries(source).Length==0)Directory.Delete(source);
        }
        static bool SameBytes(string left,string right) {
            if(new FileInfo(left).Length!=new FileInfo(right).Length)return false;
            using(var a=File.OpenRead(left))using(var b=File.OpenRead(right)) {
                int value;while((value=a.ReadByte())!=-1)if(value!=b.ReadByte())return false;
                return true;
            }
        }
        internal static void StartCleanup(string backgroundExecutable,int backgroundPid) {
            var info=new ProcessStartInfo(backgroundExecutable,"--cleanup-data "+Process.GetCurrentProcess().Id+" "+backgroundPid) {
                UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden
            };
            using(var child=Process.Start(info)){if(child==null)throw new IOException("无法启动卸载文件清理程序。");}
        }
        internal static int CleanupAfterExit(int frontendPid,int backgroundPid) {
            using(var identity=WindowsIdentity.GetCurrent())
                if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))throw new UnauthorizedAccessException("清理数据需要管理员权限。");
            bool exited=WaitForExit(frontendPid,"CMP90HXControl") & WaitForExit(backgroundPid,"CMP90HXBackground");
            Cleanup(Root,exited,ScheduleDelete);
            Cleanup(LegacyRoot,exited,ScheduleDelete);
            return 0;
        }
        static bool WaitForExit(int pid,string expected) {
            if(pid<=0 || pid==Process.GetCurrentProcess().Id)return true;
            try {using(var process=Process.GetProcessById(pid))return process.ProcessName!=expected || process.WaitForExit(30000);}
            catch(ArgumentException){return true;}
            catch(InvalidOperationException){return true;}
        }
        internal static void Cleanup(string root,bool immediate,Action<string> schedule) {
            root=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if(!String.Equals(Path.GetFileName(root),"CMP90HX",StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(root)==null)
                throw new IOException("拒绝清理无效的数据目录。");
            if(Directory.Exists(root))DeleteEntry(root,root,immediate,schedule);
        }
        static void DeleteEntry(string root,string entry,bool immediate,Action<string> schedule) {
            string path=Path.GetFullPath(entry);
            if(!String.Equals(path,root,StringComparison.OrdinalIgnoreCase) && !path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new IOException("清理路径超出数据目录。");
            bool directory=Directory.Exists(path);
            // Never walk into junctions or symbolic links, including a linked root.
            if(directory && !IsLink(path))foreach(string child in Directory.GetFileSystemEntries(path))DeleteEntry(root,child,immediate,schedule);
            if(immediate) {
                try {if(directory)Directory.Delete(path,false);else File.Delete(path);return;}
                catch(IOException){}
                catch(UnauthorizedAccessException){}
            }
            schedule(path);
        }
        static void ScheduleDelete(string path) {
            if(!MoveFileEx(path,null,4))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"无法安排重启后清理："+path);
        }
        static bool IsLink(string path){return (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0;}
    }
}
