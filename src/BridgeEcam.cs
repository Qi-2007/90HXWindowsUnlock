using System;
using System.IO;

namespace CMP90HX
{
    // Full-unlock only. HAL rejects Type-1 config writes; ECAM is limited to
    // the discovered upstream bridge and three control words, not arbitrary PCI.
    sealed class BridgeEcam
    {
        readonly IHardware io;
        readonly uint bdf, cap;
        readonly ulong physical;
        readonly Action<string> log;
        internal static byte[] ReadMcfg()
        {
            const uint acpi=0x41435049, mcfg=0x4746434d;
            uint bytes=Native.GetSystemFirmwareTable(acpi,mcfg,null,0);
            if (bytes==0) throw Native.Failure(System.Runtime.InteropServices.Marshal.GetLastWin32Error(),"Read ACPI MCFG size");
            if (bytes<60 || bytes>44+16*256) throw new IOException("Unexpected ACPI MCFG size.");
            byte[] table=new byte[checked((int)bytes)];
            if (Native.GetSystemFirmwareTable(acpi,mcfg,table,bytes)!=bytes)
                throw new IOException("ACPI MCFG read failed or changed size.");
            return table;
        }
        internal static ulong Resolve(byte[] table,uint selectedBdf)
        {
            if (selectedBdf>0xffff || table==null || table.Length<60 || table.Length>44+16*256 ||
                BitConverter.ToUInt32(table,0)!=0x4746434d || BitConverter.ToUInt32(table,4)!=table.Length ||
                (table.Length-44)%16!=0) throw new IOException("Invalid ACPI MCFG header/length.");
            uint sum=0; foreach (byte v in table) sum+=v;
            if ((sum&255)!=0) throw new IOException("ACPI MCFG checksum failed.");
            uint bus=selectedBdf>>8; int matches=0; ulong result=0;
            for (int i=44;i<table.Length;i+=16) {
                ulong start=BitConverter.ToUInt64(table,i);
                uint segment=BitConverter.ToUInt16(table,i+8), first=table[i+10], last=table[i+11];
                if (first>last || start==0 || (start&0xfffff)!=0 || start>0x000fffffffffffffUL-0x10000000UL)
                    throw new IOException("Invalid ACPI MCFG allocation.");
                if (segment!=0 || bus<first || bus>last) continue;
                // MCFG base corresponds to bus zero, NOT to first/start bus.
                result=checked(start+((ulong)bus<<20)+((ulong)(selectedBdf&255)<<12)); matches++;
            }
            if (matches!=1) throw new IOException("Expected exactly one segment-0 MCFG range for bridge; found "+matches);
            return result;
        }
        internal BridgeEcam(IHardware hardware,byte[] table,Snapshot device,Action<string> logger)
        {
            io=hardware; bdf=device.BridgeBdf; cap=device.Bridge.CapabilityOffset; log=logger;
            if (bdf==device.GpuBdf || cap<0x40 || cap>0xc0 || (cap&3)!=0 || !device.Bridge.HasLinkControl2)
                throw new IOException("Invalid upstream bridge for scoped ECAM transport.");
            physical=Resolve(table,bdf);
            uint id=io.ReadPci(bdf,0,4);
            if (id==0 || id==0xffffffff || (id&65535)==65535 ||
                (io.ReadPci(bdf,0x0e,1)&0x7f)!=1 || (io.ReadPci(bdf,8,4)>>16)!=0x0604)
                throw new IOException("ECAM target is not a live Type-1 PCI bridge.");
            uint buses=io.ReadPci(bdf,0x18,4), gpuBus=device.GpuBdf>>8;
            if (gpuBus<((buses>>8)&255) || gpuBus>((buses>>16)&255))
                throw new IOException("ECAM bridge no longer contains the selected GPU bus.");
            Match(0,4); Match(8,4); Match(0x0c,4); Match(0x18,4); Match(cap,4);
            Match(0x3e,2); Match(cap+0x10,2); Match(cap+0x30,2);
            log(String.Format("BRIDGE_ECAM_VERIFIED {0} physical=0x{1:x}; controls=0x3e,0x{2:x},0x{3:x}; read-only probe, no writes",
                Gen2Engine.Bdf(bdf),physical,cap+0x10,cap+0x30));
        }
        uint Read(uint offset,int size)
        {
            uint value=io.ReadMmio(physical+(offset&~3U));
            return size==4?value:(value>>checked((int)((offset&3)*8)))&65535;
        }
        void Match(uint offset,int size)
        {
            uint hal=io.ReadPci(bdf,offset,size), ecam=Read(offset,size);
            if (hal!=ecam) throw new IOException(String.Format("Bridge ECAM/HAL mismatch {0}+0x{1:x}: HAL={2:x8} ECAM={3:x8}",Gen2Engine.Bdf(bdf),offset,hal,ecam));
        }
        internal bool IsTarget(uint selectedBdf) { return selectedBdf==bdf; }
        internal static uint Compose(uint cap,uint offset,int size,uint before,uint value)
        {
            uint mask;
            if (size!=2 || value>65535) throw new IOException("Bridge ECAM accepts only control-word writes.");
            if (offset==0x3e) mask=0x40;
            else if (offset==cap+0x10) mask=0x23; // ASPM and Retrain Link only.
            else if (offset==cap+0x30) mask=15;   // Target Link Speed only.
            else throw new IOException("Bridge ECAM offset is outside the control whitelist.");
            uint old=offset==0x3e?before>>16:before&65535;
            if (((old^value)&~mask)!=0) throw new IOException("Bridge ECAM write would change non-whitelisted control bits.");
            // Bridge Control shares a DWORD with Interrupt Line/Pin: preserve
            // the low half and write zero to Discard Timer Status (bit 10),
            // reserved on PCIe but W1C on conventional bridge implementations.
            // LNKCTL/LNKCTL2 share with status (including W1C):
            // write zero to the high status half, never replay its read value.
            return offset==0x3e?(before&65535)|((value&~0x400U)<<16):value;
        }
        internal void Write(uint offset,uint value,int size)
        {
            // Compose validates offsets before any MMIO access.
            Compose(cap,offset,size,offset==0x3e?value<<16:value,value);
            uint aligned=offset&~3U, before=io.ReadMmio(physical+aligned);
            uint encoded=Compose(cap,offset,size,before,value);
            Match(offset,size);
            io.WriteMmio(physical+aligned,encoded);
            uint post=Read(offset,2), expectedMask=offset==cap+0x10?0xffdfU:0xffffU;
            log(String.Format("BRIDGE_ECAM_WRITE {0}+0x{1:x} value=0x{2:x4} readback=0x{3:x4}",Gen2Engine.Bdf(bdf),offset,value,post));
            // Retrain Link may self-clear immediately; verify its outcome in
            // Gen2Engine, not by requiring that transient bit to stay asserted.
            if ((post&expectedMask)!=(value&expectedMask)) throw new IOException("Bridge ECAM control readback mismatch.");
        }
    }
}
