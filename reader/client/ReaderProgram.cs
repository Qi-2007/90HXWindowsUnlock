using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

internal static class ReaderProgram {
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool DeviceIoControl(SafeFileHandle handle,uint code,byte[] input,int inputSize,byte[] output,int outputSize,out int returned,IntPtr overlapped);
    static uint Word(byte[] data,int offset){return BitConverter.ToUInt32(data,offset);}
    static uint Parse(string value) {
        string[] parts=value.Split(':','.');if(parts.Length==4){if(parts[0]!="0000")throw new FormatException("Only PCI segment 0 is supported.");parts=new[]{parts[1],parts[2],parts[3]};}
        if(parts.Length!=3)throw new FormatException("BDF must be 02:00.0.");
        uint bus=Convert.ToUInt32(parts[0],16),dev=Convert.ToUInt32(parts[1],16),fn=Convert.ToUInt32(parts[2],16);
        if(bus>255 || dev>31 || fn>7)throw new FormatException("Invalid BDF.");return bus<<8|dev<<3|fn;
    }
    static int Main(string[] args) {
        try {
            if(args.Length==1 && args[0]=="--help"){Console.WriteLine("CMP90HXReader.exe --bdf 02:00.0 --out snapshot.json (fixed read-only snapshot; no DMA)");return 0;}
            if(args.Length!=4 || args[0]!="--bdf" || args[2]!="--out")throw new ArgumentException("Expected --bdf BDF --out FILE.");
            uint bdf=Parse(args[1]);byte[] output=new byte[64];
            using(var handle=CreateFile(@"\\.\CMP90HXReader",0xc0000000,0,IntPtr.Zero,3,0,IntPtr.Zero)) {
                if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error(),"Open CMP90HXReader: signed driver and matching newly started client required.");
                int bytes;if(!DeviceIoControl(handle,0x8338e000,BitConverter.GetBytes(bdf),4,output,output.Length,out bytes,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error(),"Read current unlock status");
                if(bytes!=64 || Word(output,0)!=1 || Word(output,4)!=bdf || (Word(output,12)&1)==0)throw new IOException("Reader protocol/target mismatch.");
            }
            Func<int,object> reg=offset=>new {Offset=(uint)offset,Value=Word(output,offset==0x82381c?36:offset==0x823820?40:44),Error=(string)null};
            var registers=new System.Collections.Generic.List<object>{reg(0x82381c),reg(0x823820),reg(0x823830)};
            if((Word(output,12)&2)!=0) {
                registers.Add(new {Offset=0x823808u,Value=Word(output,56),Error=(string)null});
                registers.Add(new {Offset=0x823814u,Value=Word(output,60),Error=(string)null});
            }
            var snapshot=new {Schema=1,DeviceId=0x220d10deu,GpuBdf=bdf,BridgeBdf=Word(output,8),TimestampUtc=DateTime.UtcNow.ToString("o"),Bar0=BitConverter.ToUInt64(output,16),Boot0=Word(output,24),Gpu=new{Status=Word(output,28)},Bridge=new{Status=Word(output,32)},Registers=registers};
            string file=Path.GetFullPath(args[3]);Directory.CreateDirectory(Path.GetDirectoryName(file));File.WriteAllText(file,new JavaScriptSerializer().Serialize(snapshot));
            Console.WriteLine("UNLOCK_STATUS_CAPTURED provider=CMP90HXReader CPU-MMIO no-DMA");return 0;
        } catch(Exception error){Console.Error.WriteLine("READER_FAILED: "+error);return 1;}
    }
}
