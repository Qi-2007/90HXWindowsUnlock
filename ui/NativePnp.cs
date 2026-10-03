using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CMP90HX.Control
{
    internal sealed class GpuDevice
    {
        internal string InstanceId, Name, Bdf;
        internal uint Node, Problem, BusAddress;
    }

    internal static class NativePnp
    {
        [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode)] static extern uint CM_Get_Device_ID_List_SizeW(out uint size, string filter, uint flags);
        [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode)] static extern uint CM_Get_Device_ID_ListW(string filter, [Out] char[] ids, uint size, uint flags);
        [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode)] static extern uint CM_Locate_DevNodeW(out uint node, string id, uint flags);
        [DllImport("cfgmgr32.dll")] static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
        [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode)] static extern uint CM_Get_DevNode_Registry_PropertyW(uint node, uint property, out uint type, [Out] byte[] buffer, ref uint size, uint flags);
        [DllImport("cfgmgr32.dll")] static extern uint CM_Disable_DevNode(uint node, uint flags);
        [DllImport("cfgmgr32.dll")] static extern uint CM_Enable_DevNode(uint node, uint flags);

        static void Check(uint code, string operation)
        {
            if (code != 0) throw new IOException(operation + " failed: CONFIGRET=0x" + code.ToString("x"));
        }
        static byte[] Property(uint node, uint property)
        {
            byte[] buffer=new byte[2048]; uint size=(uint)buffer.Length, type;
            Check(CM_Get_DevNode_Registry_PropertyW(node,property,out type,buffer,ref size,0), "Read PnP property");
            Array.Resize(ref buffer,(int)size); return buffer;
        }
        internal static List<GpuDevice> Enumerate()
        {
            uint size; char[] ids=null;
            for(int attempt=0;attempt<3;attempt++) {
                Check(CM_Get_Device_ID_List_SizeW(out size,null,0),"List PnP size");
                if(size>1024*1024) throw new IOException("PnP list exceeds bound.");
                ids=new char[size]; uint result=CM_Get_Device_ID_ListW(null,ids,size,0);
                if(result==0) break;
                if(result!=0x1a || attempt==2) Check(result,"List PnP devices");
            }
            List<GpuDevice> resultDevices=new List<GpuDevice>();
            foreach(string id in new string(ids).Split(new[]{'\0'},StringSplitOptions.RemoveEmptyEntries)) {
                if(!id.StartsWith(@"PCI\VEN_10DE&DEV_220D&",StringComparison.OrdinalIgnoreCase)) continue;
                uint node, status, problem;
                if(CM_Locate_DevNodeW(out node,id,0)!=0 || CM_Get_DevNode_Status(out status,out problem,node,0)!=0) continue;
                if(problem!=0 && problem!=22 && problem!=43) continue;
                uint bus=BitConverter.ToUInt32(Property(node,0x16),0), address=BitConverter.ToUInt32(Property(node,0x1d),0);
                uint dev=address>>16, fn=address&65535;
                if(bus>255 || dev>31 || fn>7) throw new IOException("Unsupported segment-0 PnP BDF.");
                string name="NVIDIA CMP 90HX";
                try { name=Encoding.Unicode.GetString(Property(node,13)).TrimEnd('\0'); } catch(IOException) { }
                resultDevices.Add(new GpuDevice { InstanceId=id,Node=node,Problem=problem,Name=name,
                    BusAddress=(bus<<8)|(dev<<3)|fn,Bdf=bus.ToString("x2")+":"+dev.ToString("x2")+"."+fn });
            }
            return resultDevices;
        }
        internal static GpuDevice Unique()
        {
            var devices=Enumerate();
            if(devices.Count!=1) throw new IOException("Expected exactly one active/disabled 90HX; found "+devices.Count+".");
            return devices[0];
        }
        internal static uint Problem(string id)
        {
            uint node,status,problem;
            Check(CM_Locate_DevNodeW(out node,id,0),"Locate target 90HX");
            Check(CM_Get_DevNode_Status(out status,out problem,node,0),"Read target PnP status");
            return problem;
        }
        internal static void Enable(string id,bool enabled)
        {
            // Re-resolve the exact instance after each devnode transition.
            uint node; Check(CM_Locate_DevNodeW(out node,id,0),"Locate target 90HX");
            Check(enabled ? CM_Enable_DevNode(node,0) : CM_Disable_DevNode(node,0x0a),enabled?"Enable 90HX":"Disable 90HX");
        }
        internal static void WaitDisabled(string id)
        {
            for(int i=0;i<30;i++) { if(Problem(id)==22) return; Thread.Sleep(200); }
            throw new IOException("90HX did not reach Code 22; refusing full unlock.");
        }
    }
}
