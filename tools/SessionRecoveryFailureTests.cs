using System;
using CMP90HX.Control;

static class SessionRecoveryFailureTests {
    static int count;
    static void Check(bool condition,string name) {
        if(!condition)throw new Exception(name);
        count++;Console.WriteLine("PASS "+name);
    }
    static int Main() {
        string denied="FATAL: System.ComponentModel.Win32Exception (0x80004005): Open CMP90HXUnlockSession: require administrator, temporary driver Code 0 and matching worker; reboot after SYS update: Win32=5 (0x00000005): 拒绝访问。";
        var failure=SessionRecoveryFailure.ClassifyWorkerLog(denied);
        Check(failure.Code=="SESSION_RECOVERY_ACCESS_DENIED" && !failure.RequiresReboot,"customer access-denied log is not a DMA/reboot failure");
        Check(failure.Detail.Contains("尚未读取 DMA 状态"),"access-denied display explains unavailable DMA state");
        Check(SessionRecoveryFailure.FromOutput("BACKGROUND_FAILED: "+failure.Message)==failure,"background code selects the matching desktop presentation");
        failure=SessionRecoveryFailure.ClassifyWorkerLog(denied.Replace("Win32=5 (0x00000005)","Win32=50 (0x00000032)"));
        Check(failure.Code=="SESSION_RECOVERY_CONTROL_UNAVAILABLE" && !failure.RequiresReboot,"Win32 50 is not confused with Win32 5");
        failure=SessionRecoveryFailure.ClassifyWorkerLog(denied.Replace("Win32=5 (0x00000005)","Win32=2 (0x00000002)"));
        Check(failure.Code=="SESSION_RECOVERY_CONTROL_UNAVAILABLE","missing control device does not imply DMA quarantine");
        failure=SessionRecoveryFailure.ClassifyWorkerLog("SESSION_STATE busy=0 quarantine=1 retained=1\nFATAL: Busy/quarantined session: reboot before restoring NVIDIA.");
        Check(failure.Code=="SESSION_RECOVERY_REQUIRES_REBOOT" && failure.RequiresReboot,"explicit failed DMA safety gate retains reboot guidance");
        failure=SessionRecoveryFailure.ClassifyWorkerLog("FATAL: Original NVIDIA INF changed; refusing replacement package.");
        Check(failure.Code=="SESSION_RECOVERY_INF_MISMATCH" && !failure.RequiresReboot,"INF mismatch has a distinct recovery message");
        failure=SessionRecoveryFailure.ClassifyWorkerLog("FATAL: Driver binding requires reboot; no unlock IOCTL will be submitted.");
        Check(failure.Code=="SESSION_RECOVERY_BINDING_REQUIRES_REBOOT" && failure.RequiresReboot && !failure.Detail.Contains("DMA"),"installer reboot is distinct from a failed DMA gate");
        failure=SessionRecoveryFailure.ClassifyWorkerLog("FATAL: Direct NVIDIA restore requires reboot; retain recovery record.");
        Check(failure.Code=="SESSION_RECOVERY_BINDING_REQUIRES_REBOOT","direct installer reboot is classified");
        failure=SessionRecoveryFailure.ClassifyWorkerLog("Unexpected error; reboot after SYS update");
        Check(failure.Code=="SESSION_AUTO_RESTORE_FAILED" && !failure.RequiresReboot,"generic reboot advice cannot trigger the reboot classification");
        Check(SessionRecoveryFailure.ClassifyWorkerLog(null).Code=="SESSION_AUTO_RESTORE_FAILED","missing worker log remains an unknown failure");
        Check(SessionRecoveryFailure.FromOutput("SESSION_UNLOCK_COMPLETE")==null,"success output is not classified as a recovery failure");
        Check(SessionRecoveryFailure.FromOutput("session_recovery_access_denied").Code=="SESSION_RECOVERY_ACCESS_DENIED","stable error code matching is case insensitive");
        Console.WriteLine("Session recovery regression checks passed: "+count);return 0;
    }
}
