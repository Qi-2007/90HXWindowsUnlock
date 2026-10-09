using System; using System.IO; using System.Linq; using System.Diagnostics; namespace CMP90HX.Control {    public sealed class PowerSettings
    {
        public bool Enabled {get;set;}
        public int Threshold {get;set;}
        public int IdleSeconds {get;set;}
        public string FullSpeedApps {get;set;}
        public PowerSettings() {Threshold=15;IdleSeconds=10;FullSpeedApps="";}
        internal void Validate()
        {
            if(Threshold<1 || Threshold>100 || IdleSeconds<1 || IdleSeconds>120 || (FullSpeedApps??"").Length>4096)
                throw new IOException("省电阈值需为 1–100%，空闲等待需为 1–120 秒。");
        }
        internal bool HasFullSpeedApp()
        {
            var names=(FullSpeedApps??"").Split(new[]{',',';','\r','\n'},StringSplitOptions.RemoveEmptyEntries)
                .Select(s=>Path.GetFileNameWithoutExtension(s.Trim())).Where(s=>s.Length>0).ToArray();
            if(names.Length==0) return false;
            foreach(var process in Process.GetProcesses()) using(process) {
                try {if(names.Contains(process.ProcessName,StringComparer.OrdinalIgnoreCase)) return true;}
                catch(InvalidOperationException) { }
                catch(System.ComponentModel.Win32Exception) { }
            }
            return false;
        }
    }
    public sealed class PowerStatus
    {
        public DateTime HeartbeatUtc {get;set;}
        public string Policy {get;set;}
        public string Detail {get;set;}
        public bool LimitApplied {get;set;}
        public PowerSample Sample {get;set;}
    }
        public sealed class PowerSample
    {
        public int Pstate {get;set;}
        public int Gpu {get;set;}
        public int Video {get;set;}
        public double? Watts {get;set;}
        public string InstanceId {get;set;}
    }
}
