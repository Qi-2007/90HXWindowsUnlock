using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;

namespace CMP90HX.Control
{
    internal static class CertificateManager
    {
#if ENABLE_ROOT_CERTIFICATES
        internal static readonly string[] Names={CertificatePolicy.FileName};
        internal static readonly string[] Thumbprints={CertificatePolicy.Thumbprint};
        internal static string DisplayName { get { return CertificatePolicy.DisplayName; } }
        static readonly string[] Hashes={CertificatePolicy.Hash};
        internal static bool[] Installed()
        {
            using(var store=new X509Store(StoreName.Root,StoreLocation.LocalMachine)) {
                store.Open(OpenFlags.ReadOnly);
                return new[]{store.Certificates.Find(X509FindType.FindByThumbprint,Thumbprints[0],false).Count>0};
            }
        }
        internal static bool Ready() { return Installed()[0]; }
        internal static void EnsureInstalled(RuntimePaths paths,Action<string> log)
        {
            EnsureInstalled(Ready,()=>Install(paths,log),log);
        }
        // Delegate overload keeps installation policy testable without changing system trust.
        internal static void EnsureInstalled(Func<bool> ready,Action install,Action<string> log)
        {
            if(ready()) {log("CERTIFICATE_ALREADY_INSTALLED: 系统根证书已安装，继续安装驱动。");return;}
            log("CERTIFICATE_AUTO_INSTALL: 缺少系统根证书，正在安装随包证书。");
            install();
            if(!ready()) throw new IOException("系统根证书安装后验证失败，已停止驱动安装。");
        }
        internal static void RequireInstalled()
        {
            bool[] installed=Installed();
            for(int i=0;i<Names.Length;i++) if(!installed[i]) throw new IOException("缺少系统根证书："+Names[i]+"。请在驱动管理中安装证书。");
        }
        internal static void ValidateFiles(RuntimePaths paths)
        {
            for(int i=0;i<Names.Length;i++) using(var cert=Load(paths,i)) { }
        }
        static X509Certificate2 Load(RuntimePaths paths,int index)
        {
            string path=Path.Combine(paths.Certificates,Names[index]);
            RuntimePaths.RequireHash(path,Hashes[index]);
            var cert=new X509Certificate2(path);
            if(!String.Equals(cert.Thumbprint,Thumbprints[index],StringComparison.OrdinalIgnoreCase)) {
                cert.Dispose(); throw new IOException("证书指纹不匹配："+path);
            }
            return cert;
        }
        internal static void Install(RuntimePaths paths,Action<string> log)
        {
            // Validate the build-selected certificate before changing the trust store.
            ValidateFiles(paths);
            using(var store=new X509Store(StoreName.Root,StoreLocation.LocalMachine)) {
                store.Open(OpenFlags.ReadWrite);
                for(int i=0;i<Names.Length;i++) using(var cert=Load(paths,i)) {
                    if(store.Certificates.Find(X509FindType.FindByThumbprint,Thumbprints[i],false).Count==0) store.Add(cert);
                    log("CERTIFICATE_INSTALLED LocalMachine/Root "+Names[i]+" "+Thumbprints[i]);
                }
            }
            RequireInstalled();
        }
        internal static void Uninstall(Action<string> log)
        {
            using(var store=new X509Store(StoreName.Root,StoreLocation.LocalMachine)) {
                store.Open(OpenFlags.ReadWrite);
                for(int i=0;i<Names.Length;i++) {
                    var matches=store.Certificates.Find(X509FindType.FindByThumbprint,Thumbprints[i],false);
                    foreach(var certificate in matches) store.Remove(certificate);
                    log("CERTIFICATE_REMOVED LocalMachine/Root "+Thumbprints[i]);
                }
            }
        }
#else
        internal static readonly string[] Names=new string[0],Thumbprints=new string[0];
        internal static string DisplayName { get { return ""; } }
        internal static bool[] Installed() { return new bool[0]; }
        internal static bool Ready() { return true; }
        internal static void EnsureInstalled(RuntimePaths paths,Action<string> log) { }
        internal static void RequireInstalled() { }
        internal static void ValidateFiles(RuntimePaths paths) { }
        internal static void Install(RuntimePaths paths,Action<string> log) { throw new NotSupportedException("当前构建未启用根证书功能。"); }
        internal static void Uninstall(Action<string> log) { throw new NotSupportedException("当前构建未启用根证书功能。"); }
#endif
    }
}
