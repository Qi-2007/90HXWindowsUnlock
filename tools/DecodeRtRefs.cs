// Offline analysis only. Requires Iced 1.21.0 (MIT), kept under logs/deps.
using System;
using System.IO;
using System.Collections.Generic;
using Iced.Intel;
public static class DecodeRtRefs {
    public static int Main(string[] args) {
        if(args.Length<2) {Console.Error.WriteLine("DecodeRtRefs binary hex-value[,hex-value...] [writes-only|immediates]");return 1;}
        bool immediates=args.Length>2 && args[2]=="immediates";
        byte[] bytes=File.ReadAllBytes(args[0]);int pe=BitConverter.ToInt32(bytes,0x3c);
        ulong image=BitConverter.ToUInt64(bytes,pe+48);
        int count=BitConverter.ToUInt16(bytes,pe+6),header=pe+24+BitConverter.ToUInt16(bytes,pe+20);
        var wanted=new HashSet<ulong>();foreach(string value in args[1].Split(',')) wanted.Add(Convert.ToUInt64(value,16));
        var regions=new List<uint[]>();
        for(int s=0;s<count;s++) {
            int h=header+40*s;string name=System.Text.Encoding.ASCII.GetString(bytes,h,8).TrimEnd('\0');
            if(name!=".pdata") continue;
            uint raw=BitConverter.ToUInt32(bytes,h+20),size=BitConverter.ToUInt32(bytes,h+16);
            for(long p=raw;p+12<=Math.Min(bytes.Length,(long)raw+size);p+=12) {
                uint begin=BitConverter.ToUInt32(bytes,(int)p),end=BitConverter.ToUInt32(bytes,(int)p+4);
                if(begin>0 && end>begin) regions.Add(new uint[]{begin,end});
            }
        }
        var infoFactory=new InstructionInfoFactory();var formatter=new IntelFormatter();
        var output=new StringOutput();int matches=0;
        Console.WriteLine("RVA\tSection\tRegionBegin\tRegionEnd\tAccess\tInstruction");
        for(int s=0;s<count;s++) {
            int h=header+40*s;uint flags=BitConverter.ToUInt32(bytes,h+36);
            if((flags&0x20000000)==0) continue;
            string name=System.Text.Encoding.ASCII.GetString(bytes,h,8).TrimEnd('\0');
            uint rva=BitConverter.ToUInt32(bytes,h+12),raw=BitConverter.ToUInt32(bytes,h+20),size=BitConverter.ToUInt32(bytes,h+16);
            var reader=new ByteArrayCodeReader(bytes,(int)raw,(int)Math.Min(size,bytes.Length-raw));
            var decoder=Decoder.Create(64,reader,image+rva);
            while(reader.CanReadByte) {
                Instruction inst=decoder.Decode();
                if(inst.Code==Code.INVALID) continue;
                bool immediateMatch=false;
                if(immediates) {
                    for(int op=0;op<inst.OpCount;op++) {
                        OpKind kind=inst.GetOpKind(op);
                        if(kind>=OpKind.Immediate8 && kind<=OpKind.Immediate32to64 && wanted.Contains(inst.GetImmediate(op))) immediateMatch=true;
                    }
                    if(!immediateMatch) continue;
                } else if(inst.IsIPRelativeMemoryOperand || !wanted.Contains(inst.MemoryDisplacement64)) continue;
                InstructionInfo info=infoFactory.GetInfo(in inst);string access="";bool write=false;
                for(int op=0;op<inst.OpCount;op++) if(inst.GetOpKind(op)==OpKind.Memory) {
                    OpAccess a=info.GetOpAccess(op);access=a.ToString();
                    write=a==OpAccess.Write || a==OpAccess.ReadWrite || a==OpAccess.CondWrite || a==OpAccess.ReadCondWrite;
                }
                if(immediates) access="Immediate";
                if(access=="" || (args.Length>2 && args[2]=="writes-only" && !write)) continue;
                uint address=(uint)(inst.IP-image),begin=0,end=0;
                foreach(uint[] region in regions) if(address>=region[0] && address<region[1]) {begin=region[0];end=region[1];break;}
                output.Reset();formatter.Format(in inst,output);
                Console.WriteLine("0x{0:x}\t{1}\t0x{2:x}\t0x{3:x}\t{4}\t{5}",address,name,begin,end,access,output.ToString());matches++;
            }
        }
        Console.Error.WriteLine("Decoded operand candidates="+matches+"; linear decode may include embedded data or lose alignment; validate each candidate from a known function boundary.");
        return 0;
    }
}
