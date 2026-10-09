using System;
using System.IO;
using System.Threading;

namespace CMP90HX.Control
{
    internal sealed class PowerHardwareGate : IDisposable
    {
        readonly Mutex mutex=SharedSynchronization.OpenMutex(SharedSynchronization.HardwareName);
        bool owned;
        internal PowerHardwareGate()
        {
            try {
                try {owned=mutex.WaitOne(10000);} catch(AbandonedMutexException) {owned=true;}
                if(!owned) throw new IOException("解锁或硬件操作仍在运行，请完成后再修改省电设置。");
            } catch {mutex.Dispose();throw;}
        }
        public void Dispose() {if(owned) {owned=false;mutex.ReleaseMutex();}mutex.Dispose();}
    }
    internal static class PowerTasks
    {
        internal static void Enable(RuntimePaths paths,PowerSettings settings,Action<string> log)
        {
            settings.Validate();
            if(InspectorConflict.Running()) throw new IOException("请先退出 Inspector 的 Multi Display Power Saver，再启用内置省电；普通 Inspector 监控窗口可以保留。");
            paths.Validate(true,true);
            // Probe only reads. No P-state write until the user enables the background task.
            using(var gate=new PowerHardwareGate())
            using(var api=new NvidiaPowerApi()) {var sample=api.Read();log("NVAPI_READY P"+sample.Pstate+" GPU="+sample.Gpu+"% VPU="+sample.Video+"%");}
            Disable(log,false);
            TaskManagement.RegisterPower(paths,log);
            bool inspectorChanged=false;
            try {
                settings.Enabled=true;PowerStorage.WriteSettings(settings);
                inspectorChanged=TaskManagement.DisableInspectorStartup(log);
                TaskManagement.Run(log,TaskManagement.PowerName);
            } catch {
                try {Disable(log);} catch(Exception error) {log("POWER_ENABLE_ROLLBACK_FAILED: "+error.Message);}
                if(inspectorChanged) {
                    try {TaskManagement.RestoreInspectorStartup(log);} catch(Exception error) {log("INSPECTOR_STARTUP_RESTORE_FAILED: "+error.Message);}
                }
                throw;
            }
            log("POWER_SAVER_ENABLED threshold="+settings.Threshold+" idle="+settings.IdleSeconds+"s");
        }
        internal static void Disable(Action<string> log,bool remove=true)
        {
            var settings=PowerStorage.ReadSettings();settings.Enabled=false;PowerStorage.WriteSettings(settings);
            using(var coordination=new PowerCoordination()) {
                for(int i=0;i<100 && coordination.Running.WaitOne(0);i++) Thread.Sleep(100);
                if(coordination.Running.WaitOne(0)) throw new IOException("后台仍在恢复自动性能，未强制结束。请查看省电日志。");
            }
            var state=PowerStorage.ReadStatus();
            if(state!=null && state.LimitApplied) {
                using(var gate=new PowerHardwareGate())
                using(var api=new NvidiaPowerApi()) api.Limit(0);
                state.LimitApplied=false;state.Policy="已停用";state.HeartbeatUtc=DateTime.UtcNow;PowerStorage.WriteStatus(state);
            }
            if(remove) TaskManagement.Uninstall(log,TaskManagement.PowerName);
            log("POWER_SAVER_DISABLED: 已恢复 NVIDIA 自动性能策略。");
        }
    }
}
