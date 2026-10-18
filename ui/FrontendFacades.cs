using System;
using System.IO;

namespace CMP90HX.Control {
    // These facades intentionally contain no PnP, driver, MMIO, NVAPI calls.
    internal sealed class RuntimePaths {
        internal const string Version="1.3.8";
        internal readonly string Logs;
        internal RuntimePaths(string directory) {Logs=Path.Combine(AppData.Root,"FrontendLogs");}
    }
    internal static class DriverService {
        internal static void ProtectDirectory(string directory) {Directory.CreateDirectory(directory);}
        internal static string CurrentState() {return BackendClient.Read().DriverState;}
    }
#if ENABLE_ROOT_CERTIFICATES
    internal static class CertificateManager {
        internal static string DisplayName {get{return CertificatePolicy.DisplayName;}}
        internal static readonly string[] Thumbprints={CertificatePolicy.Thumbprint};
        internal static bool[] Installed() {return BackendClient.Read().Certificates;}
        internal static void Install(RuntimePaths paths,Action<string> log) {BackendClient.Require("CertificatesInstall",null,log);}
        internal static void Uninstall(Action<string> log) {BackendClient.Require("CertificatesRemove",null,log);}
    }
#endif
    internal static class PowerStorage {
        internal static PowerSettings ReadSettings() {return BackendClient.Read().Power;}
        internal static PowerStatus ReadStatus() {return BackendClient.Read().PowerStatus;}
    }
    internal static class PowerActions {
        internal static void Enable(RuntimePaths paths,PowerSettings settings,Action<string> log) {BackendClient.Require("PowerEnable",ControlMemory.Json().Serialize(settings),log);}
        internal static void Disable(Action<string> log) {BackendClient.Require("PowerDisable",null,log);}
    }
}
