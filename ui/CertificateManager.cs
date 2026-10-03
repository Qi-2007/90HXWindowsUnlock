using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;

namespace CMP90HX.Control
{
    internal static class CertificateManager
    {
        internal static readonly string[] Names={"Pikachu Test CA RSA.cer","Pikachu Time Sub CA.cer"};
        internal static readonly string[] Thumbprints={"F57DF00CEB2476C8144B751F1D2D45EDBB3EDFE1","671E553BC90A19B26F6F55231E5969F4771976BB"};
        static readonly string[] Hashes={"CCE4D5F3575B6E047C94448A3C9EC8B88094F0841E241EB9A26DEBD2362EBC4B","96AFA8CD191459CA5668067FE19CC0D5F63C8C82F7CD795E27B410DFE7B3F73D"};
        internal static bool[] Installed()
        {
            using(var store=new X509Store(StoreName.Root,StoreLocation.LocalMachine)) {
                store.Open(OpenFlags.ReadOnly);
                return new[]{store.Certificates.Find(X509FindType.FindByThumbprint,Thumbprints[0],false).Count>0,
                    store.Certificates.Find(X509FindType.FindByThumbprint,Thumbprints[1],false).Count>0};
            }
        }
        internal static bool Ready() { bool[] installed=Installed(); return installed[0] && installed[1]; }
        internal static void RequireInstalled()
        {
            bool[] installed=Installed();
            for(int i=0;i<2;i++) if(!installed[i]) throw new IOException("缺少系统根证书："+Names[i]+"。请在驱动管理中安装证书。");
        }
        internal static void ValidateFiles(RuntimePaths paths)
        {
            for(int i=0;i<2;i++) using(var cert=Load(paths,i)) { }
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
            // Validate both inputs before making either trust-store change.
            ValidateFiles(paths);
            using(var store=new X509Store(StoreName.Root,StoreLocation.LocalMachine)) {
                store.Open(OpenFlags.ReadWrite);
                for(int i=0;i<2;i++) using(var cert=Load(paths,i)) {
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
                for(int i=0;i<2;i++) {
                    var matches=store.Certificates.Find(X509FindType.FindByThumbprint,Thumbprints[i],false);
                    foreach(var certificate in matches) store.Remove(certificate);
                    log("CERTIFICATE_REMOVED LocalMachine/Root "+Thumbprints[i]);
                }
            }
        }
    }
}
