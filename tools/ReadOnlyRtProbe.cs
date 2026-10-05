// Standalone research probe. No register-write IOCTL, DMA arm, device reset,
// driver installation, or unlock workflow is reachable from this program.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public static class ReadOnlyRtProbe
{
    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
    [DllImport("d3d12.dll")] static extern int D3D12CreateDevice(IntPtr adapter, uint level, ref Guid iid, out IntPtr device);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]
    static extern bool DeviceIoControl(SafeFileHandle h,uint code,byte[] input,int il,byte[] output,int ol,out uint returned,IntPtr overlap);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumAdapters(IntPtr self,uint index,out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDesc(IntPtr self,IntPtr desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CheckFeature(IntPtr self,uint feature,IntPtr data,uint bytes);
    static T Method<T>(IntPtr obj,int slot) where T:class
    { return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj),slot*IntPtr.Size),typeof(T)) as T; }
    static string Hex(int value) { return "0x"+unchecked((uint)value).ToString("x8"); }
    static void Check(int hr) { if(hr<0) Marshal.ThrowExceptionForHR(hr); }

    static void Dxr()
    {
        Guid factoryId=new Guid("770aae78-f26f-4dba-a829-253c83d1b387"), deviceId=new Guid("189819f1-1db6-4b57-be54-1821339b85f7");
        IntPtr factory=IntPtr.Zero;
        try {
            Check(CreateDXGIFactory1(ref factoryId,out factory));
            var enumerate=Method<EnumAdapters>(factory,12);
            int targets=0;
            for(uint i=0;;i++) {
                IntPtr adapter;
                int hr=enumerate(factory,i,out adapter);
                if(unchecked((uint)hr)==0x887a0002) break;
                Check(hr);
                IntPtr desc=Marshal.AllocHGlobal(312),device=IntPtr.Zero,options=Marshal.AllocHGlobal(12);
                try {
                    Check(Method<GetDesc>(adapter,10)(adapter,desc));
                    uint vendor=unchecked((uint)Marshal.ReadInt32(desc,256)),id=unchecked((uint)Marshal.ReadInt32(desc,260));
                    Console.WriteLine("Adapter[{0}] {1} PCI={2:x4}:{3:x4} LUID={4:x8}:{5:x8}",i,Marshal.PtrToStringUni(desc),vendor,id,Marshal.ReadInt32(desc,300),Marshal.ReadInt32(desc,296));
                    if(vendor!=0x10de || id!=0x220d) continue;
                    targets++;
                    hr=D3D12CreateDevice(adapter,0xb000,ref deviceId,out device);
                    Console.WriteLine("D3D12CreateDevice(FL11_0) HRESULT="+Hex(hr));
                    if(hr<0) continue;
                    for(int n=0;n<12;n+=4) Marshal.WriteInt32(options,n,0);
                    hr=Method<CheckFeature>(device,13)(device,27,options,12);
                    Console.WriteLine("CheckFeatureSupport(OPTIONS5) HRESULT="+Hex(hr));
                    if(hr>=0) {
                        int tier=Marshal.ReadInt32(options,8);
                        Console.WriteLine("RaytracingTier={0} ({1}); RenderPassesTier={2}; SRVOnlyTiledResourceTier3={3}",
                            tier,tier==0?"NOT_SUPPORTED":tier==10?"1.0":tier==11?"1.1":"UNKNOWN",Marshal.ReadInt32(options,4),Marshal.ReadInt32(options,0));
                    }
                } finally { if(device!=IntPtr.Zero) Marshal.Release(device); Marshal.FreeHGlobal(options); Marshal.FreeHGlobal(desc); Marshal.Release(adapter); }
            }
            if(targets!=1) throw new Exception("Expected exactly one DXGI 10de:220d; found "+targets);
        } finally { if(factory!=IntPtr.Zero) Marshal.Release(factory); }
    }

    static byte[] Io(SafeFileHandle h,uint code,byte[] input,int size)
    {
        // This fixed whitelist deliberately excludes every write and DMA IOCTL.
        if(code!=0x8337e000 && code!=0x8337e010 && code!=0x8337e014 && code!=0x8337e01c) throw new Exception("Non-read IOCTL refused");
        byte[] output=new byte[size];uint returned;
        if(!DeviceIoControl(h,code,input,input.Length,output,size,out returned,IntPtr.Zero)) {
            int error=Marshal.GetLastWin32Error();
            throw new System.ComponentModel.Win32Exception(error,"CMP90HXDma IOCTL 0x"+code.ToString("x8")+" Win32="+error+" ("+new System.ComponentModel.Win32Exception(error).Message+")");
        }
        if(returned!=size) throw new Exception("Short IOCTL response");
        return output;
    }
    static uint Pci(SafeFileHandle h,uint bdf,uint offset,uint width)
    {
        byte[] p=new byte[16];Buffer.BlockCopy(new uint[]{bdf,offset,width,0},0,p,0,16);
        try { return BitConverter.ToUInt32(Io(h,0x8337e014,p,4),0); }
        catch(System.ComponentModel.Win32Exception e) {
            throw new System.ComponentModel.Win32Exception(e.NativeErrorCode,String.Format("PCI READ {0:x2}:{1:x2}.{2}+0x{3:x} width={4}; {5}",bdf>>8,(bdf>>3)&31,bdf&7,offset,width,e.Message));
        }
    }
    static bool Present(Func<uint,uint,uint,uint> read,uint bdf,out uint id)
    {
        try { id=read(bdf,0,4); }
        catch(System.ComponentModel.Win32Exception e) {
            // Only absence at the identity probe is tolerated. Access, protocol,
            // and reads of established devices still fail with full context.
            if(e.NativeErrorCode!=433 && e.NativeErrorCode!=1167) throw;
            Console.WriteLine("PCI identity scan miss at BDF=0x{0:x4}; Win32={1}",bdf,e.NativeErrorCode);
            id=0xffffffff;
        }
        return (id&65535)!=0 && (id&65535)!=65535;
    }
    static List<uint> Enumerate(Func<uint,uint,uint,uint> read)
    {
        var devices=new List<uint>();
        for(uint bus=0;bus<256;bus++) for(uint dev=0;dev<32;dev++) {
            uint bdf=(bus<<8)|(dev<<3),id;
            if(!Present(read,bdf,out id)) continue;
            devices.Add(bdf);
            if((read(bdf,0x0e,1)&0x80)==0) continue;
            for(uint fn=1;fn<8;fn++) if(Present(read,bdf|fn,out id)) devices.Add(bdf|fn);
        }
        return devices;
    }
    static uint Mmio(SafeFileHandle h,ulong address)
    { byte[] p=new byte[16];Buffer.BlockCopy(BitConverter.GetBytes(address),0,p,0,8);return BitConverter.ToUInt32(Io(h,0x8337e01c,p,4),0); }
    static void Registers(bool readRtFuse,bool readFeatureReadout)
    {
        using(var h=CreateFile(@"\\.\CMP90HXDma",0xc0000000,0,IntPtr.Zero,3,0,IntPtr.Zero)) {
            if(h.IsInvalid) {
                int error=Marshal.GetLastWin32Error();
                throw new System.ComponentModel.Win32Exception(error,"Open CMP90HXDma Win32="+error+" ("+new System.ComponentModel.Win32Exception(error).Message+"); administrator access required; no registers have been read");
            }
            byte[] info=Io(h,0x8337e000,new byte[0],40);
            if(BitConverter.ToUInt32(info,0)!=2 || BitConverter.ToUInt32(info,4)!=0) throw new Exception("Clean protocol-v2 driver required");
            uint gpu=0,bridge=0;int gpus=0,bridges=0;
            List<uint> devices=Enumerate(delegate(uint bdf,uint offset,uint width){return Pci(h,bdf,offset,width);});
            foreach(uint bdf in devices) if(Pci(h,bdf,0,4)==0x220d10de) {gpu=bdf;gpus++;}
            if(gpus!=1) throw new Exception("Expected exactly one segment-0 10de:220d; found "+gpus);
            foreach(uint bdf in devices) if((Pci(h,bdf,8,4)>>16)==0x0604 && ((Pci(h,bdf,0x18,4)>>8)&255)==gpu>>8) {bridge=bdf;bridges++;}
            if(bridges!=1) throw new Exception("Ambiguous upstream bridge");
            byte[] target=new byte[8];Buffer.BlockCopy(new uint[]{gpu,bridge},0,target,0,8);Io(h,0x8337e010,target,0);
            uint low=Pci(h,gpu,0x10,4);
            if(low==0 || low==0xffffffff || (low&1)!=0 || ((low&6)!=0 && (low&6)!=4) || (Pci(h,gpu,4,2)&2)==0) throw new Exception("Invalid BAR0 or memory decoding disabled");
            ulong bar=low&0xfffffff0u;if((low&6)==4) bar|=(ulong)Pci(h,gpu,0x14,4)<<32;
            uint boot=Mmio(h,bar);
            if((boot&0x1f000000)!=0x17000000 || (boot&0xf00000)!=0x200000) throw new Exception("Not GA102 BOOT0");
            Console.WriteLine("GPU BDF={0:x2}:{1:x2}.{2}; Bridge BDF={3:x2}:{4:x2}.{5}; BAR0=0x{6:x}; BOOT0=0x{7:x8}",gpu>>8,(gpu>>3)&31,gpu&7,bridge>>8,(bridge>>3)&31,bridge&7,bar,boot);
            // Preserve the established baseline; the extra RT query address is opt-in.
            foreach(uint offset in new uint[]{0x823808,0x82381c,0x823820,0x823830}) {
                uint value=Mmio(h,bar+offset);
                Console.WriteLine("BAR0+0x{0:x6}=0x{1:x8}{2}",offset,value,value==0xffffffff?" (ALL_ONES: uninterpretable)":"");
            }
            if(readRtFuse) {
                // R616 GA102 static path: query 0xff00008d -> relative 0x168,
                // mask 1, shift 0; direct MMIO mapping -> 0x820168.
                // This samples hardware, not the driver's routed/cached return value.
                uint value=Mmio(h,bar+0x820168);
                Console.WriteLine("BAR0+0x820168=0x{0:x8}{1}",value,value==0xffffffff?" (ALL_ONES: uninterpretable)":"");
                if(value!=0xffffffff) Console.WriteLine("R616_RT_QUERY_RAW_BIT0={0} (static-path input; not proof of effective RT state or performance)",value&1);
            }
            if(readFeatureReadout) {
                // GA102 service getter 0x959170 reads relative 0x3814;
                // direct address map 0x95a0f0 -> 0x823814. Record raw state
                // only: SM_TTU bit assignment and override semantics unverified.
                uint value=Mmio(h,bar+0x823814);
                Console.WriteLine("BAR0+0x823814=0x{0:x8}{1}",value,value==0xffffffff?" (ALL_ONES: uninterpretable)":" (raw feature readout; RT field semantics unverified)");
            }
        }
    }
    public static int Main(string[] args)
    {
        try {
            Console.OutputEncoding=new System.Text.UTF8Encoding(false);
            Console.SetError(new System.IO.StreamWriter(Console.OpenStandardError(),new System.Text.UTF8Encoding(false)){AutoFlush=true});
            if(IntPtr.Size!=8) throw new Exception("x64 process required");
            if(args.Length==1 && args[0]=="--self-test") { EnumerationTests();return 0; }
            bool readMmio=false,readRtFuse=false,readNvapi=false,readFeatureReadout=false;
            foreach(string arg in args) {
                if(arg=="--read-mmio" && !readMmio) readMmio=true;
                else if(arg=="--read-rt-fuse" && !readRtFuse) readRtFuse=true;
                else if(arg=="--read-nvapi" && !readNvapi) readNvapi=true;
                else if(arg=="--read-feature-readout" && !readFeatureReadout) readFeatureReadout=true;
                else throw new Exception("Usage: ReadOnlyRtProbe.exe [--read-mmio [--read-rt-fuse] [--read-feature-readout]] [--read-nvapi] | --self-test");
            }
            if(readRtFuse && !readMmio) throw new Exception("--read-rt-fuse requires --read-mmio");
            if(readFeatureReadout && !readMmio) throw new Exception("--read-feature-readout requires --read-mmio");
            Console.WriteLine("UTC="+DateTime.UtcNow.ToString("o"));
            Dxr();if(readNvapi) ReadOnlyNvapi.Run();if(readMmio) Registers(readRtFuse,readFeatureReadout);return 0;
        } catch(Exception e) {Console.Error.WriteLine(e);return 1;}
    }
    static void EnumerationTests()
    {
        // Sparse multifunction device: functions 1 and 3..7 are absent.
        Func<uint,uint,uint,uint> fake=delegate(uint bdf,uint offset,uint width) {
            bool exists=bdf==0x100 || bdf==0x102 || bdf==0x200;
            if(offset==0) return exists?0x220d10deu:0xffffffffu;
            if(!exists) throw new Exception("Read non-identity field on absent function");
            if(offset==0x0e) return bdf==0x100?0x80u:0u;
            return 0;
        };
        List<uint> found=Enumerate(fake);
        if(found.Count!=3 || !found.Contains(0x102)) throw new Exception("Sparse multifunction enumeration failed");
        uint id;
        foreach(int error in new int[]{433,1167}) {
            if(Present(delegate(uint b,uint o,uint w){throw new System.ComponentModel.Win32Exception(error);},0,out id)) throw new Exception("Absent device accepted");
        }
        foreach(int error in new int[]{5,6,87,1}) {
            bool propagated=false;
            try {Present(delegate(uint b,uint o,uint w){throw new System.ComponentModel.Win32Exception(error);},0,out id);}
            catch(System.ComponentModel.Win32Exception e) {propagated=e.NativeErrorCode==error;}
            if(!propagated) throw new Exception("Transport/access error swallowed");
        }
        Console.WriteLine("PASS: sparse multifunction discovery; absence handling; transport/access errors preserved; no hardware accessed.");
    }
}
