using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CMP90HX.Control {
    internal static class SessionDriverService {
        const string Inf="CMP90HXUnlockSession.inf";
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool SetupCopyOEMInfW(string source,string media,uint mediaType,uint flags,StringBuilder destination,uint size,out uint required,IntPtr component);
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool SetupGetInfPublishedNameW(string inf,StringBuilder published,uint size,out uint required);
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool SetupUninstallOEMInfW(string name,uint flags,IntPtr reserved);
        static string Package {get{return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"session");}}
        static List<string> InstalledPackages() {
            var result=new List<string>();
            string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"DriverStore","FileRepository");
            if(!Directory.Exists(root))return result;
            foreach(string directory in Directory.GetDirectories(root,"cmp90hxunlocksession.inf_amd64_*")) {
                string file=Path.Combine(directory,Inf);if(!File.Exists(file))continue;
                if(!File.ReadAllText(file).Contains("AddService=CMP90HXUnlockSession"))continue;
                uint needed;var published=new StringBuilder(32768);
                if(SetupGetInfPublishedNameW(file,published,(uint)published.Capacity,out needed)) {
                    // Some Windows versions return only oemNN.inf rather than a full path.
                    string name=published.ToString();
                    if(!Path.IsPathRooted(name))name=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"INF",name);
                    if(File.Exists(name))result.Add(file);
                }
            }return result;
        }
        internal static string RequireInstalled() {
            BackendPackagePolicy.VerifySession(Package);
            foreach(string file in InstalledPackages()) {
                string directory=Path.GetDirectoryName(file);
                bool match=true;
                foreach(string name in new[]{Inf,"CMP90HXUnlockSession.sys","CMP90HXUnlockSession.cat"})
                    if(!File.Exists(Path.Combine(directory,name)) || RuntimePaths.Hash(Path.Combine(directory,name))!=RuntimePaths.Hash(Path.Combine(Package,name)))match=false;
                if(match)return file;
            }
            throw new IOException("SESSION_DRIVER_NOT_INSTALLED: 请在驱动管理中手动安装当前版本的 Session 解锁驱动。");
        }
        internal static string CurrentState() {
            var packages=InstalledPackages();if(packages.Count==0)return "未安装";
            try {RequireInstalled();return "已安装 · 解锁时临时接管，完成后恢复 NVIDIA";}
            catch(IOException){return "已安装其他版本 · 请手动更新";}
        }
        internal static void Install(Action<string> log) {
            BackendPackagePolicy.VerifySession(Package);CertificateManager.RequireInstalled();
            SignatureTrust.Verify(Path.Combine(Package,"CMP90HXUnlockSession.sys"));SignatureTrust.Verify(Path.Combine(Package,"CMP90HXUnlockSession.cat"));
            uint needed;var published=new StringBuilder(32768);
            if(!SetupCopyOEMInfW(Path.Combine(Package,Inf),null,1,0,published,(uint)published.Capacity,out needed,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error(),"安装 Session 驱动包失败");
            log("SESSION_DRIVER_STAGED: "+published+"; validating installed INF/SYS/CAT.");
            RequireInstalled();log("SESSION_DRIVER_INSTALLED: "+published+"; NVIDIA binding unchanged.");
        }
        internal static void Uninstall(Action<string> log) {
            if(File.Exists(Path.Combine(PowerStorage.Root,"session-restore.json")))throw new IOException("存在待恢复的 Session 会话，请先运行 session-restore 恢复 NVIDIA，再卸载或切换模式。");
            foreach(string file in InstalledPackages()) {
                uint needed;var published=new StringBuilder(32768);
                if(!SetupGetInfPublishedNameW(file,published,(uint)published.Capacity,out needed))throw new Win32Exception(Marshal.GetLastWin32Error());
                // No force/uninstall flag: an in-use package must fail rather than remove a live device.
                if(!SetupUninstallOEMInfW(Path.GetFileName(published.ToString()),0,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error(),"卸载 Session 驱动失败；请先确认 NVIDIA 已接管设备");
                log("SESSION_DRIVER_REMOVED: "+published);
            }
        }
    }
}
