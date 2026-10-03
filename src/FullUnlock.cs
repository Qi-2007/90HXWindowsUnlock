using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace CMP90HX
{
    sealed class CoreSession : IDisposable
    {
        readonly IHardware io;
        readonly Snapshot device;
        readonly Action<string> log;
        readonly Dictionary<ulong,int> hosts=new Dictionary<ulong,int>();
        internal readonly ElfCore Core;
        internal IntPtr Gpu;
        internal ulong Handoff;
        internal int HandoffCalls;
        Exception failure;
        long delayCalls, requestedDelayMs;
        long delayTicks, handoffTicks, logTicks, mmioReads, mmioWrites, promReads, copyBytes, fillBytes;
        internal ulong G { get { return (ulong)Gpu.ToInt64(); } }
        SysV.Callback Guard(Func<ulong,ulong,ulong,ulong,ulong,ulong,ulong,ulong> callback, ulong failedReturn)
        {
            return delegate(ulong a,ulong b,ulong c,ulong d,ulong e,ulong f,ulong stack) {
                if (failure!=null) return failedReturn;
                try { return callback(a,b,c,d,e,f,stack); }
                catch (Exception error) {
                    failure=error;
                    try { log("CORE CALLBACK FAILED: "+error.Message); } catch { /* Never unwind a managed exception through ELF. */ }
                    return failedReturn;
                }
            };
        }
        void MmioAddress(ulong address)
        {
            if (address<device.Bar0 || address-device.Bar0>=0x1000000 || (address&3)!=0)
                throw new IOException("Core MMIO outside selected GPU BAR0: 0x"+address.ToString("x"));
        }
        internal CoreSession(byte[] coreBytes,IHardware hardware,Snapshot snapshot,
            Func<ulong,ulong,ulong> dmaAllocate,Action<ulong,ulong,ulong> dmaFree,Func<int> reset,Action<string> logger,
            Action<ulong,uint> checkWrite=null,bool preciseDelays=false,Func<int> handoff=null)
        {
            io=hardware; device=snapshot; log=logger;
            Gpu=Marshal.AllocHGlobal(120); Marshal.Copy(new byte[120],0,Gpu,120);
            try {
                Dictionary<string,SysV.Callback> imports=new Dictionary<string,SysV.Callback>();
                imports["ioread32"]=Guard(delegate(ulong a,ulong b,ulong c,ulong d,ulong e,ulong f,ulong stack) {
                    MmioAddress(a); mmioReads++;
                    if(a-device.Bar0>=0x300000 && a-device.Bar0<0x400000) promReads++;
                    return io.ReadMmio(a);
                },UInt64.MaxValue);
                imports["iowrite32"]=Guard(delegate(ulong a,ulong b,ulong c,ulong d,ulong e,ulong f,ulong stack) {
                    MmioAddress(b); uint value=unchecked((uint)a);
                    if(checkWrite!=null) checkWrite(b,value);
                    mmioWrites++; io.WriteMmio(b,value); return 0;
                },0);
                imports["memcpy"]=Guard(delegate(ulong a,ulong b,ulong c,ulong d,ulong e,ulong f,ulong stack) {
                    copyBytes+=checked((long)c);
                    return NativeBufferMemory.Copy(a,b,c);
                },0);
                imports["memcpy_toio"]=Guard(delegate(ulong a,ulong b,ulong c,ulong d,ulong e,ulong f,ulong stack) {
                    throw new IOException("BAR1/GR-ACR is outside the Windows-safe EFI sequence and is not implemented.");
                },0);
                imports["memset"]=Guard(delegate(ulong a,ulong b,ulong c,ulong d,ulong e,ulong f,ulong stack) {
                    fillBytes+=checked((long)c);
                    return NativeBufferMemory.Fill(a,unchecked((byte)b),c);
                },0);
                Core=new ElfCore(coreBytes,imports);
                Marshal.WriteInt64(Gpu,0,checked((long)device.Bar0)); Marshal.WriteInt16(Gpu,8,0x220d);
                if(handoff!=null) Handoff=Core.Abi.Reverse(Guard(delegate(ulong a,ulong b,ulong c,ulong d,ulong e,ulong f,ulong stack) {
                    if(a!=G) throw new IOException("Handoff GPU pointer mismatch.");
                    HandoffCalls++;
                    long start=Stopwatch.GetTimestamp();
                    try { return unchecked((ulong)(long)handoff()); }
                    finally { handoffTicks+=Stopwatch.GetTimestamp()-start; }
                },UInt64.MaxValue));
                Put(40,delegate(ulong a,ulong b,ulong c,ulong d,ulong e,ulong f,ulong stack) {
                    if (b==0 || b>32UL*1024*1024) return 0;
                    IntPtr p=Marshal.AllocHGlobal((int)b);
                    ulong address=(ulong)p.ToInt64(); hosts.Add(address,(int)b);
                    NativeBufferMemory.Fill(address,0,b); return address;
                });
                Put(48,delegate(ulong a,ulong b,ulong c,ulong d,ulong e,ulong f,ulong stack) {
                    if (b==0) return 0;
                    if (!hosts.Remove(b)) throw new IOException("Core freed unknown host allocation.");
                    Marshal.FreeHGlobal(new IntPtr((long)b)); return 0;
                });
                SysV.Callback alloc=Guard(delegate(ulong a,ulong b,ulong c,ulong d,ulong e,ulong f,ulong stack) { return dmaAllocate(b,c); },0);
                ulong allocPtr=Core.Abi.Reverse(alloc);
                Marshal.WriteInt64(Gpu,56,(long)allocPtr); Marshal.WriteInt64(Gpu,72,(long)allocPtr);
                Put(64,delegate(ulong a,ulong b,ulong c,ulong d,ulong e,ulong f,ulong stack) { dmaFree(b,c,d); return 0; });
                Put(80,delegate(ulong a,ulong b,ulong c,ulong d,ulong e,ulong f,ulong stack) {
                    if (b>252 || (b&3)!=0) throw new IOException("Invalid core PCI config read.");
                    Marshal.WriteInt32(new IntPtr((long)c),unchecked((int)io.ReadPci(device.GpuBdf,(uint)b,4))); return 0;
                });
                Put(88,delegate(ulong a,ulong b,ulong c,ulong d,ulong e,ulong f,ulong stack) {
                    if (a>30000) throw new IOException("Unexpected core delay.");
                    delayCalls++; requestedDelayMs+=(long)a;
                    long start=Stopwatch.GetTimestamp();
                    try {
                        if (preciseDelays && a<=2) WindowsHardware.PreciseShortDelay((int)a);
                        else io.Delay((int)a);
                    } finally { delayTicks+=Stopwatch.GetTimestamp()-start; }
                    return 0;
                });
                Put(96,delegate(ulong a,ulong b,ulong c,ulong d,ulong e,ulong f,ulong stack) { return unchecked((ulong)(long)reset()); });
                Put(104,delegate(ulong a,ulong b,ulong c,ulong d,ulong e,ulong f,ulong stack) {
                    long start=Stopwatch.GetTimestamp();
                    try { log("CORE: "+Format(b,new ulong[]{c,d,e,f},stack).TrimEnd('\r','\n')); return 0; }
                    finally { logTicks+=Stopwatch.GetTimestamp()-start; }
                });
            } catch { Dispose(); throw; }
        }
        void Put(int offset,SysV.Callback callback)
        { Marshal.WriteInt64(Gpu,offset,(long)Core.Abi.Reverse(Guard(callback.Invoke,offset==80 || offset==96?UInt64.MaxValue:0))); }
        static string Format(ulong format,ulong[] regs,ulong overflow)
        {
            string text=Marshal.PtrToStringAnsi(new IntPtr((long)format)); int index=0;
            return Regex.Replace(text,@"%([-+ #0]*)(\d*)(?:\.\d+)?(ll|l|z|hh|h)?([diuxXscp%])",delegate(Match m) {
                char spec=m.Groups[4].Value[0]; if (spec=='%') return "%";
                if (index>=32) throw new IOException("Too many core log arguments.");
                ulong value=index<4?regs[index]:unchecked((ulong)Marshal.ReadInt64(new IntPtr((long)overflow),(index-4)*8)); index++;
                string result;
                if (spec=='s') result=value==0?"(null)":Marshal.PtrToStringAnsi(new IntPtr((long)value));
                else if (spec=='c') result=((char)(value&255)).ToString();
                else if (spec=='p') result="0x"+value.ToString("x");
                else {
                    bool wide=m.Groups[3].Value=="l" || m.Groups[3].Value=="ll" || m.Groups[3].Value=="z";
                    if (spec=='d' || spec=='i') result=(wide?unchecked((long)value):unchecked((int)value)).ToString();
                    else result=(wide?value:unchecked((uint)value)).ToString(spec=='x'?"x":spec=='X'?"X":"d");
                }
                int width=0; if (m.Groups[2].Length!=0) width=Math.Min(128,Int32.Parse(m.Groups[2].Value));
                return result.PadLeft(width,m.Groups[1].Value.Contains("0")?'0':' ');
            });
        }
        internal int Call(string name,ulong b,ulong c,ulong d,ulong e=0)
        {
            if (failure!=null) throw new IOException("Previous native callback failed.",failure);
            Stopwatch timer=Stopwatch.StartNew(); long calls=delayCalls, requested=requestedDelayMs;
            long delays=delayTicks, handoffs=handoffTicks, logs=logTicks, reads=mmioReads,
                writes=mmioWrites, prom=promReads, copied=copyBytes, filled=fillBytes;
            try {
                int result=Core.Call(name,G,b,c,d,e);
                if (failure!=null) throw new IOException("Native callback failed.",failure);
                return result;
            } finally {
                log(String.Format("CORE_TIMING name={0} elapsed_ms={1} delay_calls={2} requested_delay_ms={3}",
                    name,timer.ElapsedMilliseconds,delayCalls-calls,requestedDelayMs-requested));
                // Inclusive counters: handoff/delay/log timings are parts of
                // CORE_TIMING, not additional elapsed time. Avoid clock reads
                // per MMIO: PROM alone invokes 262144 reads on the first call.
                log(String.Format("CORE_PROFILE name={0} handoff_ms={1} delay_actual_ms={2} log_ms={3} mmio_reads={4} prom_reads={5} mmio_writes={6} copy_bytes={7} fill_bytes={8}",
                    name,Milliseconds(handoffTicks-handoffs),Milliseconds(delayTicks-delays),Milliseconds(logTicks-logs),
                    mmioReads-reads,promReads-prom,mmioWrites-writes,copyBytes-copied,fillBytes-filled));
            }
        }
        static long Milliseconds(long ticks) { return (long)(ticks*1000.0/Stopwatch.Frequency); }
        public void Dispose()
        {
            if (Core!=null) Core.Dispose();
            foreach (ulong p in hosts.Keys) Marshal.FreeHGlobal(new IntPtr((long)p)); hosts.Clear();
            if (Gpu!=IntPtr.Zero) { Marshal.FreeHGlobal(Gpu); Gpu=IntPtr.Zero; }
        }
    }
    static class EfiResetSequence
    {
        static bool LinkReady(IHardware io,uint bdf,LinkState link)
        {
            uint status=io.ReadPci(bdf,link.CapabilityOffset+0x12,2);
            return status!=0xffff && (status&15)!=0 && (status&0x3f0)!=0 && (status&0x800)==0;
        }
        static bool ConfigReady(IHardware io,Snapshot device)
        {
            return io.ReadPci(device.GpuBdf,0,4)==Gen2Engine.TargetId &&
                LinkReady(io,device.GpuBdf,device.Gpu) && LinkReady(io,device.BridgeBdf,device.Bridge);
        }
        internal static bool BootstrapReady(IHardware io,Snapshot device)
        {
            uint plm=io.ReadMmio(device.Bar0+0x118128), progress=io.ReadMmio(device.Bar0+0x118234),
                memory=io.ReadMmio(device.Bar0+0x100ce0);
            // Pinned v0.2.2 EFI bootstrap predicate: GFW_BOOT enabled,
            // GFW progress completed, and a valid nonzero memory magnitude.
            return !GpuDmaProbe.Invalid(plm) && (plm&1)!=0 &&
                !GpuDmaProbe.Invalid(progress) && (progress&255)==255 &&
                !GpuDmaProbe.Invalid(memory) && (memory&0x3f0)!=0;
        }
        static string Readiness(IHardware io,Snapshot device,uint[] bars,uint command,bool bootstrap)
        {
            if (!ConfigReady(io,device)) return "config";
            for (uint i=0;i<6;i++) if (io.ReadPci(device.GpuBdf,0x10+i*4,4)!=bars[i]) return "bars";
            if (io.ReadPci(device.GpuBdf,4,2)!=command) return "command";
            if (io.ReadMmio(device.Bar0)!=device.Boot0) return "boot0";
            if (bootstrap && !BootstrapReady(io,device)) return "bootstrap";
            return "ready";
        }
        internal static int Once(IHardware io,Snapshot device,uint[] bars,uint command,Action<string> log,bool fast=false,bool bootstrap=false)
        {
            Stopwatch timer=Stopwatch.StartNew();
            long phaseStart=0, assertMs=-1, configMs=-1, restoreMs=-1, settleMs=-1;
            int configPolls=0, readyPolls=0, bootstrapPending=0, statePending=0;
            string phase="assert_release", lastPending="none";
            bool complete=false;
            try {
            uint control=io.ReadPci(device.BridgeBdf,0x3e,2);
            if ((control&0x40)!=0) throw new IOException("Bridge already in reset.");
            log("SBR "+Gen2Engine.Bdf(device.BridgeBdf)+"+0x3e "+(fast?
                "FAST: assert 100 ms -> release 100 ms + config poll -> restore -> 250 ms + stable poll":
                "assert -> 100 ms -> release -> 1 s -> restore BAR/Command -> 8 s"));
            try {
                io.WritePci(device.BridgeBdf,0x3e,control|0x40,2);
                if (io.ReadPci(device.BridgeBdf,0x3e,2)!=(control|0x40)) throw new IOException("SBR assert readback failed.");
                io.Delay(100);
            }
            finally {
                io.WritePci(device.BridgeBdf,0x3e,control&~0x40U,2);
                if (io.ReadPci(device.BridgeBdf,0x3e,2)!=(control&~0x40U)) throw new IOException("SBR release readback failed.");
            }
            assertMs=timer.ElapsedMilliseconds; phaseStart=timer.ElapsedMilliseconds; phase="config";
            if (fast) {
                io.Delay(100);
                int waited=100;
                for (;;) {
                    configPolls++;
                    if(ConfigReady(io,device)) break;
                    if (waited>=1000) throw new IOException("SBR config/link did not return within 1 s.");
                    io.Delay(20); waited+=20;
                }
            } else io.Delay(1000);
            configMs=timer.ElapsedMilliseconds-phaseStart; phaseStart=timer.ElapsedMilliseconds; phase="bar_restore";
            for (uint i=0;i<6;i++) io.WritePci(device.GpuBdf,0x10+i*4,bars[i],4);
            io.WritePci(device.GpuBdf,4,command,2);
            restoreMs=timer.ElapsedMilliseconds-phaseStart; phaseStart=timer.ElapsedMilliseconds; phase="settle";
            if (fast) {
                // Readable BAR0 alone is not a stable link. Require three
                // consecutive full identity/BAR/Command/link samples. Keep the
                // old 8 s budget for slow hardware without issuing another SBR.
                io.Delay(250);
                int stable=0;
                for (int waited=250;;waited+=50) {
                    string readiness=Readiness(io,device,bars,command,bootstrap); readyPolls++;
                    if(readiness!="ready") {
                        lastPending=readiness;
                        if(readiness=="bootstrap") bootstrapPending++; else statePending++;
                    }
                    stable=readiness=="ready"?stable+1:0;
                    if (stable==3) break;
                    if (waited>=8000) throw new IOException("SBR readiness timeout; use conservative timing on the next run.");
                    io.Delay(50);
                }
            } else io.Delay(8000);
            settleMs=timer.ElapsedMilliseconds-phaseStart; phaseStart=timer.ElapsedMilliseconds; phase="final_verify";
            if (io.ReadPci(device.GpuBdf,0,4)!=Gen2Engine.TargetId || io.ReadMmio(device.Bar0)!=device.Boot0)
                throw new IOException("GPU identity/BAR0 did not survive SBR.");
            for (uint i=0;i<6;i++) if (io.ReadPci(device.GpuBdf,0x10+i*4,4)!=bars[i])
                throw new IOException("BAR readback failed after SBR.");
            if ((io.ReadPci(device.GpuBdf,4,2)&6)!=6) throw new IOException("Memory/bus master decoding lost after SBR.");
            if(bootstrap) {
                if(!BootstrapReady(io,device)) throw new IOException("GFW bootstrap incomplete after SBR.");
                log(String.Format("SBR_BOOTSTRAP_READY GFW_BOOT=0x{0:x8} PROGRESS=0x{1:x8} LMR=0x{2:x8}",
                    io.ReadMmio(device.Bar0+0x118128),io.ReadMmio(device.Bar0+0x118234),io.ReadMmio(device.Bar0+0x100ce0)));
            }
            log("SBR_TIMING mode="+(fast?"fast":"conservative")+" elapsed_ms="+timer.ElapsedMilliseconds);
            complete=true;
            return 0;
            } finally {
                log(String.Format("SBR_PHASE_TIMING mode={0} success={1} phase={2} assert_release_ms={3} config_ms={4} bar_restore_ms={5} settle_ms={6} phase_elapsed_ms={7} config_polls={8} ready_polls={9} bootstrap_pending_polls={10} state_pending_polls={11} last_pending={12} elapsed_ms={13}",
                    fast?"fast":"conservative",complete,phase,assertMs,configMs,restoreMs,settleMs,
                    timer.ElapsedMilliseconds-phaseStart,configPolls,readyPolls,bootstrapPending,statePending,lastPending,timer.ElapsedMilliseconds));
            }
        }
    }
    sealed class FullUnlockEngine
    {
        readonly WindowsHardware io;
        readonly DmaArena arena;
        readonly Action<string> log;
        readonly bool fast;
        readonly Gen2Engine gen2;
        bool managed;
        Snapshot device;
        uint[] bars;
        uint command;
        internal string Stage="NOT_STARTED";
        internal FullUnlockEngine(WindowsHardware hardware,DmaArena dma,Action<string> logger,bool conservative=false)
        {
            io=hardware; arena=dma; log=logger; fast=!conservative;
            gen2=new Gen2Engine(io,log,fast);
        }
        internal static void RequireDisabledDevice()
        {
            int count=0;
            using (ManagementObjectSearcher query=new ManagementObjectSearcher("SELECT PNPDeviceID,ConfigManagerErrorCode FROM Win32_PnPEntity WHERE Present = TRUE"))
            using (ManagementObjectCollection objects=query.Get()) foreach (ManagementObject item in objects) using (item) {
                string id=item["PNPDeviceID"] as string;
                if (id==null || !id.StartsWith(@"PCI\VEN_10DE&DEV_220D",StringComparison.OrdinalIgnoreCase)) continue;
                count++;
                if (Convert.ToUInt32(item["ConfigManagerErrorCode"])!=22)
                    throw new IOException("90HX must be disabled in PnP before full unlock (Code 22 required).");
            }
            if (count!=1) throw new IOException("Expected exactly one disabled 90HX PnP device; found "+count);
        }
        uint Capability(uint bdf,uint id)
        {
            uint p=io.ReadPci(bdf,0x34,1); HashSet<uint> seen=new HashSet<uint>();
            while (p!=0) {
                if (p<0x40 || p>0xfc || (p&3)!=0 || !seen.Add(p)) throw new IOException("Invalid PCI capability chain.");
                if (io.ReadPci(bdf,p,1)==id) return p;
                p=io.ReadPci(bdf,p+1,1);
            }
            return 0;
        }
        void Prepare(uint bdf)
        {
            RequireDisabledDevice(); Stage="PREPARE_DEVICE";
            if (io.ReadPci(bdf,0,4)!=Gen2Engine.TargetId) throw new IOException("Selected BDF is not a live 90HX (possibly D3cold).");
            bars=new uint[6]; for (uint i=0;i<6;i++) bars[i]=io.ReadPci(bdf,0x10+i*4,4);
            if ((bars[0]&0xfffffff0)==0 || bars[0]==0xffffffff) throw new IOException("Disabled GPU has no assigned BAR0.");
            uint pm=Capability(bdf,1);
            if (pm!=0) {
                uint csr=io.ReadPci(bdf,pm+4,2);
                if ((csr&3)!=0) { io.WritePci(bdf,pm+4,csr&~3U&~0x8000U,2); io.Delay(1000); }
                if ((io.ReadPci(bdf,pm+4,2)&3)!=0) throw new IOException("GPU did not return to D0.");
            }
            command=io.ReadPci(bdf,4,2)|6;
            io.WritePci(bdf,4,command,2);
            device=gen2.Capture(bdf);
            // A bridge reset may affect every subordinate device, not just the
            // selected endpoint. Refuse if that subtree contains another device.
            uint range=io.ReadPci(device.BridgeBdf,0x18,4), secondary=(range>>8)&255, subordinate=(range>>16)&255;
            foreach (uint found in gen2.Enumerate(secondary,subordinate))
                if ((found>>8)>=secondary && (found>>8)<=subordinate && found!=bdf)
                    throw new IOException("SBR subtree contains another device: "+Gen2Engine.Bdf(found));
            log("Disabled GPU prepared: "+Gen2Engine.Bdf(bdf)+" BAR0=0x"+device.Bar0.ToString("x"));
        }
        int Reset()
        { return EfiResetSequence.Once(io,device,bars,command,log,fast,managed); }
        void DualReset() { Reset(); Reset(); }
        void Require(CoreSession core,string name,ulong b,ulong c,ulong d,ulong e=0)
        { int result=core.Call(name,b,c,d,e); if (result!=0) throw new IOException(Stage+": "+name+" returned "+result); }
        internal static void InitializeGspForProbe(byte[] bytes,IHardware hardware,Snapshot snapshot,Action<string> logger)
        {
            ulong engine=snapshot.Bar0+0x1103c0, bcr=snapshot.Bar0+0x111668, rm=snapshot.Bar0+0x110084;
            uint savedEngine=hardware.ReadMmio(engine), savedBcr=hardware.ReadMmio(bcr);
            uint cpu=hardware.ReadMmio(snapshot.Bar0+0x110100);
            logger(String.Format("GSP_INIT_PRE ENGINE=0x{0:x8} BCR=0x{1:x8} CPUCTL=0x{2:x8}",savedEngine,savedBcr,cpu));
            if(GpuDmaProbe.Invalid(savedEngine) || GpuDmaProbe.Invalid(savedBcr) || GpuDmaProbe.Invalid(cpu))
                throw new IOException("GSP_INIT_UNAVAILABLE: reset/BCR/CPU control unreadable; no reset attempted.");
            if((savedEngine&1)!=0 || (cpu&0x30)==0)
                throw new IOException("GSP_INIT_UNAVAILABLE: already in reset or Falcon not stopped; no reset attempted.");
            bool resetAttempted=false, resetReleased=false;
            logger("GSP_INIT_BEGIN: explicit GSP-only engine reset using pinned EFI core; firmware/DMEM reset is not reversible. No SBR, CPU start, SEC2, PLM or fuse writes.");
            try {
            using(CoreSession core=new CoreSession(bytes,hardware,snapshot,
                delegate(ulong size,ulong physical) { throw new IOException("DMA allocation forbidden during GSP initialization."); },
                delegate(ulong address,ulong size,ulong physical) { throw new IOException("DMA free forbidden during GSP initialization."); },
                delegate { throw new IOException("SBR forbidden during GSP initialization."); },logger,
                delegate(ulong address,uint value) {
                    if(hardware.ReadPci(snapshot.GpuBdf,0,4)!=Gen2Engine.TargetId || hardware.ReadMmio(snapshot.Bar0)!=snapshot.Boot0)
                        throw new IOException("Selected GPU identity lost during GSP initialization.");
                    if(address==engine && ((value^savedEngine)&~1U)==0) {
                        if((value&1)!=0) resetAttempted=true;
                        else if(resetAttempted) resetReleased=true;
                        else throw new IOException("GSP reset release before assertion.");
                        return;
                    }
                    if(address==bcr) {
                        // Reset legitimately changes BRFETCH and other BCR
                        // state. Compare with the current register, not the
                        // pre-reset snapshot. The pinned helper still may
                        // change only CORE_SELECT/VALID, preserving the rest.
                        uint current=hardware.ReadMmio(bcr);
                        if(!GpuDmaProbe.Invalid(current) && ((value^current)&~0x11U)==0 && (value&0x11)==1) {
                            logger(String.Format("GSP_INIT_BCR_WRITE current=0x{0:x8} value=0x{1:x8}",current,value));
                            return;
                        }
                    }
                    // The pinned reset helper writes BOOT_0 to FALCON_RM
                    // after releasing the engine reset. No other value or
                    // register is permitted in this initialization phase.
                    if(address==rm && resetReleased && value==snapshot.Boot0) {
                        logger(String.Format("GSP_INIT_RM_WRITE BOOT_0=0x{0:x8}",value));
                        return;
                    }
                    throw new IOException(String.Format("GSP initialization write outside engine-reset/BCR whitelist: address=0x{0:x} value=0x{1:x8}",address,value));
                })) {
                if(core.Call("gsp_falcon_reset",0,0,0)!=0) throw new IOException("Pinned GSP reset helper failed.");
            }
            } catch(Exception error) {
                if(resetAttempted) {
                    try {
                        if(hardware.ReadPci(snapshot.GpuBdf,0,4)!=Gen2Engine.TargetId || hardware.ReadMmio(snapshot.Bar0)!=snapshot.Boot0)
                            throw new IOException("GPU identity unavailable; refusing reset-release write.");
                        uint current=hardware.ReadMmio(engine);
                        if(GpuDmaProbe.Invalid(current) || ((current^savedEngine)&~1U)!=0)
                            throw new IOException("Engine register unavailable or unexpected.");
                        if((current&1)!=0) hardware.WriteMmio(engine,current&~1U);
                        if((hardware.ReadMmio(engine)&1)!=0) throw new IOException("Reset release did not read back.");
                        logger("GSP_INIT_FAILURE_RESET_RELEASED: no DMA/firmware attempted; keep device disabled. Original firmware state is not restored.");
                    } catch(Exception releaseError) {
                        logger("GSP_INIT_RESET_RELEASE_UNCONFIRMED: "+releaseError.Message+"; keep device disabled and cold boot.");
                        throw new IOException("GSP_INIT_RESET_RELEASE_UNCONFIRMED",error);
                    }
                }
                throw;
            }
            // The native helper is exactly the EFI's switch/reset/switch with
            // 10 ms on each side. Independently check scrubbing and stopped
            // state; do not silently continue into booting any firmware.
            System.Diagnostics.Stopwatch timer=System.Diagnostics.Stopwatch.StartNew();
            uint cfg,ctl;
            do {
                cfg=hardware.ReadMmio(snapshot.Bar0+0x1100f4);
                ctl=hardware.ReadMmio(snapshot.Bar0+0x11010c);
                if(GpuDmaProbe.Invalid(cfg) || GpuDmaProbe.Invalid(ctl)) throw new IOException("GSP_INIT_POST_RESET_UNREADABLE");
                if((cfg&0x1000)==0 && (ctl&6)==0) break;
                hardware.Delay(1);
            } while(timer.ElapsedMilliseconds<2000);
            cpu=hardware.ReadMmio(snapshot.Bar0+0x110100);
            uint afterBcr=hardware.ReadMmio(bcr), afterEngine=hardware.ReadMmio(engine), cmd=hardware.ReadMmio(snapshot.Bar0+0x110118);
            logger(String.Format("GSP_INIT_POST ENGINE=0x{0:x8} BCR=0x{1:x8} CPUCTL=0x{2:x8} HWCFG2=0x{3:x8} DMACTL=0x{4:x8} DMATRFCMD=0x{5:x8}",afterEngine,afterBcr,cpu,cfg,ctl,cmd));
            if(GpuDmaProbe.Invalid(cpu) || GpuDmaProbe.Invalid(afterBcr) || GpuDmaProbe.Invalid(afterEngine) || GpuDmaProbe.Invalid(cmd) ||
                (afterEngine&1)!=0 || (afterBcr&0x11)!=1 || (cpu&0x30)==0 || (cfg&0x1000)!=0 || (ctl&6)!=0 || (cmd&3)!=2)
                throw new IOException("GSP_INIT_POST_RESET_GATE_FAILED: no firmware/CPU start or automatic retry attempted.");
            logger("GSP_INIT_CONTROL_READY: probe still must verify DMEM PIO; only post-reset probe state can be restored.");
        }
        internal void RunDmaProbe(uint bdf,byte[] initializeCore=null)
        {
            Prepare(bdf); arena.TestMapping();
            if(initializeCore!=null) {
                Stage="GSP_EXPLICIT_INITIALIZE";
                try { InitializeGspForProbe(initializeCore,io,device,log); }
                catch { log("GSP_INITIALIZATION_STOPPED: no probe/firmware fallback or automatic retry; keep device disabled."); throw; }
            }
            GpuDmaProbe probe=new GpuDmaProbe(io,device,arena.Allocate,arena.Free,log);
            try { probe.Run(); } finally { Stage=probe.Stage; }
        }
        internal RecoveryResult Run(byte[] bytes,uint bdf)
        {
            managed=ElfCore.IsManaged(bytes);
            log("UNLOCK_CORE="+(managed?"469dc0c managed-handoff":"380bdf3 legacy"));
            log("UNLOCK_TIMING_MODE="+(fast?"fast":"conservative"));
            Prepare(bdf); arena.TestMapping();
            Stage="BRIDGE_ECAM_PREFLIGHT"; log("STAGE="+Stage); io.EnableBridgeEcam(device,log);
            using (CoreSession core=new CoreSession(bytes,io,device,arena.Allocate,arena.Free,Reset,log,null,fast,
                delegate { log("MANAGED_HANDOFF_BEGIN stage="+Stage); DualReset(); log("MANAGED_HANDOFF_COMPLETE stage="+Stage); return 0; })) {
                Stage="INITIAL_DUAL_SBR"; log("STAGE="+Stage); DualReset();
                Stage="COMPUTE"; log("STAGE="+Stage);
                Require(core,managed?"do_permissive_with_handoff":"do_permissive",1,managed?core.Handoff:0,0);
                if(!managed) DualReset();
                Stage="GRAPHICS"; log("STAGE="+Stage);
                Require(core,managed?"do_permissive_with_handoff":"do_permissive",2,managed?core.Handoff:0,0);
                if(!managed) DualReset();
                IntPtr scratch=Marshal.AllocHGlobal(32);
                try {
                    int opened=0;
                    for (;;) {
                        Stage="GEN2_PLM_SCAN"; Marshal.WriteInt64(scratch,0);
                        int scan=core.Call(managed?"pcie_find_first_closed_plm":"pcie_gen2_find_first_closed_plm",(ulong)scratch.ToInt64(),0,0);
                        if (scan==0) break;
                        if (scan!=1 || opened++>=9) throw new IOException("PLM scan failed or exceeded nine targets.");
                        IntPtr target=new IntPtr(Marshal.ReadInt64(scratch));
                        uint offset=unchecked((uint)Marshal.ReadInt32(target));
                        if (Array.IndexOf(Gen2Engine.PlmOffsets,offset)<0) throw new IOException("Unknown PLM target.");
                        ulong name=unchecked((ulong)Marshal.ReadInt64(target,8));
                        Stage="GEN2_PLM_0x"+offset.ToString("x"); log("STAGE="+Stage);
                        Require(core,managed?"ga102_booter_open_plm_with_handoff":"ga102_v67_open_plm",offset,name,(ulong)scratch.ToInt64()+16,managed?core.Handoff:0);
                        if (Marshal.ReadInt32(scratch,16)!=-1 || io.ReadMmio(device.Bar0+offset)!=0xffffffff)
                            throw new IOException("PLM did not open.");
                        if(!managed) DualReset();
                    }
                    Stage="GEN2_PRE_RESET"; log("STAGE="+Stage); Require(core,managed?"pcie_run_pre_reset_group":"pcie_gen2_run_pre_reset_group",managed?2UL:0,0,0);
                    // Match EFI's post-policy capability gate before prearming
                    // the root port. Do not write v2-only offsets on a v1 device.
                    Stage="GEN2_CAPABILITY_GATE"; log("STAGE="+Stage);
                    Snapshot policyState=gen2.Capture(device.GpuBdf);
                    if (!policyState.Gpu.HasLinkControl2 || !policyState.Bridge.HasLinkControl2 ||
                        (policyState.Gpu.Capability&15)<2 || (policyState.Gpu.Capability2&2)==0 ||
                        (policyState.Bridge.Capability&15)<2)
                        throw new IOException("Gen2 capability gate not ready after policy restore: GPU capability v"+
                            policyState.Gpu.CapabilityVersion+", bridge v"+policyState.Bridge.CapabilityVersion);
                    uint cap=Capability(device.BridgeBdf,0x10); if (cap==0) throw new IOException("Bridge PCIe capability missing.");
                    uint before=io.ReadPci(device.BridgeBdf,cap+0x30,2), value=(before&~15U)|2;
                    io.WritePci(device.BridgeBdf,cap+0x30,value,2);
                    if (io.ReadPci(device.BridgeBdf,cap+0x30,2)!=value) throw new IOException("Bridge prearm readback failed.");
                    DualReset();
                    Stage="GEN2_POST_RESET_GATE"; log("STAGE="+Stage);
                    if(managed) Require(core,"pcie_check_post_reset_gate",2,(ulong)scratch.ToInt64(),0);
                    else Require(core,"pcie_gen2_check_post_reset_gate",(ulong)scratch.ToInt64(),0,0);
                    Stage="GEN2_POST_RESET_RESTORE"; log("STAGE="+Stage);
                    if(managed) Require(core,"pcie_restore_post_reset_group",2,(ulong)scratch.ToInt64(),0);
                    else Require(core,"pcie_gen2_restore_post_reset_group",(ulong)scratch.ToInt64(),0,0);
                } finally { Marshal.FreeHGlobal(scratch); }
                Stage="GEN2_RETRAIN_VERIFY"; log("STAGE="+Stage);
                RecoveryResult result=gen2.Recover(true,bdf,device);
                if (!result.Success || !Gen2Engine.CoreUnlockValid(result.After) || (result.After.Register(0x8860c)&1)==0)
                    throw new IOException("Final Gen2/core/VSEC verification failed: "+result.Code);
                foreach (uint offset in Gen2Engine.PlmOffsets) if (result.After.Register(offset)!=0xffffffff)
                    throw new IOException("PLM relocked during final verification.");
                result.Before=device;
                Stage="FULL_UNLOCK_VERIFIED_WHILE_DISABLED"; log("STAGE="+Stage); return result;
            }
        }
    }
}
