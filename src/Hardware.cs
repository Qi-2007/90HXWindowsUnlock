using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace CMP90HX
{
    public interface IHardware : IDisposable
    {
        uint ReadPci(uint bdf, uint offset, int size);
        void WritePci(uint bdf, uint offset, uint value, int size);
        uint ReadMmio(ulong address);
        void WriteMmio(ulong address, uint value);
        void Delay(int milliseconds);
    }

    // Synchronous register IOCTLs do not call back into managed code. Reuse
    // buffers per thread, never between simultaneous requests on two threads.
    sealed class RegisterIoPacket
    {
        [ThreadStatic] static RegisterIoPacket current;
        internal static RegisterIoPacket Current {
            get { return current ?? (current=new RegisterIoPacket()); }
        }
        internal readonly byte[] Input=new byte[16], Output=new byte[4];
        internal int InputLength, OutputLength;
        int valueWidth;
        void Word(int offset,uint value)
        {
            for(int i=0;i<4;i++) Input[offset+i]=unchecked((byte)(value>>(8*i)));
        }
        internal void Pci(uint bdf,uint offset,int size,bool write,uint value)
        {
            Word(0,bdf); Word(4,offset); Word(8,(uint)size); Word(12,write?value:0);
            InputLength=16; OutputLength=write?0:4; valueWidth=size;
        }
        internal void Physical(ulong address,bool write,uint value)
        {
            Word(0,unchecked((uint)address)); Word(4,(uint)(address>>32));
            Word(8,write?value:0); Word(12,0);
            InputLength=16; OutputLength=write?0:4; valueWidth=4;
        }
        internal void Target(uint gpu,uint bridge)
        { Word(0,gpu); Word(4,bridge); InputLength=8; OutputLength=0; valueWidth=0; }
        internal uint Value {
            get {
                uint value=0;
                for(int i=0;i<valueWidth;i++) value|=(uint)Output[i]<<(8*i);
                return value;
            }
        }
    }

    // Unified CMP90HXDma transport. PCI reads use the exact
    // width: a 32-bit access at LNKSTA (cap+0x12) would cross register boundaries.
    public sealed class WindowsHardware : IHardware
    {
        readonly KernelDma driver;
        BridgeEcam bridgeConfig;

        public WindowsHardware(string driverDirectory)
        {
            if (!Environment.Is64BitProcess) throw new Exception("Run the x64 executable.");
            // --drivers is retained for command-line compatibility; no reference
            // driver is loaded. The unified signed driver must already be started.
            driver=new KernelDma(false);
        }

        internal KernelDma MapArena() { driver.MapArena(); return driver; }
        internal void Bind(uint gpu,uint bridge) { driver.Bind(gpu,bridge); }
        public uint ReadPci(uint bdf, uint offset, int size)
        {
            CheckPci(bdf, offset, size);
            RegisterIoPacket packet=RegisterIoPacket.Current;
            packet.Pci(bdf,offset,size,false,0);
            try { driver.RegisterCall(KernelDma.PciReadCode, packet); }
            catch (Win32Exception e) { throw Native.Failure(e.NativeErrorCode,String.Format("PCI READ {0}+0x{1:x} width={2}; {3}",Gen2Engine.Bdf(bdf),offset,size,e.Message)); }
            return packet.Value;
        }

        public void WritePci(uint bdf, uint offset, uint value, int size)
        {
            CheckPci(bdf, offset, size);
            if (bridgeConfig!=null && bridgeConfig.IsTarget(bdf)) { bridgeConfig.Write(offset,value,size); return; }
            RegisterIoPacket packet=RegisterIoPacket.Current;
            packet.Pci(bdf,offset,size,true,value);
            try { driver.RegisterCall(KernelDma.PciWriteCode, packet); }
            catch (Win32Exception e) { throw Native.Failure(e.NativeErrorCode,String.Format("PCI WRITE {0}+0x{1:x} width={2} value=0x{3:x}; {4}",Gen2Engine.Bdf(bdf),offset,size,value,e.Message)); }
        }

        internal void EnableBridgeEcam(Snapshot device,Action<string> log)
        { bridgeConfig=new BridgeEcam(this,BridgeEcam.ReadMcfg(),device,log); }

        static void CheckPci(uint bdf, uint offset, int size)
        {
            if (bdf > 0xffff || (size != 1 && size != 2 && size != 4) ||
                offset + size > 256 || (offset % size) != 0)
                throw new ArgumentException("Invalid segment-0 PCI access.");
        }

        public uint ReadMmio(ulong address)
        {
            if ((address & 3) != 0) throw new ArgumentException("Unaligned MMIO read.");
            RegisterIoPacket packet=RegisterIoPacket.Current;
            packet.Physical(address,false,0);
            try { driver.RegisterCall(KernelDma.MmioReadCode, packet); return packet.Value; }
            catch (Win32Exception e) { throw Native.Failure(e.NativeErrorCode,"PHYSICAL READ32 0x"+address.ToString("x")+"; "+e.Message); }
        }

        public void WriteMmio(ulong address, uint value)
        {
            if ((address & 3) != 0) throw new ArgumentException("Unaligned MMIO write.");
            RegisterIoPacket packet=RegisterIoPacket.Current;
            packet.Physical(address,true,value);
            try { driver.RegisterCall(KernelDma.MmioWriteCode, packet); }
            catch (Win32Exception e) { throw Native.Failure(e.NativeErrorCode,String.Format("PHYSICAL WRITE32 0x{0:x} value=0x{1:x8}; {2}",address,value,e.Message)); }
        }

        public void Delay(int milliseconds) { Thread.Sleep(milliseconds); }

        internal static void PreciseShortDelay(int milliseconds)
        {
            if (milliseconds < 0 || milliseconds > 2) throw new ArgumentOutOfRangeException("milliseconds");
            long start = Stopwatch.GetTimestamp();
            long ticks = (Stopwatch.Frequency * milliseconds + 999) / 1000;
            // Keep the native millisecond contract without Sleep(1)'s scheduler
            // rounding. Bounded spinning costs one CPU during firmware polling.
            while (Stopwatch.GetTimestamp() - start < ticks) Thread.SpinWait(32);
        }

        public void Dispose()
        {
            if(driver!=null) driver.Dispose();
        }
    }
    static class Native
    {
        // SERVICE_KERNEL_DRIVER ImagePath is a filename, not a command line.
        // Quotes remain literal; use the NT DOS-device prefix for local drives.
        internal static string DriverImagePath(string path)
        {
            string full = Path.GetFullPath(path);
            if (full.Length < 3 || full[1] != ':' || full[2] != '\\')
                throw new ArgumentException("Driver must be on a local drive: " + path);
            return @"\??\" + full;
        }

        internal static Win32Exception Failure(int error, string operation)
        {
            return new Win32Exception(error, String.Format("{0}: Win32={1} (0x{2:x8}): {3}",
                operation, error, unchecked((uint)error), new Win32Exception(error).Message));
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint GetSystemFirmwareTable(uint provider,uint table,[Out] byte[] buffer,uint bytes);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr OpenSCManager(string machine, string database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateService(IntPtr manager, string name, string display, uint access, uint type, uint start, uint error, string path, string group, IntPtr tag, string dependencies, string user, string password);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool StartService(IntPtr service, uint count, IntPtr args);
        [StructLayout(LayoutKind.Sequential)]
        public struct ServiceStatus { public uint Type, State, Accepted, Win32Exit, ServiceExit, Checkpoint, WaitHint; }
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteService(IntPtr service);
        [DllImport("advapi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseServiceHandle(IntPtr handle);
    }
}
