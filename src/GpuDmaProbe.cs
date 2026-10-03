using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace CMP90HX
{
    // Diagnostic only: a stopped GSP Falcon, one non-secure 256-byte DMEM
    // block. No CPU start, engine reset, BCR switch, PLM or fuse writes.
    sealed class GpuDmaProbe
    {
        internal const uint Gsp=0x110000;
        readonly IHardware io;
        readonly Snapshot gpu;
        readonly Func<ulong,ulong,ulong> alloc;
        readonly Action<ulong,ulong,ulong> free;
        readonly Action<string> log;
        readonly HashSet<uint> changed=new HashSet<uint>();
        readonly uint[] savedOffsets={0x10c,0x110,0x114,0x11c,0x128,0x600,0x624,0x1c0};
        internal string Stage="GSP_DMA_PREFLIGHT";
        internal GpuDmaProbe(IHardware hardware,Snapshot device,Func<ulong,ulong,ulong> allocate,
            Action<ulong,ulong,ulong> release,Action<string> logger)
        { io=hardware; gpu=device; alloc=allocate; free=release; log=logger; }
        internal static bool Invalid(uint value)
        { return value==0xffffffff || (value&0xffff0000)==0xbadf0000 ||
            value==0xdead5ec1 || value==0xdead5ec2 || value==0xdeaddead; }
        uint R(uint offset) { return io.ReadMmio(gpu.Bar0+Gsp+offset); }
        void W(uint offset,uint value) {
            if(Array.IndexOf(savedOffsets,offset)>=0) changed.Add(offset);
            io.WriteMmio(gpu.Bar0+Gsp+offset,value);
        }
        void Live()
        {
            if (io.ReadPci(gpu.GpuBdf,0,4)!=Gen2Engine.TargetId || io.ReadMmio(gpu.Bar0)!=gpu.Boot0 ||
                (io.ReadPci(gpu.GpuBdf,4,2)&6)!=6)
                throw new IOException("GPU_DMA_DEVICE_OR_BUS_MASTER_LOST");
        }
        void WriteChecked(uint offset,uint value)
        {
            Live(); W(offset,value); uint post=R(offset);
            if (post!=value) throw new IOException(String.Format("GSP_DMA_SETUP_WRITE_BLOCKED offset=0x{0:x} want=0x{1:x8} got=0x{2:x8}",offset,value,post));
        }
        uint[] ReadDmem()
        {
            W(0x1c0,1U<<25); uint[] data=new uint[64];
            for (int i=0;i<data.Length;i++) data[i]=R(0x1c4);
            return data;
        }
        void WriteDmem(uint[] data)
        { W(0x1c0,1U<<24); foreach (uint value in data) W(0x1c4,value); }
        static bool Same(uint[] a,uint[] b)
        { for (int i=0;i<a.Length;i++) if(a[i]!=b[i]) return false; return true; }
        bool Idle()
        { uint cmd=R(0x118); return !Invalid(cmd) && (cmd&3)==2; }
        void Diagnostic(string phase)
        {
            // Read-only raw samples. Some generation-specific error/PLM
            // registers may be unimplemented; never clear or decode them as
            // proof of a particular fault without the matching hardware map.
            foreach(uint offset in new uint[]{0x008,0x10c,0x118,0x168,0x16c,0x240,0x244,0x248,0x24c,
                0x25c,0x284,0x288,0x28c,0x2e0,0x2e4,0x2e8,0x600,0x624}) {
                try { log(String.Format("GSP_DMA_RAW phase={0} +0x{1:x}=0x{2:x8}",phase,offset,R(offset))); }
                catch(Exception error) { log("GSP_DMA_RAW phase="+phase+" +0x"+offset.ToString("x")+" unavailable: "+error.Message); }
            }
            try {
                log(String.Format("GPU_DMA_PCI phase={0} GPU_CMD=0x{1:x4} GPU_STATUS=0x{2:x4} RP_CMD=0x{3:x4} RP_STATUS=0x{4:x4} RP_SECONDARY_STATUS=0x{5:x4}",
                    phase,io.ReadPci(gpu.GpuBdf,4,2),io.ReadPci(gpu.GpuBdf,6,2),
                    io.ReadPci(gpu.BridgeBdf,4,2),io.ReadPci(gpu.BridgeBdf,6,2),io.ReadPci(gpu.BridgeBdf,0x1e,2)));
            } catch(Exception error) { log("GPU_DMA_PCI diagnostic unavailable: "+error.Message); }
        }
        bool Compare(uint[] actual,uint[] expected,uint[] original,string direction)
        {
            int matches=0,unchanged=0,first=-1;
            for(int i=0;i<64;i++) {
                if(actual[i]==expected[i]) matches++; else if(first<0) first=i;
                if(original!=null && actual[i]==original[i]) unchanged++;
            }
            log(String.Format("GPU_DMA_DATA direction={0} matches={1}/64 unchanged={2}/64 firstMismatch={3}",direction,matches,unchanged,first));
            for(int i=0;i<8;i++) log(String.Format("GPU_DMA_WORD direction={0} index={1} expected=0x{2:x8} actual=0x{3:x8}",direction,i,expected[i],actual[i]));
            if(first>=8) log(String.Format("GPU_DMA_WORD direction={0} index={1} expected=0x{2:x8} actual=0x{3:x8}",direction,first,expected[first],actual[first]));
            return matches==64;
        }
        void WaitDma(uint requested)
        {
            Stopwatch timer=Stopwatch.StartNew();
            uint first=0,last=0; int polls=0; bool busy=false;
            try {
            do {
                uint cmd=R(0x118);
                if(polls++==0) first=cmd; last=cmd;
                if (Invalid(cmd)) throw new IOException("GSP_DMA_COMMAND_INACCESSIBLE: 0x"+cmd.ToString("x8"));
                if ((cmd&0x100000)!=0) throw new IOException("GSP_DMA_COMMAND_ERROR: 0x"+cmd.ToString("x8"));
                if ((cmd&3)==2) return;
                busy=true;
                io.Delay(1);
            } while(timer.ElapsedMilliseconds<1000);
            throw new IOException("GSP_DMA_TIMEOUT: no IDLE&&!FULL within 1 second.");
            } finally {
                log(String.Format("GPU_DMA_COMMAND requested=0x{0:x8} first=0x{1:x8} last=0x{2:x8} polls={3} busyObserved={4} elapsedMs={5}; status alone is not payload verification",
                    requested,first,last,polls,busy,timer.ElapsedMilliseconds));
            }
        }
        void Transfer(ulong physical,bool toSysmem,ref bool pending)
        {
            if ((physical&255)!=0 || physical>=0x100000000UL) throw new IOException("Invalid probe physical DMA address.");
            uint cpu=R(0x100), bcr=R(0x1668);
            if (Invalid(cpu) || (cpu&0x30)==0 || Invalid(bcr) || (bcr&0x11)!=1)
                throw new IOException("GSP_DMA_ENGINE_STATE_CHANGED");
            WriteChecked(0x110,(uint)(physical>>8)); WriteChecked(0x128,0);
            WriteChecked(0x114,0); WriteChecked(0x11c,0); // DMEM[0], one 256-byte block.
            uint requested=toSysmem?0x620U:0x600U;
            log(String.Format("GPU_DMA_TRANSFER direction={0} physical=0x{1:x} BASE=0x{2:x8} BASE1=0x{3:x8} MOFFS=0x{4:x8} FBOFFS=0x{5:x8} TRANSCFG0=0x{6:x8} DMACTL=0x{7:x8} FBIF_CTL=0x{8:x8}",
                toSysmem?"OUT":"IN",physical,R(0x110),R(0x128),R(0x114),R(0x11c),R(0x600),R(0x10c),R(0x624)));
            pending=true; W(0x118,requested); WaitDma(requested); pending=false;
        }
        internal void Run()
        {
            Live();
            uint bridgeCommand=io.ReadPci(gpu.BridgeBdf,4,2);
            log(String.Format("GPU_DMA_UPSTREAM_GATE RP={0} COMMAND=0x{1:x4} BME={2}",Gen2Engine.Bdf(gpu.BridgeBdf),bridgeCommand,(bridgeCommand&4)!=0));
            if((bridgeCommand&4)==0) throw new IOException("GPU_DMA_UPSTREAM_BME_DISABLED: root port cannot forward upstream memory requests; no bridge write attempted.");
            foreach(uint offset in new uint[]{0x840100,0x840240,0x840284,0x840288,0x84028c,0x840190}) {
                try { log(String.Format("SEC2_ACCESS +0x{0:x} = 0x{1:x8}",offset,io.ReadMmio(gpu.Bar0+offset))); }
                catch(Exception error) { log("SEC2_ACCESS +0x"+offset.ToString("x")+" read failed: "+error.Message); }
            }
            // Read all gates before even selecting a DMEM port.
            uint cpu=R(0x100), bcr=R(0x1668), ctl=R(0x10c), cmd=R(0x118), dmemPort=R(0x1c0);
            log(String.Format("GSP_DMA_STATE CPUCTL=0x{0:x8} BCR=0x{1:x8} DMACTL=0x{2:x8} DMATRFCMD=0x{3:x8} DMEMC=0x{4:x8}",cpu,bcr,ctl,cmd,dmemPort));
            if (Invalid(cpu) || Invalid(bcr) || Invalid(ctl) || Invalid(cmd) || Invalid(dmemPort))
                throw new IOException("GSP_DMA_PROBE_UNAVAILABLE: engine/DMEM control not host-readable; GPU DMA not tested.");
            if ((cpu&0x30)==0 || (bcr&0x11)!=1)
                throw new IOException("GSP_DMA_PROBE_UNAVAILABLE: stopped Falcon mode required; no reset/switch attempted.");
            if ((ctl&6)!=0 || (cmd&3)!=2)
                throw new IOException("GSP_DMA_PROBE_UNAVAILABLE: scrubbing or DMA engine not idle.");
            Dictionary<uint,uint> saved=new Dictionary<uint,uint>();
            foreach(uint offset in savedOffsets) {
                uint value=R(offset);
                if (Invalid(value)) throw new IOException("GSP_DMA_PROBE_UNAVAILABLE: register unreadable +0x"+offset.ToString("x"));
                saved.Add(offset,value);
            }
            uint[] original=null, pattern=new uint[64];
            ulong source=0,destination=0,srcPhysical=0,dstPhysical=0;
            bool modifiedDmem=false,pending=false,restored=true;
            Exception failure=null;
            IntPtr physicalOut=Marshal.AllocHGlobal(8);
            try {
                Stage="GSP_DMEM_PIO_GATE";
                original=ReadDmem();
                for(int i=0;i<8;i++) log(String.Format("GSP_DMEM_INITIAL index={0} value=0x{1:x8}",i,original[i]));
                foreach(uint word in original) if (Invalid(word))
                    throw new IOException("GSP_DMEM_NOT_READABLE: protected/invalid DMEM data; GPU DMA not tested.");
                // Every source word differs from saved DMEM, so a dropped
                // inbound DMA cannot pass by coinciding with existing data.
                for(int i=0;i<64;i++) pattern[i]=original[i]^0x9058a5a5U^((uint)i*0x10203U);
                modifiedDmem=true; WriteDmem(pattern);
                if (!Same(ReadDmem(),pattern)) throw new IOException("GSP_DMEM_HOST_WRITE_BLOCKED: PIO readback mismatch; GPU DMA not tested.");
                WriteDmem(original);
                if(!Same(ReadDmem(),original)) throw new IOException("GSP_DMEM_PIO_RESTORE_BLOCKED: cannot establish distinct initial DMEM for DMA test.");
                log("GSP_DMEM_PIO_VERIFIED: host read/write gate passed; not a DMA success claim.");
                Stage="GPU_DMA_ARENA_BUFFER_CHECK";
                source=alloc(4096,(ulong)physicalOut.ToInt64()); srcPhysical=unchecked((ulong)Marshal.ReadInt64(physicalOut));
                if(source==0) throw new IOException("Probe source allocation failed.");
                destination=alloc(4096,(ulong)physicalOut.ToInt64()); dstPhysical=unchecked((ulong)Marshal.ReadInt64(physicalOut));
                if(destination==0) throw new IOException("Probe destination allocation failed.");
                for (int i=0;i<64;i++) {
                    Marshal.WriteInt32(new IntPtr((long)source),i*4,unchecked((int)pattern[i]));
                    Marshal.WriteInt32(new IntPtr((long)destination),i*4,unchecked((int)~pattern[i]));
                }
                Thread.MemoryBarrier();
                for (int i=0;i<64;i++)
                    if (io.ReadMmio(srcPhysical+(uint)i*4)!=pattern[i] || io.ReadMmio(dstPhysical+(uint)i*4)!=~pattern[i])
                        throw new IOException("PROBE_CPU_PHYSICAL_BUFFER_MISMATCH");
                Stage="GSP_DMA_SETUP";
                log(String.Format("GPU_DMA_BUFFERS sourceVA=0x{0:x} sourcePhysical=0x{1:x} destinationVA=0x{2:x} destinationPhysical=0x{3:x}",source,srcPhysical,destination,dstPhysical));
                WriteChecked(0x10c,saved[0x10c]&~1U);
                WriteChecked(0x624,saved[0x624]|0x80);
                WriteChecked(0x600,(saved[0x600]&~0x10007U)|5); // ctx0 coherent physical sysmem.
                Diagnostic("BEFORE");
                Stage="GPU_DMA_SYSMEM_TO_GSP"; log("STAGE="+Stage+" physical=0x"+srcPhysical.ToString("x"));
                Transfer(srcPhysical,false,ref pending);
                Diagnostic("AFTER_IN");
                bool inbound=Compare(ReadDmem(),pattern,original,"IN");
                // An idle inbound with wrong data must remain a failure. But
                // independently seed DMEM via already-verified PIO so outbound
                // can still diagnose write-vs-read access, using only our own
                // reserved destination. No retry, reset or alternate context.
                if(!inbound) {
                    log("GPU_DMA_INBOUND_FAILED: testing independent outbound with a PIO-seeded pattern; this cannot convert inbound failure into success.");
                    WriteDmem(pattern);
                    if(!Same(ReadDmem(),pattern)) throw new IOException("GSP_DMEM_HOST_WRITE_BLOCKED during independent outbound setup.");
                }
                Stage="GPU_DMA_GSP_TO_SYSMEM"; log("STAGE="+Stage+" physical=0x"+dstPhysical.ToString("x"));
                Transfer(dstPhysical,true,ref pending); Thread.MemoryBarrier();
                Diagnostic("AFTER_OUT");
                uint[] mapped=new uint[64],physicalData=new uint[64],sentinel=new uint[64];
                for(int i=0;i<64;i++) {
                    mapped[i]=unchecked((uint)Marshal.ReadInt32(new IntPtr((long)destination),i*4));
                    physicalData[i]=io.ReadMmio(dstPhysical+(uint)i*4); sentinel[i]=~pattern[i];
                }
                bool mappedOk=Compare(mapped,pattern,sentinel,"OUT_CPU");
                bool physicalOk=Compare(physicalData,pattern,sentinel,"OUT_PHYSICAL");
                bool outbound=mappedOk && physicalOk;
                log("GPU_DMA_DIRECTION_RESULT IN="+(inbound?"PASS":"FAIL")+" OUT="+(outbound?"PASS":"FAIL"));
                if(!inbound) {
                    Stage="GPU_DMA_SYSMEM_TO_GSP";
                    throw new IOException("GPU_DMA_SYSMEM_TO_GSP_MISMATCH: source pattern not received; independent outbound "+(outbound?"passed":"also failed")+". See GPU_DMA_DATA/COMMAND/RAW.");
                }
                if(!outbound) throw new IOException("GPU_DMA_GSP_TO_SYSMEM_MISMATCH: destination was not changed to the DMEM pattern.");
            } catch(Exception error) { failure=error; log("GPU_DMA_PROBE_FAILED_STAGE="+Stage+"; "+error.Message); }
            finally {
                // A pending DMA must not be redirected by restoring address
                // registers. Arena physical RAM remains reserved until reboot.
                try {
                    if (pending && !Idle()) {
                        restored=false; log("GPU_DMA_NOT_QUIESCENT: no register/DMEM restoration or buffer reuse attempted; keep GPU disabled and cold boot.");
                    } else {
                        pending=false;
                        if(modifiedDmem && original!=null) {
                            WriteDmem(original);
                            if(!Same(ReadDmem(),original)) throw new IOException("Probe DMEM restore readback failed.");
                        }
                        for(int i=savedOffsets.Length-1;i>=0;i--)
                            if(changed.Contains(savedOffsets[i])) WriteChecked(savedOffsets[i],saved[savedOffsets[i]]);
                        log(modifiedDmem ? "GPU_DMA_PROBE_STATE_RESTORED: DMEM and config restored; DMATRFCMD not replayed." :
                            "GPU_DMA_PROBE_STATE_RESTORED: modified config/DMEM port restored; DMEM payload never written; DMATRFCMD not replayed.");
                    }
                } catch(Exception error) { restored=false; log("GPU_DMA_RESTORE_FAILED: "+error.Message); }
                if(!pending) {
                    if(destination!=0) free(destination,4096,dstPhysical);
                    if(source!=0) free(source,4096,srcPhysical);
                }
                Marshal.FreeHGlobal(physicalOut);
            }
            if(!restored) throw new IOException("GPU_DMA_RESTORE_UNCONFIRMED: keep GPU disabled and cold boot.",failure);
            if(failure!=null) throw failure;
            Stage="GPU_DMA_ROUNDTRIP_VERIFIED";
            log("GPU_DMA_ROUNDTRIP_VERIFIED: GSP DMEM 256 bytes both directions, full CPU/physical comparison; CPU never started; not SEC2/firmware/Gen2 verification.");
        }
    }
}
