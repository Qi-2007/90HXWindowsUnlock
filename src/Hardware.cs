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
        internal readonly byte[] Input=new byte[12], Output=new byte[4];
        internal int InputLength, OutputLength;
        void Word(int offset,uint value)
        {
            for(int i=0;i<4;i++) Input[offset+i]=unchecked((byte)(value>>(8*i)));
        }
        internal void Pci(uint bdf,uint offset,int size,bool write,uint value)
        {
            Word(0,bdf); Word(4,offset);
            if(write) Word(8,value);
            InputLength=write?8+size:8; OutputLength=write?0:size;
        }
        internal void Physical(ulong address,bool write,uint value)
        {
            Word(0,unchecked((uint)address)); Word(4,(uint)(address>>32));
            if(write) Word(8,value);
            InputLength=write?12:8; OutputLength=write?0:4;
        }
        internal uint Value {
            get {
                uint value=0;
                for(int i=0;i<OutputLength;i++) value|=(uint)Output[i]<<(8*i);
                return value;
            }
        }
    }

    // Same IOCTL transport as the reference project. PCI reads use the exact
    // width: a 32-bit access at LNKSTA (cap+0x12) would cross register boundaries.
    public sealed class WindowsHardware : IHardware
    {
        const uint PciReadIoctl = (40000U << 16) | (1U << 14) | (0x851U << 2);
        const uint PciWriteIoctl = (40000U << 16) | (2U << 14) | (0x852U << 2);
        SafeFileHandle pci, mmio;
        BridgeEcam bridgeConfig;
        readonly List<DriverLease> leases = new List<DriverLease>();

        public WindowsHardware(string driverDirectory)
        {
            if (!Environment.Is64BitProcess) throw new Exception("Run the x64 executable.");
            try
            {
                string suffix = Process.GetCurrentProcess().Id.ToString();
                pci = Open(@"\\.\WinRing0_1_2_0", "CMP90HX_WinRing0_" + suffix, "WinRing0x64.sys",
                    "11bd2c9f9e2397c9a16e0990e4ed2cf0679498fe0fd418a3dfdac60b5c160ee5", driverDirectory, false);
                mmio = Open(@"\\.\ThrottleStop", "CMP90HX_ThrottleStop_" + suffix, "ThrottleStop.sys",
                    "16f83f056177c4ec24c7e99d01ca9d9d6713bd0497eeedb777a3ffefa99c97f0", driverDirectory, true);
            }
            catch { Dispose(); throw; }
        }

        internal static string LoadedDevicePath(string defaultDevice, string service, bool serviceNamedDevice)
        {
            // The pinned ThrottleStop DriverEntry uses the last component of
            // its registry service path in both \\Device\\%ls and \\??\\%ls.
            // WinRing0 instead exports a fixed device name.
            return serviceNamedDevice ? @"\\.\" + service : defaultDevice;
        }

        SafeFileHandle Open(string device, string service, string file, string hash, string directory, bool serviceNamedDevice)
        {
            SafeFileHandle h = Native.CreateFile(device, 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (!h.IsInvalid) return h;
            int error = Marshal.GetLastWin32Error();
            h.Dispose();
            if (error != 2 && error != 3)
                throw Native.Failure(error, "Cannot open " + device + " (possibly in use)");
            if (directory == null)
                throw new Exception(device + " is unavailable. Use --drivers <directory> to load the reference drivers.");
            string path = Path.GetFullPath(Path.Combine(directory, file));
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                if (actual != hash) throw new Exception("Driver hash mismatch: " + path);
            }
            DriverLease lease = new DriverLease(service, path);
            leases.Add(lease);
            device = LoadedDevicePath(device, service, serviceNamedDevice);
            h = Native.CreateFile(device, 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid)
            {
                error = Marshal.GetLastWin32Error(); h.Dispose();
                throw Native.Failure(error, "Driver loaded but device unavailable: " + device);
            }
            return h;
        }

        static void Ioctl(SafeFileHandle h, uint code, RegisterIoPacket packet)
        {
            uint returned;
            if (!Native.DeviceIoControl(h, code, packet.Input, packet.InputLength,
                packet.Output, packet.OutputLength, out returned, IntPtr.Zero))
                throw Native.Failure(Marshal.GetLastWin32Error(), "IOCTL 0x" + code.ToString("x8"));
            if (returned != packet.OutputLength) throw new IOException("Short IOCTL response: " + returned + "/" + packet.OutputLength);
        }

        public uint ReadPci(uint bdf, uint offset, int size)
        {
            CheckPci(bdf, offset, size);
            RegisterIoPacket packet=RegisterIoPacket.Current;
            packet.Pci(bdf,offset,size,false,0);
            try { Ioctl(pci, PciReadIoctl, packet); }
            catch (Win32Exception e) { throw Native.Failure(e.NativeErrorCode,String.Format("PCI READ {0}+0x{1:x} width={2}; {3}",Gen2Engine.Bdf(bdf),offset,size,e.Message)); }
            return packet.Value;
        }

        public void WritePci(uint bdf, uint offset, uint value, int size)
        {
            CheckPci(bdf, offset, size);
            if (bridgeConfig!=null && bridgeConfig.IsTarget(bdf)) { bridgeConfig.Write(offset,value,size); return; }
            RegisterIoPacket packet=RegisterIoPacket.Current;
            packet.Pci(bdf,offset,size,true,value);
            try { Ioctl(pci, PciWriteIoctl, packet); }
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
            try { Ioctl(mmio, 0x80006498, packet); return packet.Value; }
            catch (Win32Exception e) { throw Native.Failure(e.NativeErrorCode,"PHYSICAL READ32 0x"+address.ToString("x")+"; "+e.Message); }
        }

        public void WriteMmio(ulong address, uint value)
        {
            if ((address & 3) != 0) throw new ArgumentException("Unaligned MMIO write.");
            RegisterIoPacket packet=RegisterIoPacket.Current;
            packet.Physical(address,true,value);
            try { Ioctl(mmio, 0x8000649C, packet); }
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
            if (mmio != null) { mmio.Dispose(); mmio = null; }
            if (pci != null) { pci.Dispose(); pci = null; }
            for (int i = leases.Count - 1; i >= 0; i--) leases[i].Dispose();
            leases.Clear();
        }
    }

    // Dedicated demand-start services. Existing reference devices are reused.
    // If our service name exists, fail rather than replace an unknown service.
    sealed class DriverLease : IDisposable
    {
        IntPtr manager, service;
        bool started;
        public DriverLease(string name, string path)
        {
            try
            {
                manager = Native.OpenSCManager(null, null, 3);
                if (manager == IntPtr.Zero) throw Native.Failure(Marshal.GetLastWin32Error(), "OpenSCManager (run elevated)");
                service = Native.CreateService(manager, name, name, 0xF01FF, 1, 3, 1,
                    Native.DriverImagePath(path), null, IntPtr.Zero, null, null, null);
                if (service == IntPtr.Zero)
                    throw Native.Failure(Marshal.GetLastWin32Error(), "CreateService " + name + " (existing service is not modified)");
                if (!Native.StartService(service, 0, IntPtr.Zero))
                    throw Native.Failure(Marshal.GetLastWin32Error(), "StartService " + name);
                started = true;
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            if (service != IntPtr.Zero)
            {
                Native.ServiceStatus status;
                bool stopped = !started;
                if (started)
                {
                    Native.ControlService(service, 1, out status);
                    for (int i = 0; i < 30; i++)
                    {
                        if (Native.QueryServiceStatus(service, out status) && status.State == 1)
                        { stopped = true; break; }
                        Thread.Sleep(100);
                    }
                }
                if (stopped) Native.DeleteService(service);
                else Console.Error.WriteLine("Driver did not stop; service retained for inspection.");
                Native.CloseServiceHandle(service); service = IntPtr.Zero;
            }
            if (manager != IntPtr.Zero) { Native.CloseServiceHandle(manager); manager = IntPtr.Zero; }
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
