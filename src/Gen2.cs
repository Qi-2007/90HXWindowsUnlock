using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;

namespace CMP90HX
{
    public sealed class RegisterRule
    {
        public uint Offset, ClearMask, SetMask;
        public string Name;
        public RegisterRule(uint offset, uint clear, uint set, string name)
        { Offset = offset; ClearMask = clear; SetMask = set; Name = name; }
        public uint Apply(uint value) { return (value & ~ClearMask) | SetMask; }
    }

    public sealed class RegisterValue
    {
        public uint Offset, Value;
        public string Name, Error;
    }

    public sealed class LinkState
    {
        public uint CapabilityOffset, CapabilityFlags;
        public uint Capability, Capability2, Control, Control2, Status;
        public int CapabilityVersion { get { return (int)(CapabilityFlags & 15); } }
        public bool HasLinkControl2 { get { return CapabilityVersion >= 2; } }
        public int Speed { get { return (int)(Status & 15); } }
        public int Width { get { return (int)((Status >> 4) & 63); } }
        public bool Training { get { return (Status & 0x800) != 0; } }
    }

    public sealed class Snapshot
    {
        public int Schema = 1;
        public string TimestampUtc;
        public uint GpuBdf, BridgeBdf, DeviceId, Boot0;
        public ulong Bar0;
        public LinkState Gpu, Bridge;
        public List<RegisterValue> Registers = new List<RegisterValue>();
        public uint Register(uint offset)
        {
            RegisterValue v = Registers.Find(delegate(RegisterValue r) { return r.Offset == offset; });
            if (v == null || v.Error != null) throw new Exception("Register unavailable: 0x" + offset.ToString("x8"));
            return v.Value;
        }
    }

    public sealed class RecoveryResult
    {
        public string Code, Detail;
        public Snapshot Before, After;
        public int MmioWrites, PciWrites;
        public bool Success { get { return Code == "GEN2_VERIFIED" || Code == "ALREADY_GEN2"; } }
    }

    public sealed class Gen2Engine
    {
        public const uint TargetId = 0x220d10de;
        // Verified against the pinned core's run_register_group, .text+0x3f70,
        // and common/pcie_gen2.h. Preserve every non-policy bit (incl. remote rate).
        public static readonly RegisterRule[] Rules = {
            new RegisterRule(0x8872c, 0xf, 0x6, "XVE_FUSE_OVERRIDE"),
            new RegisterRule(0x8841c, 0x7800, 0x2800, "XVE_PRIV_MISC_1"),
            new RegisterRule(0x8c2c0, 0x4, 0, "XP_PL_CYA_0"),
            new RegisterRule(0x8c040, 0xc0000, 0x80000, "XP_PL_LINK_CONFIG_0"),
            new RegisterRule(0x8c1c0, 0xf0000, 0x20000, "XP_PL_LCTRL_2")
        };
        public static readonly uint[] PlmOffsets = {
            0x8200fc, 0x8e1b0, 0x8e1b4, 0x8e1b8, 0x8e1bc,
            0x88fe8, 0x88fec, 0x88ff0, 0x8e1f0
        };
        readonly IHardware io;
        readonly Action<string> log;
        readonly bool reuseTopology;
        sealed class Topology
        {
            internal uint Gpu, Bridge, BridgeId, Buses, Boot0;
            internal ulong Bar0;
        }
        Topology topology;
        public Gen2Engine(IHardware hardware, Action<string> logger) : this(hardware,logger,false) { }
        // Reuse only inside one disabled-device unlock session. Standalone
        // snapshot/recover and new instances always rediscover the topology.
        internal Gen2Engine(IHardware hardware,Action<string> logger,bool reuse)
        { io=hardware; log=logger; reuseTopology=reuse; }
        public static string Bdf(uint bdf)
        { return String.Format("0000:{0:x2}:{1:x2}.{2}", bdf >> 8, (bdf >> 3) & 31, bdf & 7); }

        bool Present(uint bdf, out uint id)
        {
            try { id = io.ReadPci(bdf, 0, 4); return (id & 0xffff) != 0xffff && (id & 0xffff) != 0; }
            catch (Win32Exception e)
            {
                // Preserve access/handle errors while tolerating PCI scan misses.
                // The unified driver normally returns all ones for absent IDs.
                // Preserve real access/handle failures and treat other scan misses
                // as absent. A completely broken transport still yields zero GPUs.
                if (e.NativeErrorCode == 5 || e.NativeErrorCode == 6) throw;
                id = 0xffffffff; return false;
            }
        }

        internal List<uint> Enumerate(uint firstBus=0,uint lastBus=255)
        {
            if(firstBus>lastBus || lastBus>255) throw new ArgumentException("Invalid PCI bus range.");
            List<uint> devices = new List<uint>();
            for (uint bus = firstBus; bus <= lastBus; bus++)
            for (uint dev = 0; dev < 32; dev++)
            {
                uint bdf = (bus << 8) | (dev << 3), id;
                if (!Present(bdf, out id)) continue;
                devices.Add(bdf);
                if ((io.ReadPci(bdf, 0x0e, 1) & 0x80) == 0) continue;
                for (uint fn = 1; fn < 8; fn++) if (Present(bdf | fn, out id)) devices.Add(bdf | fn);
            }
            return devices;
        }

        public Snapshot Capture(uint? selectedBdf)
        {
            if(topology!=null) {
                if(selectedBdf.HasValue && selectedBdf.Value!=topology.Gpu)
                    throw new IOException("Selected GPU changed within the unlock session.");
                ValidateTopology();
                return CaptureDevice(topology.Gpu,topology.Bridge);
            }
            List<uint> devices = Enumerate();
            List<uint> candidates = devices.FindAll(delegate(uint d) { return io.ReadPci(d, 0, 4) == TargetId; });
            if (selectedBdf.HasValue) candidates.RemoveAll(delegate(uint d) { return d != selectedBdf.Value; });
            if (candidates.Count != 1) throw new Exception("Expected exactly one selected 10de:220d; found " + candidates.Count);
            uint gpu = candidates[0], bus = gpu >> 8;
            List<uint> bridges = devices.FindAll(delegate(uint d) {
                return ((io.ReadPci(d, 8, 4) >> 16) & 0xffff) == 0x0604 &&
                    ((io.ReadPci(d, 0x18, 4) >> 8) & 255) == bus;
            });
            if (bridges.Count != 1) throw new Exception("Immediate upstream bridge is missing or ambiguous.");
            uint bridge=bridges[0];
            Snapshot result=CaptureDevice(gpu,bridge);
            if(reuseTopology) {
                topology=new Topology { Gpu=gpu, Bridge=bridge, BridgeId=io.ReadPci(bridge,0,4),
                    Buses=io.ReadPci(bridge,0x18,4)&0xffffff, Bar0=result.Bar0, Boot0=result.Boot0 };
                ValidateTopology();
            }
            return result;
        }

        void ValidateTopology()
        {
            uint id=io.ReadPci(topology.Bridge,0,4), buses=io.ReadPci(topology.Bridge,0x18,4)&0xffffff;
            if(id!=topology.BridgeId || (id&65535)==0 || (id&65535)==65535 ||
                (io.ReadPci(topology.Bridge,0x0e,1)&0x7f)!=1 ||
                (io.ReadPci(topology.Bridge,8,4)>>16)!=0x0604 || buses!=topology.Buses ||
                ((buses>>8)&255)!=(topology.Gpu>>8) || ((buses>>16)&255)<(topology.Gpu>>8) ||
                io.ReadPci(topology.Gpu,0,4)!=TargetId)
                throw new IOException("GPU/upstream bridge identity or routing changed during unlock.");
        }

        Snapshot CaptureDevice(uint gpu,uint bridge)
        {
            WindowsHardware windows=io as WindowsHardware;
            if(windows!=null) windows.Bind(gpu,bridge);
            uint low = io.ReadPci(gpu, 0x10, 4);
            if (low == 0 || low == 0xffffffff || (low & 1) != 0) throw new Exception("Invalid BAR0.");
            uint type = low & 6;
            if (type != 0 && type != 4) throw new Exception("Unsupported BAR0 type.");
            ulong bar = (ulong)(low & 0xfffffff0);
            if (type == 4) bar |= (ulong)io.ReadPci(gpu, 0x14, 4) << 32;
            if (bar == 0) throw new Exception("BAR0 not assigned.");
            if(topology!=null && bar!=topology.Bar0) throw new IOException("BAR0 reassigned during unlock.");
            uint boot = io.ReadMmio(bar);
            if ((boot & 0x1f000000) != 0x17000000 || (boot & 0xf00000) != 0x200000)
                throw new Exception("BOOT0 is not GA102: 0x" + boot.ToString("x8"));
            if(topology!=null && boot!=topology.Boot0) throw new IOException("BOOT0 changed during unlock.");
            if ((io.ReadPci(gpu, 4, 2) & 2) == 0) throw new Exception("GPU memory decoding is disabled.");
            Snapshot s = new Snapshot { TimestampUtc = DateTime.UtcNow.ToString("o"), GpuBdf = gpu,
                BridgeBdf = bridge, DeviceId = TargetId, Boot0 = boot, Bar0 = bar,
                Gpu = ReadLink(gpu), Bridge = ReadLink(bridge) };
            LogCapability(gpu, s.Gpu);
            LogCapability(bridge, s.Bridge);
            foreach (uint offset in PlmOffsets) AddRegister(s, offset, "PLM");
            foreach (RegisterRule rule in Rules) AddRegister(s, rule.Offset, rule.Name);
            AddRegister(s, 0x8860c, "XVE_VSEC_DEVICE");
            AddRegister(s, 0x88084, "XVE_LINK_CAPABILITIES");
            AddRegister(s, 0x880a4, "XVE_LINK_CAPABILITIES_2");
            AddRegister(s, 0x82381c, "SM_SPEED_SELECT_0");
            AddRegister(s, 0x823820, "SM_SPEED_SELECT_1");
            AddRegister(s, 0x823830, "GFX_SPEED_SELECT");
            return s;
        }

        void AddRegister(Snapshot s, uint offset, string name)
        {
            RegisterValue r = new RegisterValue { Offset = offset, Name = name };
            try { r.Value = io.ReadMmio(checked(s.Bar0 + offset)); }
            catch (Exception e) { r.Error = e.Message; }
            s.Registers.Add(r);
        }

        uint FindCapability(uint bdf)
        {
            uint p = io.ReadPci(bdf, 0x34, 1);
            HashSet<uint> visited = new HashSet<uint>();
            while (p != 0)
            {
                if (p < 0x40 || p > 0xfc || (p & 3) != 0 || !visited.Add(p))
                    throw new Exception("Invalid/cyclic PCI capability list: " + Bdf(bdf));
                if (io.ReadPci(bdf, p, 1) == 0x10) return p;
                p = io.ReadPci(bdf, p + 1, 1);
            }
            throw new Exception("PCIe capability missing: " + Bdf(bdf));
        }

        LinkState ReadLink(uint bdf)
        {
            uint cap = FindCapability(bdf);
            uint flags = io.ReadPci(bdf, cap + 2, 2);
            int version = (int)(flags & 15);
            if (version == 0 || flags == 0xffff)
                throw new Exception(String.Format("Invalid PCIe capability: {0} cap=0x{1:x2} flags=0x{2:x4}", Bdf(bdf), cap, flags));
            // Cold/locked state may expose v1 before the core's Gen2 protocol
            // override. Snapshot must not require the outcome of the unlock.
            // v1 defines LNKCAP/LNKCTL/LNKSTA, not LNKCAP2/LNKCTL2.
            return new LinkState { CapabilityOffset = cap, CapabilityFlags = flags,
                Capability = io.ReadPci(bdf, cap + 0xc, 4),
                Capability2 = version >= 2 ? io.ReadPci(bdf, cap + 0x2c, 4) : 0,
                Control = io.ReadPci(bdf, cap + 0x10, 2),
                Status = io.ReadPci(bdf, cap + 0x12, 2),
                Control2 = version >= 2 ? io.ReadPci(bdf, cap + 0x30, 2) : 0 };
        }

        void LogCapability(uint bdf, LinkState link)
        {
            log(String.Format("PCIE_CAP {0} offset=0x{1:x2} flags=0x{2:x4} version={3}; LNKCTL2={4}",
                Bdf(bdf), link.CapabilityOffset, link.CapabilityFlags, link.CapabilityVersion,
                link.HasLinkControl2 ? "available" : "unavailable (not read)"));
        }

        static bool InvalidRegister(uint value)
        { return value == 0xffffffff || (value & 0xffff0000) == 0xbadf0000; }

        void Liveness(Snapshot s)
        {
            if(topology!=null) ValidateTopology();
            if (io.ReadPci(s.GpuBdf, 0, 4) != TargetId || io.ReadMmio(s.Bar0) != s.Boot0)
                throw new Exception("GPU identity/BAR0 changed during recovery.");
            ulong bar = io.ReadPci(s.GpuBdf, 0x10, 4) & 0xfffffff0U;
            if ((io.ReadPci(s.GpuBdf, 0x10, 4) & 6) == 4)
                bar |= (ulong)io.ReadPci(s.GpuBdf, 0x14, 4) << 32;
            if (bar != s.Bar0) throw new Exception("BAR0 was reassigned during recovery.");
        }

        void SetControl(Snapshot s, uint bdf, uint offset, uint clear, uint set, RecoveryResult result)
        {
            Liveness(s);
            uint before = io.ReadPci(bdf, offset, 2), value = (before & ~clear) | set;
            if (value == before) return;
            io.WritePci(bdf, offset, value, 2); result.PciWrites++;
            uint post = io.ReadPci(bdf, offset, 2);
            log(String.Format("PCI {0}+0x{1:x}: {2:x4} -> {3:x4} readback={4:x4}", Bdf(bdf), offset, before, value, post));
            if (post != value) throw new Exception("PCI control readback mismatch.");
        }

        public RecoveryResult Recover(bool restorePolicy, uint? selectedBdf, Snapshot baseline)
        {
            RecoveryResult result = new RecoveryResult();
            result.Before = Capture(selectedBdf);
            Snapshot s = result.Before;
            int gpuWidth = s.Gpu.Width, rootWidth = s.Bridge.Width;
            if (baseline != null)
            {
                if (baseline.Schema != 1 || baseline.DeviceId != TargetId || baseline.Gpu == null || baseline.Bridge == null ||
                    baseline.GpuBdf != s.GpuBdf || baseline.BridgeBdf != s.BridgeBdf || baseline.Boot0 != s.Boot0)
                    throw new Exception("Baseline does not match current GA102 device/topology.");
                gpuWidth = Math.Max(gpuWidth, baseline.Gpu.Width);
                rootWidth = Math.Max(rootWidth, baseline.Bridge.Width);
            }
            if (!CoreUnlockValid(s))
            {
                result.After = s;
                result.Code = "CORE_UNLOCK_LOST";
                result.Detail = "Compute/graphics fuse override values no longer match the EFI-unlocked state.";
                return result;
            }
            if (!s.Gpu.HasLinkControl2 || !s.Bridge.HasLinkControl2)
            {
                result.After = s;
                result.Code = "PCIE_V2_NOT_AVAILABLE";
                result.Detail = String.Format("LNKCTL2 writes refused: GPU capability v{0}, bridge v{1}. A v1 snapshot is allowed, but retraining requires the Gen2 protocol override first.",
                    s.Gpu.CapabilityVersion, s.Bridge.CapabilityVersion);
                return result;
            }
            if (gpuWidth == 0 || rootWidth == 0) throw new Exception("No live negotiated PCIe width.");
            if (Verified(s.Gpu, s.Bridge, gpuWidth, rootWidth))
            {
                result.After = s; result.Code = "ALREADY_GEN2";
                result.Detail = "Both ends already report Gen2 without width loss."; return result;
            }
            if ((s.Bridge.Capability & 15) < 2) throw new Exception("Upstream bridge does not advertise Gen2.");
            try
            {
                if (restorePolicy)
                {
                    foreach (RegisterRule rule in Rules)
                    {
                        Liveness(s);
                        uint old = io.ReadMmio(s.Bar0 + rule.Offset);
                        if (InvalidRegister(old)) throw new Exception(rule.Name + " returned invalid MMIO value.");
                        uint value = rule.Apply(old);
                        if (value == old) { log(rule.Name + " policy already set"); continue; }
                        io.WriteMmio(s.Bar0 + rule.Offset, value); result.MmioWrites++;
                        uint post = io.ReadMmio(s.Bar0 + rule.Offset);
                        log(String.Format("MMIO {0} 0x{1:x8}: {2:x8} -> {3:x8} readback={4:x8}",
                            rule.Name, rule.Offset, old, value, post));
                        if (post != value) throw new Exception(rule.Name + " write did not stick; PLM may have relocked.");
                    }
                }
                uint gcap = FindCapability(s.GpuBdf), rcap = FindCapability(s.BridgeBdf);
                // RMW only TLS and ASPM; do not guess Common Clock/Extended Sync.
                SetControl(s, s.GpuBdf, gcap + 0x30, 15, 2, result);
                SetControl(s, s.BridgeBdf, rcap + 0x30, 15, 2, result);
                SetControl(s, s.GpuBdf, gcap + 0x10, 3, 0, result);
                SetControl(s, s.BridgeBdf, rcap + 0x10, 3, 0, result);
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    Liveness(s);
                    uint ctl = io.ReadPci(s.BridgeBdf, rcap + 0x10, 2);
                    if ((ctl & 0x10) != 0) throw new Exception("Upstream link is disabled.");
                    io.WritePci(s.BridgeBdf, rcap + 0x10, ctl | 0x20, 2); result.PciWrites++;
                    log("Root-port retrain attempt " + (attempt + 1));
                    int stable = 0;
                    for (int poll = 0; poll < 50; poll++)
                    {
                        io.Delay(100);
                        LinkState g = ReadLink(s.GpuBdf), r = ReadLink(s.BridgeBdf);
                        stable = Verified(g, r, gpuWidth, rootWidth) ? stable + 1 : 0;
                        if (stable < 3) continue;
                        result.After = Capture(s.GpuBdf);
                        if (!Verified(result.After.Gpu, result.After.Bridge, gpuWidth, rootWidth)) continue;
                        result.Code = "GEN2_VERIFIED";
                        result.Detail = "Both ends observed at Gen2 for three polls; negotiated width retained.";
                        return result;
                    }
                }
                result.After = Capture(s.GpuBdf);
                ClassifyFailure(result, "Retrain did not produce verified Gen2 on both ends. TLS=2 alone is not success.");
            }
            catch (Exception e)
            {
                try { result.After = Capture(s.GpuBdf); } catch { result.After = s; }
                ClassifyFailure(result, e.Message);
            }
            return result;
        }

        public static bool Verified(LinkState gpu, LinkState bridge, int gpuWidth, int bridgeWidth)
        { return gpu.Speed == 2 && bridge.Speed == 2 && !gpu.Training && !bridge.Training &&
            gpu.Width >= gpuWidth && bridge.Width >= bridgeWidth && gpu.Width > 0 && bridge.Width > 0; }

        public static bool CoreUnlockValid(Snapshot snapshot)
        {
            try { return snapshot.Register(0x82381c) == 0x88888888 &&
                         snapshot.Register(0x823820) == 8 && snapshot.Register(0x823830) == 4; }
            catch { return false; }
        }

        static void ClassifyFailure(RecoveryResult result, string detail)
        {
            Snapshot s = result.After;
            result.Code = "GEN2_UNCONFIRMED";
            foreach (uint offset in PlmOffsets)
            {
                RegisterValue r = s.Registers.Find(delegate(RegisterValue v) { return v.Offset == offset; });
                if (r != null && r.Error == null && r.Value != 0xffffffff && !InvalidRegister(r.Value))
                    result.Code = "PLM_REOPEN_REQUIRED";
            }
            try
            {
                uint remote = (s.Register(0x8c1c0) >> 20) & 15, vsec = s.Register(0x8860c);
                if (result.Code != "PLM_REOPEN_REQUIRED" &&
                    (!InvalidRegister(vsec) && (vsec & 1) == 0 || remote == 3)) result.Code = "SBR_RESAMPLE_REQUIRED";
            }
            catch { }
            result.Detail = detail;
        }
    }
}
