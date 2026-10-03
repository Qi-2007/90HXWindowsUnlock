using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

namespace CMP90HX
{
    static class Program
    {
        static TextWriter logFile;

        static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            try
            {
                Dictionary<string, string> options = Parse(args);
                string command = args.Length == 0 ? "" : args[0].ToLowerInvariant();
                if (options.ContainsKey("log"))
                {
                    string path = Path.GetFullPath(options["log"]);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    logFile = new StreamWriter(path, true, new UTF8Encoding(false)) { AutoFlush = true };
                }
                if (command == "self-test") return SelfTests.Run(Console.Out);
                if (command == "core-test") return CoreTest(ElfCore.PinnedBytes(options["core"]));
                if(options.ContainsKey("dma-backend") && options["dma-backend"]!="kernel") throw new ArgumentException("This standalone project only supports --dma-backend kernel.");
                bool dmaCommand=command=="arena-info" || command=="arena-reserve-test" || command=="dma-test" || command=="full-unlock" || command=="gpu-dma-test" || command=="gpu-dma-init-test";
                if(dmaCommand && !options.ContainsKey("physical-dma-experiment")) throw new ArgumentException("Kernel backend requires --physical-dma-experiment; physical addresses are not IOMMU mappings.");
                if (command == "arena-info")
                {
                    using(KernelDma driver=new KernelDma(false)) Log("KERNEL_DMA_AVAILABLE: boot-lifetime arena; physical address experiment, no IOMMU mapping.");
                    return 0;
                }
                if (command == "arena-reserve-test")
                {
                    using(KernelDma driver=new KernelDma(true))
                        Log(String.Format("KERNEL_DMA_RESERVE_MAPPED physical=0x{0:x} bytes={1}; no GPU DMA performed.",
                            driver.Descriptor.Physical,driver.Descriptor.Length));
                    return 0;
                }
                if (command == "dma-test" || command == "full-unlock" || command == "gpu-dma-test" || command == "gpu-dma-init-test")
                {
                    using (Mutex mutex = new Mutex(false, @"Global\CMP90HX_FullUnlock"))
                    {
                        bool owned;
                        try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
                        if (!owned) throw new IOException("Another DMA/full-unlock operation is running.");
                        try
                        {
                            byte[] bytes = command == "full-unlock" || command == "gpu-dma-init-test" ? ElfCore.PinnedBytes(options["core"]) : null;
                            string directory = options.ContainsKey("drivers") ? Path.GetFullPath(options["drivers"]) : null;
                            using (WindowsHardware hardware = new WindowsHardware(directory))
                            using (KernelDma driver = hardware.MapArena())
                            using (DmaArena arena = new DmaArena(hardware, driver))
                            {
                                Log(String.Format("DMA_BACKEND={0} physical=0x{1:x} bytes={2}","kernel-physical-experiment",arena.Descriptor.Physical,arena.Descriptor.Length));
                                if (command == "dma-test") { arena.TestMapping(); Log("DMA_MAPPING_VERIFIED; probe restored; no GPU write performed."); return 0; }
                                if (!options.ContainsKey("bdf")) throw new ArgumentException(command+" requires --bdf.");
                                FullUnlockEngine engine = new FullUnlockEngine(hardware, arena, Log, options.ContainsKey("conservative"));
                                try {
                                    if(command=="gpu-dma-test" || command=="gpu-dma-init-test") { engine.RunDmaProbe(ParseBdf(options["bdf"]),bytes); return 0; }
                                    RecoveryResult result = engine.Run(bytes, ParseBdf(options["bdf"]));
                                    if(arena.HasOutstanding) throw new RetainedDmaException("DMA_RETAINED: native core reported success without releasing all DMA allocations.");
                                    Print(result.After, "FULL_UNLOCK_AFTER");
                                    if (options.ContainsKey("out")) Save(options["out"], result);
                                    Log("Windows full unlock verified while disabled; re-enable NVIDIA and verify again.");
                                    return 0;
                                } catch(Exception error) {
                                    Log("FAILED_STAGE=" + engine.Stage);
                                    if(arena.HasOutstanding) throw new RetainedDmaException("DMA_RETAINED: firmware ownership is unconfirmed. Keep GPU disabled; cold boot before another attempt.",error);
                                    throw;
                                }
                            }
                        }
                        finally { mutex.ReleaseMutex(); }
                    }
                }
                if (command != "snapshot" && command != "recover" && command != "bridge-probe") return Usage();

                uint? bdf = options.ContainsKey("bdf") ? (uint?)ParseBdf(options["bdf"]) : null;
                string drivers = options.ContainsKey("drivers") ? Path.GetFullPath(options["drivers"]) : null;
                using (WindowsHardware hardware = new WindowsHardware(drivers))
                {
                    Gen2Engine engine = new Gen2Engine(hardware, Log);
                    if (command == "snapshot" || command == "bridge-probe")
                    {
                        Snapshot snapshot = engine.Capture(bdf);
                        if (command == "bridge-probe") hardware.EnableBridgeEcam(snapshot,Log);
                        Print(snapshot, "SNAPSHOT");
                        if (options.ContainsKey("out")) Save(options["out"], snapshot);
                        return 0;
                    }

                    Snapshot baseline = options.ContainsKey("baseline")
                        ? Load<Snapshot>(options["baseline"]) : null;
                    bool restorePolicy = !options.ContainsKey("tls-only");
                    RecoveryResult result = engine.Recover(restorePolicy, bdf, baseline);
                    Print(result.Before, "BEFORE");
                    Print(result.After, "AFTER");
                    Log(String.Format("RESULT={0} MMIO_WRITES={1} PCI_WRITES={2}",
                        result.Code, result.MmioWrites, result.PciWrites));
                    Log(result.Detail);
                    if (options.ContainsKey("out")) Save(options["out"], result);
                    if (result.Success) return 0;
                    if (result.Code == "PLM_REOPEN_REQUIRED") return 3;
                    if (result.Code == "SBR_RESAMPLE_REQUIRED") return 4;
                    if (result.Code == "CORE_UNLOCK_LOST") return 5;
                    return 2;
                }
            }
            catch (Exception e)
            {
                Log("FATAL: " + e.Message);
                Log(e.ToString());
                return e is RetainedDmaException ? 6 : 1;
            }
            finally { if (logFile != null) logFile.Dispose(); }
        }

        static int CoreTest(byte[] bytes)
        {
            if(ElfCore.IsManaged(bytes)) return ManagedCoreTest(bytes);
            using(GpuDmaMock hardware=new GpuDmaMock()) {
                Snapshot snapshot=new Gen2Engine(hardware,Log).Capture(null);
                hardware.BlockPio=true; hardware.AllowReset=true; hardware.ResetSelectsRiscv=true; hardware.Regs[0x1668]=0x11;
                FullUnlockEngine.InitializeGspForProbe(bytes,hardware,snapshot,Log);
                if(hardware.EngineResets!=1 || hardware.Regs[0x3c0]!=0 || hardware.Regs[0x1668]!=0x101 || hardware.Commands!=0 || hardware.Inner.PciWrites!=0)
                    throw new IOException("Pinned GSP reset changed unexpected engine/PCI/DMA state.");
                new GpuDmaProbe(hardware,snapshot,hardware.Allocate,hardware.Free,Log).Run();
                if(hardware.Commands!=2 || hardware.Regs[0x100]!=0x10 || hardware.Inner.MmioWrites!=0)
                    throw new IOException("GSP init/probe test touched forbidden CPU/fuse state.");
                Log("PINNED_GSP_INIT_TEST_PASSED: real EFI reset helper on mock hardware, engine/BCR whitelist, no CPU start/SBR/fuse writes, followed by DMA probe; no real hardware access.");
                Log("PINNED_GSP_INIT_DYNAMIC_BCR_TEST_PASSED: reset changed BCR from 0x1 to 0x111; Falcon switch preserved current BRFETCH state at 0x101.");
            }
            using(GpuDmaMock hardware=new GpuDmaMock()) {
                Snapshot snapshot=new Gen2Engine(hardware,Log).Capture(null);
                hardware.Regs[0x100]=0; hardware.AllowReset=true;
                bool refused=false;
                try { FullUnlockEngine.InitializeGspForProbe(bytes,hardware,snapshot,Log); }
                catch(IOException error) { refused=error.Message.Contains("GSP_INIT_UNAVAILABLE"); }
                if(!refused || hardware.EngineResets!=0 || hardware.Writes!=0) throw new IOException("Running GSP initialization gate failed.");
                Log("PINNED_GSP_INIT_GATE_TEST_PASSED: running/unavailable GSP not silently reset; no real hardware access.");
            }
            using(GpuDmaMock hardware=new GpuDmaMock()) {
                Snapshot snapshot=new Gen2Engine(hardware,Log).Capture(null);
                hardware.AllowReset=true; hardware.FailResetAssert=true;
                List<string> lines=new List<string>(); bool refused=false;
                try { FullUnlockEngine.InitializeGspForProbe(bytes,hardware,snapshot,lines.Add); }
                catch(IOException) { refused=true; }
                if(!refused || hardware.Regs[0x3c0]!=0 || hardware.Commands!=0 ||
                    !String.Join("\n",lines).Contains("GSP_INIT_FAILURE_RESET_RELEASED"))
                    throw new IOException("GSP initialization failed to release a partially asserted reset.");
                Log("PINNED_GSP_INIT_RELEASE_TEST_PASSED: failed native callback cannot leave asserted reset without explicit release check; no DMA/real hardware access.");
            }
            using (MockHardware hardware = new MockHardware())
            {
                Snapshot snapshot = new Gen2Engine(hardware, Log).Capture(null);
                using (CoreSession core = new CoreSession(bytes, hardware, snapshot,
                    delegate(ulong size, ulong physical) { throw new Exception("Unexpected DMA during read-only/policy core test."); },
                    delegate(ulong address, ulong size, ulong physical) { throw new Exception("Unexpected DMA free during core test."); },
                    delegate { throw new Exception("Unexpected SBR during core test."); }, Log))
                {
                    IntPtr scratch = System.Runtime.InteropServices.Marshal.AllocHGlobal(16);
                    try
                    {
                        int result = core.Call("pcie_gen2_find_first_closed_plm", (ulong)scratch.ToInt64(), 0, 0);
                        if (result != 0) throw new Exception("All-open PLM scan returned " + result);
                        hardware.ClosePlm();
                        result = core.Call("pcie_gen2_find_first_closed_plm", (ulong)scratch.ToInt64(), 0, 0);
                        if (result != 1) throw new Exception("Closed PLM scan returned " + result);
                        IntPtr target = new IntPtr(System.Runtime.InteropServices.Marshal.ReadInt64(scratch));
                        if (System.Runtime.InteropServices.Marshal.ReadInt32(target) != 0x8200fc)
                            throw new Exception("Unexpected first closed PLM target.");
                        hardware.WriteMmio(MockHardware.Bar + 0x8200fc, 0xffffffff);
                        result = core.Call("pcie_gen2_run_pre_reset_group", 0, 0, 0);
                        if (result != 0) throw new Exception("Core pre-reset policy returned " + result);
                        foreach (RegisterRule rule in Gen2Engine.Rules)
                            if ((hardware.ReadMmio(MockHardware.Bar + rule.Offset) & rule.ClearMask) != rule.SetMask)
                                throw new Exception("Native core policy mismatch: " + rule.Name);
                        Log("PINNED_CORE_POLICY_TEST_PASSED: real ELF relocated/executed with mock MMIO; all-open/closed PLM scan and five policy rules checked; no real hardware access.");
                    }
                    finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(scratch); }
                }
                using (CoreSession failureCore = new CoreSession(bytes, hardware, snapshot,
                    delegate(ulong size, ulong physical) { throw new IOException("Simulated DMA allocation failure."); },
                    delegate(ulong address, ulong size, ulong physical) { }, delegate { return 0; }, delegate(string text) { }))
                {
                    ulong callback = unchecked((ulong)System.Runtime.InteropServices.Marshal.ReadInt64(failureCore.Gpu, 56));
                    SysV.Function allocator = failureCore.Core.Abi.Forward(callback);
                    if (allocator(failureCore.G, 4096, 0, 0, 0, 0) != 0)
                        throw new Exception("Failed DMA callback must return NULL, never -1 as a pointer.");
                    bool blocked = false;
                    try { failureCore.Call("pcie_gen2_run_pre_reset_group", 0, 0, 0); }
                    catch (IOException) { blocked = true; }
                    if (!blocked) throw new Exception("Native execution continued after failed callback.");
                    failureCore.Dispose(); // Dispose must also be safe if called again by using.
                }
                Log("PINNED_CORE_TEST_PASSED: native DMA callback failure returns NULL, no exception crosses ELF, further execution refused; no real hardware access.");
                return 0;
            }
        }

        static int ManagedCoreTest(byte[] bytes)
        {
            using(GpuDmaMock hardware=new GpuDmaMock()) {
                Snapshot snapshot=new Gen2Engine(hardware,Log).Capture(null);
                hardware.BlockPio=true; hardware.AllowReset=true; hardware.ResetSelectsRiscv=true;
                hardware.Regs[0x1668]=0x11;
                hardware.Regs[0xf4]=0x47f7; // Observed on the stopped 90HX.
                FullUnlockEngine.InitializeGspForProbe(bytes,hardware,snapshot,Log);
                if(hardware.EngineResets!=1 || hardware.Regs[0x3c0]!=0 || hardware.Regs[0x1668]!=0x101 ||
                   hardware.Regs[0x84]!=snapshot.Boot0 || hardware.Commands!=0 || hardware.Inner.PciWrites!=0)
                    throw new IOException("Managed core GSP reset changed unexpected state.");
                new GpuDmaProbe(hardware,snapshot,hardware.Allocate,hardware.Free,Log).Run();
                if(hardware.Commands!=2 || hardware.Regs[0x100]!=0x10 || hardware.Inner.MmioWrites!=0)
                    throw new IOException("Managed core init/probe touched forbidden CPU/fuse state.");
                Log("PINNED_MANAGED_GSP_INIT_TEST_PASSED: real 469dc0c reset helper, FALCON_RM BOOT_0 write, and DMA probe on mock hardware only.");
            }
            using(TraceHardware hardware=new TraceHardware()) {
                Snapshot s=new Gen2Engine(hardware,delegate(string _) {}).Capture(null);
                uint[] bars=new uint[6]; for(uint i=0;i<6;i++) bars[i]=hardware.ReadPci(s.GpuBdf,0x10+4*i,4);
                int handed=0;
                using(CoreSession core=new CoreSession(bytes,hardware,s,
                    delegate(ulong size,ulong physical) { throw new IOException("Unexpected DMA in policy/ABI test."); },
                    delegate(ulong address,ulong size,ulong physical) { throw new IOException("Unexpected DMA free."); },
                    delegate { throw new IOException("Unexpected legacy reset."); },Log,null,false,
                    delegate { handed++; EfiResetSequence.Once(hardware,s,bars,6,Log,true,true); EfiResetSequence.Once(hardware,s,bars,6,Log,true,true); return 0; })) {
                    IntPtr scratch=Marshal.AllocHGlobal(32);
                    try {
                        if(!core.Core.Managed) throw new IOException("Managed version detection failed.");
                        int beforeCompute=hardware.Inner.MmioWrites;
                        if(core.Call("do_permissive_with_handoff",1,core.Handoff,0)!=0 ||
                            hardware.Inner.MmioWrites!=beforeCompute || handed!=0)
                            throw new IOException("Managed already-overridden compute path changed hardware or requested unnecessary handoff.");
                        if(core.Call("pcie_find_first_closed_plm",(ulong)scratch.ToInt64(),0,0)!=0) throw new IOException("Managed all-open scan failed.");
                        hardware.Inner.ClosePlm();
                        if(core.Call("pcie_find_first_closed_plm",(ulong)scratch.ToInt64(),0,0)!=1) throw new IOException("Managed closed scan failed.");
                        IntPtr target=new IntPtr(Marshal.ReadInt64(scratch));
                        uint offset=unchecked((uint)Marshal.ReadInt32(target));
                        ulong name=unchecked((ulong)Marshal.ReadInt64(target,8));
                        if(Array.IndexOf(Gen2Engine.PlmOffsets,offset)<0) throw new IOException("Managed target ABI mismatch.");
                        int writes=hardware.Inner.MmioWrites;
                        if(core.Call("ga102_booter_open_plm_with_handoff",offset,name,(ulong)scratch.ToInt64()+16,0)==0 || hardware.Inner.MmioWrites!=writes)
                            throw new IOException("Managed PLM entry did not reject a missing fifth-argument callback.");
                        hardware.Inner.WriteMmio(MockHardware.Bar+0x8200fc,0xffffffff);
                        if(core.Call("pcie_run_pre_reset_group",2,0,0)!=0) throw new IOException("Managed pre-reset policy failed.");
                        foreach(RegisterRule rule in Gen2Engine.Rules)
                            if((hardware.ReadMmio(s.Bar0+rule.Offset)&rule.ClearMask)!=rule.SetMask) throw new IOException("Managed Gen2 policy differs at "+rule.Name);
                        if(core.Call("pcie_check_post_reset_gate",2,(ulong)scratch.ToInt64(),0)!=0) throw new IOException("Managed post-reset ABI failed.");
                        if(core.Call("pcie_restore_post_reset_group",2,(ulong)scratch.ToInt64(),0)!=0) throw new IOException("Managed policy restore ABI failed.");
                        SysV.Function callback=core.Core.Abi.Forward(core.Handoff);
                        if(callback(core.G,0,0,0,0,0)!=0 || handed!=1 || core.HandoffCalls!=1 ||
                            String.Join(",",hardware.BridgeControls)!="64,0,64,0") throw new IOException("Managed callback ABI/dual SBR failed.");
                    } finally { Marshal.FreeHGlobal(scratch); }
                }
            }
            using(MockHardware hardware=new MockHardware()) {
                Snapshot s=new Gen2Engine(hardware,delegate(string _) {}).Capture(null);
                using(CoreSession core=new CoreSession(bytes,hardware,s,
                    delegate(ulong size,ulong physical) { return 0; },delegate(ulong address,ulong size,ulong physical) {},
                    delegate { return 0; },Log,null,false,delegate { throw new IOException("Simulated handoff failure."); })) {
                    SysV.Function callback=core.Core.Abi.Forward(core.Handoff);
                    if(callback(core.G,0,0,0,0,0)!=UInt64.MaxValue) throw new IOException("Handoff failure did not cross ABI as an error code.");
                    bool refused=false; try { core.Call("pcie_run_pre_reset_group",2,0,0); } catch(IOException) { refused=true; }
                    if(!refused || hardware.MmioWrites!=0) throw new IOException("Handoff callback failure did not latch.");
                }
            }
            Log("PINNED_MANAGED_CORE_TEST_PASSED: real 469dc0c ELF imports/relocations, Gen2 policies, PLM target/fifth argument, dual-SBR callback ABI and exception containment; mock hardware only.");
            return 0;
        }

        static Dictionary<string, string> Parse(string[] args)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--")) throw new ArgumentException("Unexpected argument: " + args[i]);
                string key = args[i].Substring(2);
                if (result.ContainsKey(key)) throw new ArgumentException("Duplicate option --" + key);
                if (key == "tls-only" || key == "conservative" || key == "physical-dma-experiment") { result[key] = "1"; continue; }
                if (++i >= args.Length || args[i].StartsWith("--")) throw new ArgumentException("Missing value for --" + key);
                if (result.ContainsKey(key)) throw new ArgumentException("Duplicate option --" + key);
                result[key] = args[i];
            }
            return result;
        }

        static uint ParseBdf(string text)
        {
            string value = text.Trim();
            int colon = value.LastIndexOf(':'), dot = value.LastIndexOf('.');
            if (colon < 0 || dot < colon) throw new FormatException("BDF must look like 01:00.0 or 0000:01:00.0");
            string busText = value.Substring(0, colon);
            int prior = busText.LastIndexOf(':');
            if (prior >= 0) busText = busText.Substring(prior + 1);
            uint bus = Convert.ToUInt32(busText, 16);
            uint dev = Convert.ToUInt32(value.Substring(colon + 1, dot - colon - 1), 16);
            uint fn = Convert.ToUInt32(value.Substring(dot + 1), 16);
            if (bus > 255 || dev > 31 || fn > 7) throw new FormatException("BDF out of range.");
            return (bus << 8) | (dev << 3) | fn;
        }

        static void Print(Snapshot s, string title)
        {
            if (s == null) { Log(title + ": unavailable"); return; }
            Log(String.Format("{0}: GPU={1} Gen{2} x{3} TLS={4}; RP={5} Gen{6} x{7} TLS={8}; BAR0=0x{9:x}",
                title, Gen2Engine.Bdf(s.GpuBdf), s.Gpu.Speed, s.Gpu.Width,
                s.Gpu.HasLinkControl2 ? (s.Gpu.Control2 & 15).ToString() : "n/a(v1)",
                Gen2Engine.Bdf(s.BridgeBdf), s.Bridge.Speed, s.Bridge.Width,
                s.Bridge.HasLinkControl2 ? (s.Bridge.Control2 & 15).ToString() : "n/a(v1)", s.Bar0));
            foreach (RegisterValue r in s.Registers)
                if (r.Name == "SM_SPEED_SELECT_0" || r.Name == "SM_SPEED_SELECT_1" ||
                    r.Name == "GFX_SPEED_SELECT" || r.Name == "XVE_VSEC_DEVICE" ||
                    r.Name == "XP_PL_LCTRL_2")
                    Log(r.Error == null ? String.Format("  {0}=0x{1:x8}", r.Name, r.Value)
                                        : "  " + r.Name + " ERROR=" + r.Error);
        }

        static void Save<T>(string file, T value)
        {
            string path = Path.GetFullPath(file);
            string directory = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporary = path + ".tmp";
            using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            Log("Saved " + path);
        }

        static T Load<T>(string file)
        {
            using (FileStream stream = File.OpenRead(Path.GetFullPath(file)))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }

        static void Log(string text)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + text;
            Console.WriteLine(line);
            if (logFile != null) logFile.WriteLine(line);
        }

        static int Usage()
        {
            Console.WriteLine("CMP90HX Windows Gen2 wake recovery proof of concept");
            Console.WriteLine("  CMP90HXGen2.exe snapshot --out baseline.json [--drivers DIR] [--bdf 01:00.0]");
            Console.WriteLine("  CMP90HXGen2.exe bridge-probe --drivers DIR --bdf 02:00.0 [--log FILE] (read-only ECAM/HAL comparison)");
            Console.WriteLine("  CMP90HXGen2.exe recover --baseline baseline.json --out result.json [--drivers DIR] [--bdf 01:00.0] [--tls-only] [--log FILE]");
            Console.WriteLine("  CMP90HXGen2.exe self-test");
            Console.WriteLine("  DMA commands require --physical-dma-experiment and preinstalled CMP90HXDma driver (kernel backend only).");
            Console.WriteLine("  CMP90HXGen2.exe arena-info [--log FILE]");
            Console.WriteLine("  CMP90HXDma protocol v2 must be installed/started for hardware commands; --drivers is a compatibility option only.");
            Console.WriteLine("  CMP90HXGen2.exe arena-reserve-test [--log FILE] (no GPU access)");
            Console.WriteLine("  CMP90HXGen2.exe dma-test --drivers DIR [--log FILE]");
            Console.WriteLine("  CMP90HXGen2.exe gpu-dma-test --drivers DIR --bdf 02:00.0 [--log FILE] (already disabled Code 22 required)");
            Console.WriteLine("  CMP90HXGen2.exe gpu-dma-init-test --core PINNED_OBJECT --drivers DIR --bdf 02:00.0 [--log FILE] (explicit GSP engine reset; Code 22 required)");
            Console.WriteLine("  CMP90HXGen2.exe core-test --core PINNED_OBJECT");
            Console.WriteLine("  CMP90HXGen2.exe full-unlock --core PINNED_OBJECT --drivers DIR --bdf 02:00.0 --out full-unlock.json [--conservative] [--log FILE] (fast timing by default; PnP Code 22 required)");
            return 1;
        }
    }
}
