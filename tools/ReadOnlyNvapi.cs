// Documented NVIDIA NVAPI getters only. No private RM command or configuration API.
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class ReadOnlyNvapi
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern IntPtr LoadLibraryEx(string file,IntPtr reserved,uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Ansi,ExactSpelling=true,SetLastError=true)]
    static extern IntPtr GetProcAddress(IntPtr module,string name);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]
    static extern uint GetModuleFileName(IntPtr module,StringBuilder name,int size);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Query(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NoArgs();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Enumerate([Out,MarshalAs(UnmanagedType.LPArray,SizeConst=64)] IntPtr[] handles,out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PciIds(IntPtr gpu,out uint device,out uint subsystem,out uint revision,out uint external);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GpuInfo(IntPtr gpu,IntPtr info);
    static T Resolve<T>(Query query,uint id) where T:class
    {
        IntPtr p=query(id);
        if(p==IntPtr.Zero) throw new Exception("NVAPI interface unavailable: 0x"+id.ToString("x8"));
        return Marshal.GetDelegateForFunctionPointer(p,typeof(T)) as T;
    }
    static void Check(string name,int status)
    {
        Console.WriteLine("{0} Status={1}",name,status);
        if(status!=0) throw new Exception(name+" failed with NVAPI status "+status+"; returned fields are not interpreted.");
    }
    public static void Run()
    {
        IntPtr module=LoadLibraryEx("nvapi64.dll",IntPtr.Zero,0x800); // System32 only
        if(module==IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Load System32 nvapi64.dll");
        NoArgs unload=null;bool initialized=false;
        try {
            var path=new StringBuilder(32768);
            if(GetModuleFileName(module,path,path.Capacity)==0) throw new Exception("Cannot identify loaded NVAPI module");
            Console.WriteLine("NVAPI_MODULE="+path);
            IntPtr entry=GetProcAddress(module,"nvapi_QueryInterface");
            if(entry==IntPtr.Zero) throw new Exception("nvapi_QueryInterface missing");
            var query=(Query)Marshal.GetDelegateForFunctionPointer(entry,typeof(Query));
            var initialize=Resolve<NoArgs>(query,0x0150e828);
            unload=Resolve<NoArgs>(query,0xd22bdd7e);
            Check("NvAPI_Initialize",initialize());initialized=true;
            var enumerate=Resolve<Enumerate>(query,0xe5ac921f);
            var pci=Resolve<PciIds>(query,0x2ddfb66e);
            var getInfo=Resolve<GpuInfo>(query,0xafd1b02c);
            var handles=new IntPtr[64];uint count;
            Check("NvAPI_EnumPhysicalGPUs",enumerate(handles,out count));
            if(count>64) throw new Exception("NVAPI GPU count exceeds documented capacity");
            int targets=0;
            for(int i=0;i<count;i++) {
                uint id,subsystem,revision,external;
                Check("NvAPI_GPU_GetPCIIdentifiers["+i+"]",pci(handles[i],out id,out subsystem,out revision,out external));
                Console.WriteLine("NVAPI_GPU[{0}] DeviceId=0x{1:x8}; Subsystem=0x{2:x8}; Revision=0x{3:x8}; ExtDeviceId=0x{4:x8}",i,id,subsystem,revision,external);
                if(id!=0x220d10de) continue;
                targets++;
                // Official NV_GPU_INFO_V2: 80 bytes, version=(2<<16)|80;
                // reserved fields zero, RT count at +16 and tensor count at +20.
                IntPtr buffer=Marshal.AllocHGlobal(80);
                try {
                    Marshal.Copy(new byte[80],0,buffer,80);
                    Marshal.WriteInt32(buffer,0x20050);
                    Check("NvAPI_GPU_GetGPUInfo(V2)",getInfo(handles[i],buffer));
                    Console.WriteLine("NVAPI_RayTracingCores={0}; NVAPI_TensorCores={1} (driver reports; not hardware performance proof)",unchecked((uint)Marshal.ReadInt32(buffer,16)),unchecked((uint)Marshal.ReadInt32(buffer,20)));
                } finally {Marshal.FreeHGlobal(buffer);}
            }
            if(targets!=1) throw new Exception("Expected exactly one NVAPI 10de:220d; found "+targets);
        } finally {
            if(initialized && unload!=null) Console.WriteLine("NvAPI_Unload Status="+unload());
            FreeLibrary(module);
        }
    }
}
