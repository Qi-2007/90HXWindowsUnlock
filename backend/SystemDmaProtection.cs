using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CMP90HX.Control {
    internal static class SystemDmaProtection {
        // SystemDmaGuardPolicyInformation (202), BOOLEAN DmaGuardPolicyEnabled.
        // phnt/ntexapi.h. Unsupported systems must not be treated as protection disabled.
        [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int informationClass,out byte information,uint length,out uint returned);
        internal static bool? Query(out string error) {
            byte enabled;uint returned;
            int status=NtQuerySystemInformation(202,out enabled,1,out returned);
            error=status==0 && returned==1 && enabled<=1?null:"无法查询内核 DMA 保护：NTSTATUS=0x"+status.ToString("x8")+"。请在 msinfo32 中确认。";
            return error==null?(bool?)(enabled!=0):null;
        }
        internal static bool? Check(Action<string> log) {
            string error;var enabled=Query(out error);
            log(enabled.HasValue?"KERNEL_DMA_PROTECTION="+(enabled.Value?"ON":"OFF"):error);
            if(enabled==true)log("内核 DMA 保护已开启，无法使用 Native 模式，请选择 Session 模式。");
            return enabled;
        }
        internal static void RequireNative(Action<string> log) {
            if(Check(log)!=false)throw new IOException("NATIVE_DMA_PROTECTION_BLOCKED: 内核 DMA 保护已开启或状态无法确认，无法使用 Native 模式；请选择 Session 模式。");
        }
    }
}
