using System;

namespace CMP90HX.Control {
    // Shared by the background classifier and the desktop presentation.
    internal sealed class SessionRecoveryFailure {
        internal readonly string Code,Title,Detail,Footer;
        internal readonly bool RequiresReboot;
        SessionRecoveryFailure(string code,string title,string detail,string footer,bool reboot=false) {
            Code=code;Title=title;Detail=detail;Footer=footer;RequiresReboot=reboot;
        }
        static readonly SessionRecoveryFailure[] Known={
            new SessionRecoveryFailure("SESSION_RECOVERY_ACCESS_DENIED","无法访问 Session 控制设备",
                "控制设备拒绝访问，尚未读取 DMA 状态。请确认管理员权限及 worker 与当前绑定驱动是否配套；若仍绑定旧驱动，请先使用旧包配套 worker 恢复 NVIDIA。",
                "恢复未完成 · 检查权限与配套驱动"),
            new SessionRecoveryFailure("SESSION_RECOVERY_CONTROL_UNAVAILABLE","无法打开 Session 控制设备",
                "尚未读取 DMA 状态，无法确认是否可恢复 NVIDIA。请检查设备状态、驱动是否加载及配套 worker，保留恢复记录并查看 session-auto-restore.log。",
                "恢复未完成 · 控制设备不可用"),
            new SessionRecoveryFailure("SESSION_RECOVERY_REQUIRES_REBOOT","恢复 NVIDIA 需要重启",
                "已读取 Session 状态，但 DMA 安全检查未通过（busy、quarantine 或 retained 等条件）。本次操作已停止，请保留恢复记录，重启 Windows 后再恢复 NVIDIA。",
                "等待重启",true),
            new SessionRecoveryFailure("SESSION_RECOVERY_INF_MISMATCH","原 NVIDIA INF 校验失败",
                "当前 NVIDIA INF 的摘要与恢复记录不一致，程序已拒绝重新绑定。请保留恢复记录和日志，核对原驱动包；不要修改摘要或删除记录绕过检查。",
                "恢复未完成 · 核对原驱动包"),
            new SessionRecoveryFailure("SESSION_RECOVERY_BINDING_REQUIRES_REBOOT","NVIDIA 驱动绑定需要重启",
                "Windows 驱动安装接口要求重启，尚未确认 NVIDIA 已正常接管。请保留恢复记录，重启后检查并完成恢复。",
                "等待重启",true),
            new SessionRecoveryFailure("SESSION_AUTO_RESTORE_FAILED","NVIDIA 自动恢复未完成",
                "本次操作已停止。请保留恢复记录并查看 session-auto-restore.log，确认具体失败原因。",
                "恢复未完成")
        };
        static bool Has(string text,string value) {return text.IndexOf(value,StringComparison.OrdinalIgnoreCase)>=0;}
        internal static SessionRecoveryFailure ClassifyWorkerLog(string log) {
            log=log??"";
            // Match worker failure messages, never the word 'reboot' in advice.
            if(Has(log,"Open CMP90HXUnlockSession")) {
                bool denied=Has(log,"Win32=5 (") || Has(log,"Win32=5:") || Has(log,"(0x00000005)");
                return Known[denied?0:1];
            }
            if(Has(log,"Busy/quarantined session: reboot before restoring NVIDIA."))return Known[2];
            if(Has(log,"Original NVIDIA INF changed; refusing replacement package."))return Known[3];
            if(Has(log,"Driver binding requires reboot; no unlock IOCTL will be submitted.") ||
                Has(log,"Direct NVIDIA restore requires reboot; retain recovery record."))return Known[4];
            return Known[5];
        }
        internal static SessionRecoveryFailure FromOutput(string output) {
            foreach(var failure in Known)if(Has(output??"",failure.Code))return failure;
            return null;
        }
        internal string Message {get{return Code+": "+Detail;}}
    }
}
