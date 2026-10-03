using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;

namespace CMP90HX.Control
{
    internal static class RuntimeDeployment
    {
        [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,int process);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder path,ref uint count);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        internal static string Root {get {return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"CMP90HX","AutoUnlock");}}
        internal static IEnumerable<string> ActiveImages()
        {
            var files=new List<string>();
            foreach(string name in new[]{"CMP90HXControl","CMP90HXGen2"})
                foreach(var process in Process.GetProcessesByName(name)) using(process) {
                    IntPtr handle=OpenProcess(0x1000,false,process.Id);
                    if(handle==IntPtr.Zero) {
                        try {if(process.HasExited) continue;} catch(InvalidOperationException) {continue;}
                        throw new IOException("无法确认运行中程序使用的目录，暂缓文件清理。");
                    }
                    try {
                        uint count=32768;var path=new StringBuilder((int)count);
                        if(!QueryFullProcessImageName(handle,0,path,ref count)) throw new IOException("无法读取运行中程序路径，暂缓文件清理。");
                        files.Add(path.ToString());
                    } finally {CloseHandle(handle);}
                }
            return files;
        }
        static bool Managed(string path)
        {
            // The private deployment root and version/hash naming also identify partial
            // copies left by an interrupted deployment or cleanup.
            return Regex.IsMatch(Path.GetFileName(path),@"^\d+\.\d+\.\d+-[0-9a-fA-F]{16}$");
        }
        internal static IDisposable Lease(string directory)
        {
            string path=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            if(!String.Equals(Path.GetDirectoryName(path),Root,StringComparison.OrdinalIgnoreCase) || !Managed(path)) return null;
            if(HasLink(path)) throw new IOException("后台运行目录不能包含符号链接。");
            return new FileStream(Path.Combine(path,".in-use"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.ReadWrite);
        }
        static bool HasLink(string path)
        {
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) return true;
            foreach(string entry in Directory.GetFileSystemEntries(path)) {
                if((File.GetAttributes(entry)&FileAttributes.ReparsePoint)!=0 || Directory.Exists(entry) && HasLink(entry)) return true;
            }
            return false;
        }
        internal static void Clean(string root,IEnumerable<string> references,Action<string> log)
        {
            root=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if(!Directory.Exists(root)) return;
            if((File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0) throw new IOException("拒绝清理链接目录。");
            var used=references.Where(s=>!String.IsNullOrWhiteSpace(s)).Select(s=>Path.GetFullPath(Environment.ExpandEnvironmentVariables(s.Trim('"')))).ToArray();
            foreach(string directory in Directory.GetDirectories(root)) {
                string path=Path.GetFullPath(directory),prefix=path+Path.DirectorySeparatorChar;
                if(!String.Equals(Path.GetDirectoryName(path),root,StringComparison.OrdinalIgnoreCase) || !Managed(path) || HasLink(path)) continue;
                if(used.Any(s=>s.StartsWith(prefix,StringComparison.OrdinalIgnoreCase) || String.Equals(s,path,StringComparison.OrdinalIgnoreCase))) continue;
                try {
                    // Hold a lease that blocks new launches while allowing deletion of the marker.
                    using(var probe=new FileStream(Path.Combine(path,".in-use"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Delete))
                        Directory.Delete(path,true);
                    log("TASK_RUNTIME_REMOVED "+path);
                } catch(IOException) {log("TASK_RUNTIME_RETAINED: 运行中或文件仍被使用，稍后清理 "+path);}
                catch(UnauthorizedAccessException) {log("TASK_RUNTIME_RETAINED: 当前无法清理 "+path);}
            }
        }
    }
    internal sealed class DeploymentLock : IDisposable
    {
        readonly Mutex mutex=SharedSynchronization.OpenMutex(@"Global\CMP90HX_Deployment");
        bool owned;
        internal DeploymentLock()
        {
            try {
                try {owned=mutex.WaitOne(10000);} catch(AbandonedMutexException) {owned=true;}
                if(!owned) throw new IOException("后台文件管理正在运行，请稍后重试。");
            } catch {mutex.Dispose();throw;}
        }
        public void Dispose() {if(owned) mutex.ReleaseMutex();mutex.Dispose();}
    }
}
