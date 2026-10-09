using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace CMP90HX.Control
{
    internal sealed class PowerSample
    {
        public int Pstate {get;set;}
        public int Gpu {get;set;}
        public int Video {get;set;}
        public double? Watts {get;set;}
        public string InstanceId {get;set;}
    }
    internal interface IPowerGpu : IDisposable
    {
        PowerSample Read();
        void Limit(int pstate);
    }
    // Original, small ABI binding. No Inspector binary or upstream wrapper is bundled.
    internal sealed class NvidiaPowerApi : IPowerGpu
    {
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr LoadLibraryEx(string name,IntPtr file,uint flags);
        [DllImport("kernel32.dll",CharSet=CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module,string name);
        [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Query(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Simple();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumGpu([Out] IntPtr[] handles,out uint count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ReadUint(IntPtr handle,out uint value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PciIds(IntPtr handle,out uint device,out uint subsystem,out uint revision,out uint external);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DynamicInfo(IntPtr handle,[In,Out] byte[] data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetLimit(IntPtr handle,uint type,uint pstate);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NvmlHandle([MarshalAs(UnmanagedType.LPStr)] string pci,out IntPtr handle);
        IntPtr module,nvml;
        Query query;
        Simple unload,nvmlShutdown;
        EnumGpu enumerate;
        ReadUint bus,slot,pstate,powerUsage;
        PciIds pci;
        DynamicInfo utilization;
        SetLimit limit;
        NvmlHandle nvmlHandle;
        string lastInstance;
        bool initialized,nvmlReady;
        internal NvidiaPowerApi()
        {
            try {
                // Load only the driver-provided system DLL, never a DLL beside the application.
                module=LoadLibraryEx("nvapi64.dll",IntPtr.Zero,0x800);
                if(module==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"无法加载系统 nvapi64.dll。");
                IntPtr entry=GetProcAddress(module,"nvapi_QueryInterface");
                if(entry==IntPtr.Zero) throw new IOException("系统 NVAPI 缺少 QueryInterface。");
                query=(Query)Marshal.GetDelegateForFunctionPointer(entry,typeof(Query));
                var init=Function<Simple>(0x150E828);unload=Function<Simple>(0xD22BDD7E);
                Check(init(),"Initialize");initialized=true;
                enumerate=Function<EnumGpu>(0xE5AC921F);bus=Function<ReadUint>(0x1BE0B8E5);slot=Function<ReadUint>(0x2A0A350F);
                pci=Function<PciIds>(0x2DDFB66E);pstate=Function<ReadUint>(0x927DA4F6);
                utilization=Function<DynamicInfo>(0x60DED2ED);limit=Function<SetLimit>(0xFDFC7D49);
                InitializeNvml();
            } catch {Dispose();throw;}
        }
        T Function<T>(uint id) where T:class
        {
            IntPtr address=query(id);
            if(address==IntPtr.Zero) throw new IOException("当前 NVIDIA 驱动不支持省电接口 0x"+id.ToString("X8"));
            return Marshal.GetDelegateForFunctionPointer(address,typeof(T)) as T;
        }
        static void Check(int result,string action)
        { if(result!=0) throw new IOException("NVAPI "+action+" 失败："+result); }
        IntPtr Target(out GpuDevice device)
        {
            device=NativePnp.Unique();
            if(device.Problem!=0) throw new IOException("等待显卡恢复：PnP Code "+device.Problem);
            IntPtr[] handles=new IntPtr[64];uint count;Check(enumerate(handles,out count),"EnumPhysicalGPUs");
            if(count>64) throw new IOException("无效 NVAPI GPU 数量。");
            IntPtr match=IntPtr.Zero;int matches=0;
            for(int i=0;i<count;i++) {
                uint id,sub,rev,external,busId,slotId;
                if(pci(handles[i],out id,out sub,out rev,out external)!=0 || id!=0x220D10DE) continue;
                Check(bus(handles[i],out busId),"GetBusId");Check(slot(handles[i],out slotId),"GetBusSlotId");
                if(busId==(device.BusAddress>>8) && slotId==((device.BusAddress>>3)&31) && (device.BusAddress&7)==0) {match=handles[i];matches++;}
            }
            if(matches!=1) throw new IOException("NVAPI 与 PnP 的唯一 90HX 身份/BDF 不匹配。");
            return match;
        }
        public PowerSample Read()
        {
            GpuDevice device;IntPtr handle=Target(out device);
            byte[] data=new byte[72];Array.Copy(BitConverter.GetBytes(0x10048u),data,4);
            Check(utilization(handle,data),"GetDynamicPstatesInfoEx");
            if((BitConverter.ToUInt32(data,8)&1)==0) throw new IOException("驱动未提供 GPU 利用率，暂停省电限制。");
            uint gpu=BitConverter.ToUInt32(data,12),video=(BitConverter.ToUInt32(data,24)&1)!=0?BitConverter.ToUInt32(data,28):0,state;
            if(gpu>100 || video>100) throw new IOException("驱动返回无效利用率。");
            Check(pstate(handle,out state),"GetCurrentPstate");
            if(state>15) throw new IOException("驱动返回未知 P-state。");
            lastInstance=device.InstanceId;
            return new PowerSample {Pstate=(int)state,Gpu=(int)gpu,Video=(int)video,InstanceId=device.InstanceId,Watts=Watts(device)};
        }
        public void Limit(int value)
        {
            if(value!=0 && value!=8) throw new ArgumentOutOfRangeException("value");
            // Re-enumerate on every write: disable/enable and resume invalidate old handles.
            GpuDevice device;IntPtr handle=Target(out device);
            if(lastInstance!=null && lastInstance!=device.InstanceId) throw new IOException("省电目标显卡已改变。");
            if(NativePnp.Problem(device.InstanceId)!=0) throw new IOException("显卡状态改变，拒绝设置 P-state 限制。");
            // Type 3 covers the soft/hard client limits. P0 removes our P8 ceiling;
            // it permits the driver's dynamic policy and does not force full clocks.
            Check(limit(handle,3,(uint)value),"SetPstateClientLimits");
        }
        void InitializeNvml()
        {
            nvml=LoadLibraryEx("nvml.dll",IntPtr.Zero,0x800);if(nvml==IntPtr.Zero) return;
            try {
                var init=Export<Simple>("nvmlInit_v2");nvmlShutdown=Export<Simple>("nvmlShutdown");
                nvmlHandle=Export<NvmlHandle>("nvmlDeviceGetHandleByPciBusId_v2");powerUsage=Export<ReadUint>("nvmlDeviceGetPowerUsage");
                nvmlReady=init()==0;
            } catch(IOException) {nvmlReady=false;}
        }
        T Export<T>(string name) where T:class
        {
            IntPtr address=GetProcAddress(nvml,name);if(address==IntPtr.Zero) throw new IOException("NVML export unavailable.");
            return Marshal.GetDelegateForFunctionPointer(address,typeof(T)) as T;
        }
        double? Watts(GpuDevice device)
        {
            if(!nvmlReady) return null;
            IntPtr handle;uint milliwatts;
            if(nvmlHandle("00000000:"+device.Bdf,out handle)!=0 || powerUsage(handle,out milliwatts)!=0) return null;
            return Math.Round(milliwatts/1000.0,2);
        }
        public void Dispose()
        {
            if(nvmlReady && nvmlShutdown!=null) nvmlShutdown();nvmlReady=false;
            if(nvml!=IntPtr.Zero) {FreeLibrary(nvml);nvml=IntPtr.Zero;}
            if(initialized && unload!=null) unload();initialized=false;
            if(module!=IntPtr.Zero) {FreeLibrary(module);module=IntPtr.Zero;}
        }
    }
}
