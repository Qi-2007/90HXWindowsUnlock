using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CMP90HX
{
    // Driver is explicitly installed/started by the operator. Never stop it on
    // process exit: its pages and crash quarantine belong to this Windows boot.
    sealed class KernelDma : IDisposable
    {
        const uint InfoCode=0x8337e000, MapCode=0x8337e004, ArmCode=0x8337e008, CompleteCode=0x8337e00c;
        SafeFileHandle handle;
        internal ArenaDescriptor Descriptor;
        internal IntPtr Mapping;
        internal bool Pending;
        internal KernelDma(bool map)
        {
            try {
                handle=Native.CreateFile(@"\\.\CMP90HXDma",0xc0000000,0,IntPtr.Zero,3,0,IntPtr.Zero);
                if(handle.IsInvalid) throw Native.Failure(Marshal.GetLastWin32Error(),"Open CMP90HXDma (install/start the signed experimental driver first)");
                byte[] info=Call(InfoCode,40);
                ValidateInfo(info,false);
                if(map) {
                    info=Call(MapCode,40); ValidateInfo(info,true);
                    Descriptor=new ArenaDescriptor {Physical=BitConverter.ToUInt64(info,8),Length=BitConverter.ToUInt64(info,16),Cookie=BitConverter.ToUInt64(info,32)};
                    Mapping=new IntPtr(checked((long)BitConverter.ToUInt64(info,24)));
                }
            } catch { Dispose(); throw; }
        }
        internal static void ValidateInfo(byte[] info,bool mapped)
        {
            if(info.Length!=40 || BitConverter.ToUInt32(info,0)!=1) throw new IOException("Unsupported kernel DMA protocol.");
            uint state=BitConverter.ToUInt32(info,4);
            if(state==1 || state==2) throw new RetainedDmaException("DMA_RETAINED: kernel arena is armed/quarantined. Keep GPU disabled and cold boot; no EFI bootstrap is needed for the kernel backend.");
            if(state!=0) throw new IOException("Invalid kernel DMA state.");
            if(!mapped) return;
            ulong physical=BitConverter.ToUInt64(info,8),length=BitConverter.ToUInt64(info,16),address=BitConverter.ToUInt64(info,24);
            if(physical<0x100000 || (physical&4095)!=0 || length!=32UL*1024*1024 || physical>0x100000000UL-length ||
                address==0 || address>Int64.MaxValue-length || (address&4095)!=0 || BitConverter.ToUInt64(info,32)==0)
                throw new IOException("Invalid kernel DMA allocation/map range.");
        }
        byte[] Call(uint code,int size)
        {
            byte[] output=new byte[size]; uint returned;
            if(!Native.DeviceIoControl(handle,code,new byte[0],0,output,size,out returned,IntPtr.Zero))
                throw Native.Failure(Marshal.GetLastWin32Error(),"Kernel DMA IOCTL 0x"+code.ToString("x8"));
            if(returned!=size) throw new IOException("Short kernel DMA response.");
            return output;
        }
        internal void Acquire() { Call(ArmCode,0); Pending=true; }
        internal void Release() { Call(CompleteCode,0); Pending=false; }
        public void Dispose() { if(handle!=null) { handle.Dispose(); handle=null; } }
    }
}
