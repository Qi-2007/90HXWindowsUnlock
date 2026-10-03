using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Runtime.InteropServices;

namespace CMP90HX
{
    sealed class MockHardware : IHardware
    {
        readonly Dictionary<ulong, byte> pci = new Dictionary<ulong, byte>();
        readonly Dictionary<ulong, uint> mmio = new Dictionary<ulong, uint>();
        public bool CompleteRetrain = true, LoseWidth, IgnoreMmioWrites;
        public bool RejectGpuV2Reads, RejectRootV2Reads;
        public int PciWrites, MmioWrites, PciReads, MmioReads;
        public const uint Gpu = 0x100, Root = 0x008;
        public const ulong Bar = 0x100000000;

        public MockHardware()
        {
            SetPci(Gpu, 0, Gen2Engine.TargetId, 4); SetPci(Gpu, 4, 6, 2);
            SetPci(Gpu, 0x0e, 0, 1); SetPci(Gpu, 0x10, 4, 4); SetPci(Gpu, 0x14, 1, 4);
            SetPci(Root, 0, 0x12348086, 4); SetPci(Root, 8, 0x06040000, 4);
            SetPci(Root, 4, 6, 2);
            SetPci(Root, 0x0e, 1, 1); SetPci(Root, 0x18, 0x00010100, 4);
            SetupCap(Gpu, 0x60); SetupCap(Root, 0x70);
            SetPci(Gpu, 0x6c, 4, 4); SetPci(Gpu, 0x8c, 2, 4);
            SetPci(Root, 0x7c, 4, 4); SetPci(Root, 0x9c, 2, 4);
            SetPci(Gpu, 0x70, 0, 2); SetPci(Root, 0x80, 0, 2);
            SetPci(Gpu, 0x72, 0x101, 2); SetPci(Root, 0x82, 0x101, 2);
            SetPci(Gpu, 0x90, 1, 2); SetPci(Root, 0xa0, 1, 2);
            mmio[Bar] = 0x17200000;
            foreach (uint p in Gen2Engine.PlmOffsets) mmio[Bar + p] = 0xffffffff;
            foreach (RegisterRule r in Gen2Engine.Rules) mmio[Bar + r.Offset] = 0xa50c780d;
            mmio[Bar + 0x8860c] = 1; mmio[Bar + 0x88084] = 2; mmio[Bar + 0x880a4] = 2;
            mmio[Bar + 0x82381c] = 0x88888888; mmio[Bar + 0x823820] = 8; mmio[Bar + 0x823830] = 4;
            mmio[Bar + 0x118128] = 0x8b8f; mmio[Bar + 0x118234] = 0x3ff; mmio[Bar + 0x100ce0] = 0x288;
        }
        void SetupCap(uint bdf, uint cap)
        { SetPci(bdf, 0x34, cap, 1); SetPci(bdf, cap, 0x00020010, 4); }
        ulong Key(uint bdf, uint off) { return ((ulong)bdf << 32) | off; }
        public void SetPci(uint bdf, uint off, uint value, int size)
        { for (int i = 0; i < size; i++) pci[Key(bdf, off + (uint)i)] = unchecked((byte)(value >> (8 * i))); }
        public uint ReadPci(uint bdf, uint off, int size)
        {
            PciReads++;
            if ((RejectGpuV2Reads && bdf==Gpu && (off==0x8c || off==0x90)) ||
                (RejectRootV2Reads && bdf==Root && (off==0x9c || off==0xa0)))
                throw new IOException("Read of v2-only register on v1 mock device.");
            if (off == 0 && !pci.ContainsKey(Key(bdf, 0))) return 0xffffffff;
            uint value = 0; byte part;
            for (int i = 0; i < size; i++) if (pci.TryGetValue(Key(bdf, off + (uint)i), out part)) value |= (uint)part << (8 * i);
            return value;
        }
        public void WritePci(uint bdf, uint off, uint value, int size)
        {
            PciWrites++; SetPci(bdf, off, value, size);
            if (bdf == Root && off == 0x80 && (value & 0x20) != 0 && CompleteRetrain)
            {
                uint width = LoseWidth ? 8U : 16U;
                SetPci(Gpu, 0x72, (width << 4) | 2, 2); SetPci(Root, 0x82, (width << 4) | 2, 2);
            }
        }
        public uint ReadMmio(ulong address) { MmioReads++; uint value; return mmio.TryGetValue(address, out value) ? value : 0xffffffff; }
        public void WriteMmio(ulong address, uint value) { MmioWrites++; if (!IgnoreMmioWrites) mmio[address] = value; }
        public void Delay(int milliseconds) { }
        public void Dispose() { }
        public void SetGen2() { SetPci(Gpu, 0x72, 0x102, 2); SetPci(Root, 0x82, 0x102, 2); }
        public void ClosePlm() { mmio[Bar + Gen2Engine.PlmOffsets[0]] = 0; }
        public void LoseCoreUnlock() { mmio[Bar + 0x823830] = 0; }
    }

    sealed class TraceHardware : IHardware
    {
        internal readonly MockHardware Inner=new MockHardware();
        internal readonly List<int> Delays=new List<int>();
        internal readonly List<uint> BridgeControls=new List<uint>();
        internal bool ThrowAfterAssert, IgnoreAssert;
        internal int ElapsedMs, ConfigReadyAfterMs, BootReadyAfterMs, BreakBootSample, BootstrapReadyAfterMs;
        internal bool WrongId, IgnoreBarRestore;
        bool released;
        int releaseMs, bootSamples;
        public uint ReadPci(uint b,uint o,int s) {
            if (released && b==MockHardware.Gpu && o==0 && (WrongId || ElapsedMs-releaseMs<ConfigReadyAfterMs)) return 0xffffffff;
            return Inner.ReadPci(b,o,s);
        }
        public void WritePci(uint b,uint o,uint v,int s) {
            if (!(IgnoreAssert && b==MockHardware.Root && o==0x3e && (v&0x40)!=0) &&
                !(IgnoreBarRestore && b==MockHardware.Gpu && o==0x10)) Inner.WritePci(b,o,v,s);
            if (b==MockHardware.Root && o==0x3e) {
                BridgeControls.Add(v);
                if ((v&0x40)!=0) { released=false; bootSamples=0; }
                else { released=true; releaseMs=ElapsedMs; }
                if (ThrowAfterAssert && (v&0x40)!=0) throw new IOException("Simulated assert failure.");
            }
        }
        public uint ReadMmio(ulong a) {
            if(released && a==MockHardware.Bar+0x118234 && ElapsedMs-releaseMs<BootstrapReadyAfterMs) return 0;
            if (released && a==MockHardware.Bar) {
                bootSamples++;
                if (ElapsedMs-releaseMs<BootReadyAfterMs || bootSamples==BreakBootSample) return 0xbadf5620;
            }
            return Inner.ReadMmio(a);
        }
        public void WriteMmio(ulong a,uint v) { Inner.WriteMmio(a,v); }
        public void Delay(int ms) { Delays.Add(ms); ElapsedMs+=ms; }
        public void Dispose() { Inner.Dispose(); }
    }
    sealed class EcamMock : IHardware
    {
        internal readonly MockHardware Inner=new MockHardware();
        internal readonly Dictionary<ulong,uint> Words=new Dictionary<ulong,uint>();
        internal const ulong Physical=0xe0008000;
        internal int Writes;
        internal uint Last;
        internal EcamMock() {
            for (uint o=0;o<256;o+=4) Words[Physical+o]=Inner.ReadPci(MockHardware.Root,o,4);
        }
        public uint ReadPci(uint b,uint o,int s) { return Inner.ReadPci(b,o,s); }
        public void WritePci(uint b,uint o,uint v,int s) { throw new IOException("HAL bridge write forbidden in test."); }
        public uint ReadMmio(ulong a) { uint v; return Words.TryGetValue(a,out v)?v:Inner.ReadMmio(a); }
        public void WriteMmio(ulong a,uint v) {
            if (!Words.ContainsKey(a)) throw new IOException("ECAM test write outside bridge page.");
            Writes++; Last=v; uint offset=checked((uint)(a-Physical));
            if (offset==0x80 || offset==0xa0) v=(v&65535)|((Words[a]&0xffff0000)&~(v&0xffff0000));
            Words[a]=v; Inner.SetPci(MockHardware.Root,offset,v,4);
        }
        public void Delay(int ms) { }
        public void Dispose() { }
    }
    sealed class GpuDmaMock : IHardware
    {
        internal readonly MockHardware Inner=new MockHardware();
        internal readonly Dictionary<uint,uint> Regs=new Dictionary<uint,uint>();
        internal readonly uint[] Dmem=new uint[64];
        internal readonly Dictionary<ulong,IntPtr> Buffers=new Dictionary<ulong,IntPtr>();
        internal bool BlockPio,BlockInbound,BlockOutbound,StallDma,AllowReset,FailResetAssert;
        internal bool ResetRestoresPio=true;
        internal bool ResetSelectsRiscv;
        internal int EngineResets;
        internal int Commands,Writes,Freed;
        uint cursor;
        internal GpuDmaMock() {
            foreach(uint offset in new uint[]{0x10c,0x110,0x114,0x11c,0x128,0x600,0x624,0x1c0}) Regs[offset]=0;
            Regs[0x100]=0x10; Regs[0x1668]=1; Regs[0x118]=2;
            Regs[0xf4]=0x80000000; Regs[0x3c0]=0;
            for(int i=0;i<64;i++) Dmem[i]=0x12340000U+(uint)i;
        }
        internal ulong Allocate(ulong size,ulong output) {
            ulong phys=0x20000000UL+(ulong)Buffers.Count*4096;
            IntPtr p=Marshal.AllocHGlobal((int)size); Buffers.Add(phys,p);
            Marshal.WriteInt64(new IntPtr((long)output),(long)phys); return (ulong)p.ToInt64();
        }
        internal void Free(ulong address,ulong size,ulong physical) {
            if(!Buffers.ContainsKey(physical) || (ulong)Buffers[physical].ToInt64()!=address) throw new IOException("Bad probe free.");
            Marshal.FreeHGlobal(Buffers[physical]); Buffers.Remove(physical); Freed++;
        }
        public uint ReadPci(uint b,uint o,int s) { return Inner.ReadPci(b,o,s); }
        public void WritePci(uint b,uint o,uint v,int s) { Inner.WritePci(b,o,v,s); }
        public uint ReadMmio(ulong address) {
            foreach(KeyValuePair<ulong,IntPtr> buffer in Buffers)
                if(address>=buffer.Key && address<buffer.Key+4096) return unchecked((uint)Marshal.ReadInt32(buffer.Value,(int)(address-buffer.Key)));
            ulong start=MockHardware.Bar+GpuDmaProbe.Gsp;
            if(address<start || address>start+0x2000) return Inner.ReadMmio(address);
            uint offset=(uint)(address-start), value;
            if(offset==0x1c4) { value=Dmem[cursor/4]; if((Regs[0x1c0]&(1U<<25))!=0) cursor=(cursor+4)%256; return value; }
            return Regs.TryGetValue(offset,out value)?value:0;
        }
        public void WriteMmio(ulong address,uint value) {
            ulong start=MockHardware.Bar+GpuDmaProbe.Gsp;
            if(address<start || address>start+0x2000) throw new IOException("Probe wrote outside GSP.");
            uint offset=(uint)(address-start); Writes++;
            if(offset==0x1c0) { Regs[offset]=value; cursor=value&0xffffff; return; }
            if(offset==0x1c4) { if(!BlockPio) Dmem[cursor/4]=value; if((Regs[0x1c0]&(1U<<24))!=0) cursor=(cursor+4)%256; return; }
            if(offset==0x100 || (!AllowReset && (offset==0x1668 || offset==0x3c0))) throw new IOException("Probe must never start/reset/switch a CPU.");
            if(offset==0x3c0) {
                Regs[offset]=value;
                if((value&1)!=0 && FailResetAssert) throw new IOException("Simulated failure after GSP reset assertion.");
                if((value&1)==0) {
                    EngineResets++; if(ResetRestoresPio) BlockPio=false;
                    Array.Clear(Dmem,0,Dmem.Length); cursor=0;
                    Regs[0x1c0]=0; Regs[0x10c]=0; Regs[0x118]=2;
                    if(ResetSelectsRiscv) Regs[0x1668]=0x111;
                }
                return;
            }
            Regs[offset]=value;
            if(offset!=0x118) return;
            Commands++; if(StallDma) { Regs[offset]=1; return; }
            ulong phys=((ulong)Regs[0x110]<<8)+Regs[0x11c]; IntPtr buffer=Buffers[phys];
            if((value&0x20)==0) {
                if(!BlockInbound) for(int i=0;i<64;i++) Dmem[i]=unchecked((uint)Marshal.ReadInt32(buffer,i*4));
            } else if(!BlockOutbound) for(int i=0;i<64;i++) Marshal.WriteInt32(buffer,i*4,unchecked((int)Dmem[i]));
            Regs[offset]=2;
        }
        public void Delay(int milliseconds) { if(StallDma) System.Threading.Thread.Sleep(milliseconds); }
        public void Dispose() { foreach(IntPtr p in Buffers.Values) Marshal.FreeHGlobal(p); Buffers.Clear(); }
    }
    public static class SelfTests
    {
        static int failures;
        static void Check(bool value, string name, TextWriter output)
        { output.WriteLine((value ? "PASS " : "FAIL ") + name); if (!value) failures++; }
        static byte[] Mcfg(ulong physical,byte first,byte last,int entries)
        {
            byte[] table=new byte[44+16*entries];
            Buffer.BlockCopy(BitConverter.GetBytes(0x4746434dU),0,table,0,4);
            Buffer.BlockCopy(BitConverter.GetBytes((uint)table.Length),0,table,4,4);
            for (int i=44;i<table.Length;i+=16) {
                Buffer.BlockCopy(BitConverter.GetBytes(physical),0,table,i,8); table[i+10]=first; table[i+11]=last;
            }
            McfgChecksum(table); return table;
        }
        static void McfgChecksum(byte[] table) {
            table[9]=0; uint sum=0; foreach(byte b in table) sum+=b; table[9]=unchecked((byte)(0U-sum));
        }
        static void HostOptimizationTests(TextWriter output)
        {
            RegisterIoPacket packet=RegisterIoPacket.Current;
            bool widths=true;
            foreach(int width in new int[]{1,2,4}) {
                packet.Pci(0x1234,0x40,width,true,0xfedcba98);
                widths &= packet.InputLength==16 && packet.OutputLength==0 &&
                    BitConverter.ToUInt32(packet.Input,0)==0x1234 && BitConverter.ToUInt32(packet.Input,4)==0x40 &&
                    BitConverter.ToUInt32(packet.Input,8)==width && BitConverter.ToUInt32(packet.Input,12)==0xfedcba98;
                for(int i=0;i<4;i++) packet.Output[i]=0xff;
                packet.Pci(0x1234,0x40,width,false,0);
                widths &= packet.InputLength==16 && packet.OutputLength==4 &&
                    packet.Value==(width==4?0xffffffffU:((1U<<(width*8))-1));
            }
            packet.Physical(0x123456789abcUL,true,0x98765432);
            bool physical=packet.InputLength==16 && packet.OutputLength==0 &&
                BitConverter.ToUInt64(packet.Input,0)==0x123456789abcUL && BitConverter.ToUInt32(packet.Input,8)==0x98765432;
            packet.Physical(0x123456789abcUL,false,0);
            Check(widths && physical && packet.InputLength==16 && packet.OutputLength==4,
                "reused IOCTL packets preserve exact PCI widths, physical addresses and transfer lengths",output);
            RegisterIoPacket other=null;
            System.Threading.Thread thread=new System.Threading.Thread(delegate() { other=RegisterIoPacket.Current; });
            thread.Start(); thread.Join();
            Check(Object.ReferenceEquals(packet,RegisterIoPacket.Current) && !Object.ReferenceEquals(packet,other) &&
                !Object.ReferenceEquals(packet.Input,other.Input),"IOCTL buffers reuse within a thread and stay isolated across threads",output);

            const int size=1024*1024+3;
            IntPtr source=Marshal.AllocHGlobal(size+2), destination=Marshal.AllocHGlobal(size+2);
            try {
                ulong src=(ulong)source.ToInt64(), dst=(ulong)destination.ToInt64();
                byte[] pattern=new byte[size], actual=new byte[size];
                for(int i=0;i<size;i++) pattern[i]=unchecked((byte)(i*17+5));
                NativeBufferMemory.Fill(dst,0x7b,(ulong)(size+2));
                Marshal.Copy(pattern,0,IntPtr.Add(source,1),size);
                bool returned=NativeBufferMemory.Copy(dst+1,src+1,(ulong)size)==dst+1;
                Marshal.Copy(IntPtr.Add(destination,1),actual,0,size);
                bool same=true; for(int i=0;i<size;i++) if(actual[i]!=pattern[i]) { same=false; break; }
                Check(returned && same && Marshal.ReadByte(destination)==0x7b && Marshal.ReadByte(destination,size+1)==0x7b,
                    "native copy preserves unaligned megabyte payload and both boundary sentinels",output);
                NativeBufferMemory.Fill(dst+1,0,(ulong)size);
                Marshal.Copy(IntPtr.Add(destination,1),actual,0,size);
                bool zero=true; foreach(byte value in actual) if(value!=0) { zero=false; break; }
                Check(zero && Marshal.ReadByte(destination)==0x7b && Marshal.ReadByte(destination,size+1)==0x7b,
                    "native zeroing clears the full allocation without changing boundary sentinels",output);
                for(int i=0;i<64;i++) Marshal.WriteByte(destination,i,(byte)i);
                NativeBufferMemory.Copy(dst+3,dst,40);
                bool overlap=true; for(int i=0;i<40;i++) overlap &= Marshal.ReadByte(destination,i+3)==i;
                NativeBufferMemory.Copy(dst,dst+3,40);
                for(int i=0;i<40;i++) overlap &= Marshal.ReadByte(destination,i)==i;
                Check(overlap,"native copy retains staging-copy semantics for overlapping ranges in both directions",output);
                bool nullRefused=false, sizeRefused=false, rangeRefused=false;
                try { NativeBufferMemory.Copy(dst,0,1); } catch(IOException) { nullRefused=true; }
                try { NativeBufferMemory.Fill(dst,0,32UL*1024*1024+1); } catch(IOException) { sizeRefused=true; }
                try { NativeBufferMemory.Fill((ulong)Int64.MaxValue,0,2); } catch(IOException) { rangeRefused=true; }
                Check(NativeBufferMemory.Copy(0,0,0)==0 && NativeBufferMemory.Fill(0,0,0)==0 &&
                    nullRefused && sizeRefused && rangeRefused,"native memory rejects invalid ranges before access and allows zero-length operations",output);
            } finally { Marshal.FreeHGlobal(source); Marshal.FreeHGlobal(destination); }

            using(MockHardware io=new MockHardware()) {
                Gen2Engine engine=new Gen2Engine(io,delegate(string _) {},true);
                io.SetPci(MockHardware.Gpu,0x62,1,2); io.RejectGpuV2Reads=true;
                Snapshot before=engine.Capture(MockHardware.Gpu); int reads=io.PciReads;
                io.SetPci(MockHardware.Gpu,0x62,2,2); io.RejectGpuV2Reads=false;
                io.SetGen2(); io.LoseCoreUnlock();
                Snapshot after=engine.Capture(MockHardware.Gpu);
                Check(reads>=8192 && io.PciReads-reads<100 && before.Gpu.CapabilityVersion==1 &&
                    after.Gpu.CapabilityVersion==2 && after.Gpu.Speed==2 && !Gen2Engine.CoreUnlockValid(after) &&
                    io.PciWrites==0 && io.MmioWrites==0,
                    "session topology avoids repeat bus scans but rereads capabilities, link and core registers",output);
                output.WriteLine("HOST_SCAN_COUNTS initial="+reads+" repeated="+(io.PciReads-reads));
            }
            Action<MockHardware>[] mutations={
                delegate(MockHardware io) { io.SetPci(MockHardware.Root,0,0x56788086,4); },
                delegate(MockHardware io) { io.SetPci(MockHardware.Root,0x18,0x00020200,4); },
                delegate(MockHardware io) { io.SetPci(MockHardware.Root,0x0e,0,1); },
                delegate(MockHardware io) { io.SetPci(MockHardware.Root,8,0x03000000,4); },
                delegate(MockHardware io) { io.SetPci(MockHardware.Gpu,0,0xffffffff,4); },
                delegate(MockHardware io) { io.SetPci(MockHardware.Gpu,0x14,2,4); },
                delegate(MockHardware io) { io.WriteMmio(MockHardware.Bar,0x17200001); }
            };
            bool allRefused=true;
            foreach(Action<MockHardware> change in mutations) using(MockHardware io=new MockHardware()) {
                Gen2Engine engine=new Gen2Engine(io,delegate(string _) {},true); engine.Capture(MockHardware.Gpu);
                change(io); int writes=io.MmioWrites; bool refused=false;
                try { engine.Capture(MockHardware.Gpu); } catch(IOException) { refused=true; }
                allRefused &= refused && io.PciWrites==0 && io.MmioWrites==writes;
            }
            Check(allRefused,"session topology fails closed on bridge identity/type/routing, GPU identity, BAR or BOOT0 changes",output);
            using(MockHardware io=new MockHardware()) {
                Gen2Engine engine=new Gen2Engine(io,delegate(string _) {}); engine.Capture(null);
                int reads=io.PciReads; engine.Capture(null);
                Check(io.PciReads-reads>=8192,"standalone snapshots still perform full topology discovery",output);
            }
            using(MockHardware io=new MockHardware()) {
                io.SetPci(0x108,0,0x12348086,4); io.SetPci(0x108,0x0e,0x80,1);
                io.SetPci(0x10a,0,0x12348086,4); io.SetPci(0x200,0,0x12348086,4);
                List<uint> devices=new Gen2Engine(io,delegate(string _) {}).Enumerate(1,2);
                Check(devices.Count==4 && devices.Contains(0x108) && devices.Contains(0x10a) && devices.Contains(0x200) &&
                    !devices.Contains(MockHardware.Root) && io.PciReads<100,
                    "scoped SBR subtree scan still finds sibling functions and subordinate buses",output);
            }
            using(MockHardware io=new MockHardware()) {
                Gen2Engine engine=new Gen2Engine(io,delegate(string _) {},true);
                Snapshot before=engine.Capture(MockHardware.Gpu);
                RecoveryResult result=engine.Recover(true,MockHardware.Gpu,before);
                Check(result.Success && result.After.Gpu.Speed==2 && result.After.Gpu.Width==16,
                    "session topology supports policy restoration, retraining and fresh final verification",output);
            }
            using(MockHardware io=new MockHardware()) {
                Gen2Engine engine=new Gen2Engine(io,delegate(string _) {},true);
                Snapshot before=engine.Capture(MockHardware.Gpu); io.LoseWidth=true;
                Check(!engine.Recover(true,MockHardware.Gpu,before).Success,
                    "session topology cannot hide negotiated width loss after retraining",output);
            }
        }
        public static int Run(TextWriter output)
        {
            failures = 0;
            HostOptimizationTests(output);
            using(GpuDmaMock io=new GpuDmaMock()) {
                Snapshot s=new Gen2Engine(io,delegate(string _) {}).Capture(null);
                uint[] original=(uint[])io.Dmem.Clone();
                new GpuDmaProbe(io,s,io.Allocate,io.Free,delegate(string _) {}).Run();
                Check(io.Commands==2 && io.Freed==2 && io.Buffers.Count==0 &&
                    io.Regs[0x100]==0x10 && io.Regs[0x1668]==1 &&
                    String.Join(",",original)==String.Join(",",io.Dmem) && io.Regs[0x110]==0 && io.Regs[0x600]==0 && io.Regs[0x1c0]==0,
                    "GPU DMA probe models both directions and restores DMEM/config without CPU start/reset",output);
            }
            using(GpuDmaMock io=new GpuDmaMock()) {
                Snapshot s=new Gen2Engine(io,delegate(string _) {}).Capture(null); io.Regs[0x100]=0;
                bool refused=false; try { new GpuDmaProbe(io,s,io.Allocate,io.Free,delegate(string _) {}).Run(); }
                catch(IOException e) { refused=e.Message.Contains("UNAVAILABLE"); }
                Check(refused && io.Writes==0,"running GSP is refused before any probe write",output);
            }
            using(GpuDmaMock io=new GpuDmaMock()) {
                Snapshot s=new Gen2Engine(io,delegate(string _) {}).Capture(null); io.Regs[0x1c0]=0xbadf5620;
                bool refused=false; try { new GpuDmaProbe(io,s,io.Allocate,io.Free,delegate(string _) {}).Run(); }
                catch(IOException e) { refused=e.Message.Contains("UNAVAILABLE"); }
                Check(refused && io.Commands==0 && io.Writes==0,"inaccessible GSP gate is not mislabeled a DMA failure",output);
            }
            using(GpuDmaMock io=new GpuDmaMock()) {
                Snapshot s=new Gen2Engine(io,delegate(string _) {}).Capture(null); io.BlockPio=true;
                bool refused=false; try { new GpuDmaProbe(io,s,io.Allocate,io.Free,delegate(string _) {}).Run(); }
                catch(IOException e) { refused=e.Message.Contains("HOST_WRITE_BLOCKED"); }
                Check(refused && io.Commands==0 && io.Buffers.Count==0,"locked DMEM PIO is detected before issuing GPU DMA",output);
            }
            using(GpuDmaMock io=new GpuDmaMock()) {
                Snapshot s=new Gen2Engine(io,delegate(string _) {}).Capture(null); io.BlockInbound=true;
                bool refused=false; try { new GpuDmaProbe(io,s,io.Allocate,io.Free,delegate(string _) {}).Run(); }
                catch(IOException e) { refused=e.Message.Contains("SYSMEM_TO_GSP_MISMATCH"); }
                Check(refused && io.Commands==2 && io.Freed==2,"DMA idle alone is not success; independent outbound cannot hide inbound mismatch",output);
            }
            using(GpuDmaMock io=new GpuDmaMock()) {
                Snapshot s=new Gen2Engine(io,delegate(string _) {}).Capture(null); io.BlockOutbound=true;
                bool refused=false; try { new GpuDmaProbe(io,s,io.Allocate,io.Free,delegate(string _) {}).Run(); }
                catch(IOException e) { refused=e.Message.Contains("GSP_TO_SYSMEM_MISMATCH"); }
                Check(refused && io.Commands==2 && io.Freed==2,"DMA writeback must replace every destination sentinel",output);
            }
            using(GpuDmaMock io=new GpuDmaMock()) {
                Snapshot s=new Gen2Engine(io,delegate(string _) {}).Capture(null);
                io.Inner.SetPci(MockHardware.Root,4,2,2);
                bool refused=false; try { new GpuDmaProbe(io,s,io.Allocate,io.Free,delegate(string _) {}).Run(); }
                catch(IOException e) { refused=e.Message.Contains("UPSTREAM_BME_DISABLED"); }
                Check(refused && io.Commands==0 && io.Writes==0,"upstream Bus Master gate fails without writing bridge or GSP",output);
            }
            using(GpuDmaMock io=new GpuDmaMock()) {
                Snapshot s=new Gen2Engine(io,delegate(string _) {}).Capture(null); io.BlockInbound=true; io.BlockOutbound=true;
                List<string> lines=new List<string>(); uint[] original=(uint[])io.Dmem.Clone();
                bool refused=false; try { new GpuDmaProbe(io,s,io.Allocate,io.Free,lines.Add).Run(); }
                catch(IOException e) { refused=e.Message.Contains("SYSMEM_TO_GSP_MISMATCH"); }
                string transcript=String.Join("\n",lines);
                Check(refused && io.Commands==2 && io.Freed==2 &&
                    transcript.Contains("GPU_DMA_DIRECTION_RESULT IN=FAIL OUT=FAIL") &&
                    transcript.Contains("unchanged=64/64") && transcript.Contains("first=0x00000002") &&
                    String.Join(",",original)==String.Join(",",io.Dmem),
                    "dropped commands report both payload failures and raw idle status, restoring original DMEM",output);
            }
            using(GpuDmaMock io=new GpuDmaMock()) {
                Snapshot s=new Gen2Engine(io,delegate(string _) {}).Capture(null); io.StallDma=true;
                bool refused=false; try { new GpuDmaProbe(io,s,io.Allocate,io.Free,delegate(string _) {}).Run(); }
                catch(IOException e) { refused=e.Message.Contains("RESTORE_UNCONFIRMED"); }
                Check(refused && io.Commands==1 && io.Freed==0 && io.Buffers.Count==2 && io.Regs[0x110]!=0,
                    "pending DMA does not free/reuse buffers or redirect saved DMA addresses",output);
            }
            byte[] mcfg=Mcfg(0xe0000000,0,255,1);
            Check(BridgeEcam.Resolve(mcfg,0x0030)==0xe0030000UL &&
                BridgeEcam.Resolve(Mcfg(0xe0000000,0x40,0x7f,1),0x4209)==0xe4209000UL,
                "MCFG ECAM resolves BDF using bus-zero base, including nonzero start bus",output);
            bool rejectedMcfg=false;
            byte[] broken=(byte[])mcfg.Clone(); broken[16]^=1;
            try { BridgeEcam.Resolve(broken,0x0030); } catch(IOException) { rejectedMcfg=true; }
            Check(rejectedMcfg,"MCFG checksum corruption rejected",output);
            rejectedMcfg=false; try { BridgeEcam.Resolve(Mcfg(0xe0000000,0,255,2),0x0030); } catch(IOException) { rejectedMcfg=true; }
            Check(rejectedMcfg,"overlapping MCFG matches rejected",output);
            rejectedMcfg=false; try { BridgeEcam.Resolve(Mcfg(0xe0000000,1,255,1),0x0030); } catch(IOException) { rejectedMcfg=true; }
            Check(rejectedMcfg,"MCFG without selected segment/bus rejected",output);
            rejectedMcfg=false; try { BridgeEcam.Resolve(Mcfg(0xe0000001,0,255,1),0x0030); } catch(IOException) { rejectedMcfg=true; }
            Check(rejectedMcfg,"unaligned MCFG base rejected",output);
            Check(BridgeEcam.Compose(0x40,0x3e,2,0x00011234,0x0041)==0x00411234 &&
                BridgeEcam.Compose(0x40,0x3e,2,0x04011234,0x0441)==0x00411234 &&
                BridgeEcam.Compose(0x40,0x50,2,0xa0010103,0x0100)==0x0100 &&
                BridgeEcam.Compose(0x40,0x70,2,0xffff0081,0x0082)==0x0082,
                "ECAM SBR preserves Interrupt Line/Pin; link writes zero adjacent W1C status",output);
            bool rejectedWrite=false; try { BridgeEcam.Compose(0x40,0x3e,2,0,0x80); } catch(IOException) { rejectedWrite=true; }
            Check(rejectedWrite,"ECAM rejects changes outside SBR bit",output);
            rejectedWrite=false; try { BridgeEcam.Compose(0x40,0x18,4,0,1); } catch(IOException) { rejectedWrite=true; }
            Check(rejectedWrite,"ECAM rejects writes to bridge routing/BARs or non-word widths",output);
            using (EcamMock io=new EcamMock()) {
                Snapshot s=new Gen2Engine(io,delegate(string _) {}).Capture(null);
                BridgeEcam ecam=new BridgeEcam(io,mcfg,s,delegate(string _) {});
                Check(io.Writes==0,"bridge ECAM preflight compares HAL identity/config with no writes",output);
                io.Words[EcamMock.Physical+0x3c]=0x1234; io.Inner.SetPci(MockHardware.Root,0x3c,0x1234,4);
                ecam.Write(0x3e,0x40,2); ecam.Write(0x3e,0,2);
                Check(io.Writes==2 && io.Last==0x1234 && io.ReadPci(MockHardware.Root,0x3e,2)==0,
                    "bridge ECAM assert/release works without HAL writes",output);
                io.Words[EcamMock.Physical+0x80]=0xa0010003; io.Inner.SetPci(MockHardware.Root,0x80,0xa0010003,4);
                ecam.Write(0x80,0,2);
                Check(io.Last==0 && io.Words[EcamMock.Physical+0x80]==0xa0010000,
                    "bridge ECAM link write does not acknowledge W1C link status",output);
            }
            using (EcamMock io=new EcamMock()) {
                Snapshot s=new Gen2Engine(io,delegate(string _) {}).Capture(null); io.Words[EcamMock.Physical]=0xffffffff;
                bool mismatch=false; try { new BridgeEcam(io,mcfg,s,delegate(string _) {}); } catch(IOException) { mismatch=true; }
                Check(mismatch && io.Writes==0,"wrong ECAM identity refused before any physical write",output);
            }
            using (TraceHardware io=new TraceHardware()) {
                Snapshot s=new Gen2Engine(io,delegate(string _) {}).Capture(null); io.IgnoreAssert=true;
                bool mismatch=false; try { EfiResetSequence.Once(io,s,new uint[6],6,delegate(string _) {}); } catch(IOException) { mismatch=true; }
                Check(mismatch && io.Delays.Count==0 && String.Join(",",io.BridgeControls)=="64,0",
                    "SBR requires assert readback and still attempts release on failure",output);
            }
            using (MockHardware io=new MockHardware())
            {
                io.SetPci(MockHardware.Gpu,0x62,1,2); io.RejectGpuV2Reads=true;
                Snapshot s=new Gen2Engine(io,delegate(string _) {}).Capture(null);
                Check(s.Gpu.CapabilityVersion==1 && !s.Gpu.HasLinkControl2 && s.Gpu.Speed==1 &&
                    s.Gpu.Width==16 && s.Bridge.HasLinkControl2 && io.PciWrites==0 && io.MmioWrites==0,
                    "cold v1 GPU snapshot reads only defined registers, with no writes",output);
                RecoveryResult r=new Gen2Engine(io,delegate(string _) {}).Recover(true,null,null);
                Check(r.Code=="PCIE_V2_NOT_AVAILABLE" && r.PciWrites==0 && r.MmioWrites==0,
                    "v1 GPU snapshot does not authorize LNKCTL2 writes",output);
            }
            using (MockHardware io=new MockHardware())
            {
                io.SetPci(MockHardware.Root,0x72,1,2); io.RejectRootV2Reads=true;
                Snapshot s=new Gen2Engine(io,delegate(string _) {}).Capture(null);
                Check(s.Gpu.HasLinkControl2 && s.Bridge.CapabilityVersion==1 && !s.Bridge.HasLinkControl2,
                    "v1 bridge snapshot also avoids v2-only register reads",output);
            }
            using (MockHardware io=new MockHardware())
            {
                io.SetPci(MockHardware.Gpu,0x62,0,2);
                bool invalid=false; try { new Gen2Engine(io,delegate(string _) {}).Capture(null); } catch (Exception e) { invalid=e.Message.Contains("Invalid PCIe capability"); }
                Check(invalid,"invalid capability version zero is still rejected",output);
            }
            using (TraceHardware io=new TraceHardware())
            {
                Snapshot s=new Gen2Engine(io,delegate(string _) {}).Capture(null);
                uint[] bars=new uint[6]; for (uint i=0;i<6;i++) bars[i]=io.ReadPci(s.GpuBdf,0x10+4*i,4);
                EfiResetSequence.Once(io,s,bars,6,delegate(string _) {});
                EfiResetSequence.Once(io,s,bars,6,delegate(string _) {});
                Check(String.Join(",",io.Delays)=="100,1000,8000,100,1000,8000" &&
                    String.Join(",",io.BridgeControls)=="64,0,64,0" && io.Inner.PciWrites==18,
                    "EFI dual SBR timing and six-BAR/Command restoration mirrored",output);
            }
            using (TraceHardware io=new TraceHardware())
            {
                Snapshot s=new Gen2Engine(io,delegate(string _) {}).Capture(null); io.ThrowAfterAssert=true;
                bool failed=false; try { EfiResetSequence.Once(io,s,new uint[6],6,delegate(string _) {}); } catch (IOException) { failed=true; }
                Check(failed && io.ReadPci(s.BridgeBdf,0x3e,2)==0 && String.Join(",",io.BridgeControls)=="64,0",
                    "SBR reset bit released even when assertion fails",output);
            }
            using (TraceHardware io=new TraceHardware()) {
                Snapshot s=new Gen2Engine(io,delegate(string _) {}).Capture(null);
                uint[] bars=new uint[6]; for(uint i=0;i<6;i++) bars[i]=io.ReadPci(s.GpuBdf,0x10+4*i,4);
                EfiResetSequence.Once(io,s,bars,6,delegate(string _) {},true);
                EfiResetSequence.Once(io,s,bars,6,delegate(string _) {},true);
                Check(io.ElapsedMs==1100 && String.Join(",",io.BridgeControls)=="64,0,64,0" && io.Inner.PciWrites==18,
                    "fast dual SBR keeps both resets and BAR/Command writes, settles in 1.1 s when ready",output);
            }
            using (TraceHardware io=new TraceHardware()) {
                Snapshot s=new Gen2Engine(io,delegate(string _) {}).Capture(null);
                uint[] bars=new uint[6]; for(uint i=0;i<6;i++) bars[i]=io.ReadPci(s.GpuBdf,0x10+4*i,4);
                io.ConfigReadyAfterMs=500; io.BootReadyAfterMs=1000;
                EfiResetSequence.Once(io,s,bars,6,delegate(string _) {},true);
                Check(io.ElapsedMs==1200,"fast SBR waits for delayed config and BAR0 readiness",output);
            }
            using (TraceHardware io=new TraceHardware()) {
                Snapshot s=new Gen2Engine(io,delegate(string _) {}).Capture(null);
                uint[] bars=new uint[6]; for(uint i=0;i<6;i++) bars[i]=io.ReadPci(s.GpuBdf,0x10+4*i,4);
                io.BreakBootSample=2;
                EfiResetSequence.Once(io,s,bars,6,delegate(string _) {},true);
                Check(io.ElapsedMs==650,"unstable BAR0 restarts the three-sample readiness gate",output);
            }
            using (TraceHardware io=new TraceHardware()) {
                Snapshot s=new Gen2Engine(io,delegate(string _) {}).Capture(null); io.WrongId=true;
                bool failed=false; try { EfiResetSequence.Once(io,s,new uint[6],6,delegate(string _) {},true); } catch(IOException) { failed=true; }
                Check(failed && io.ElapsedMs==1100 && io.Inner.PciWrites==2 && io.ReadPci(s.BridgeBdf,0x3e,2)==0,
                    "fast SBR rejects missing identity before BAR writes, releases reset and has a bounded wait",output);
            }
            using (TraceHardware io=new TraceHardware()) {
                Snapshot s=new Gen2Engine(io,delegate(string _) {}).Capture(null);
                io.Inner.SetPci(MockHardware.Root,s.Bridge.CapabilityOffset+0x12,0x901,2);
                bool failed=false; try { EfiResetSequence.Once(io,s,new uint[6],6,delegate(string _) {},true); } catch(IOException) { failed=true; }
                Check(failed && io.Inner.PciWrites==2,"fast SBR refuses a bridge that is still training",output);
            }
            using (TraceHardware io=new TraceHardware()) {
                Snapshot s=new Gen2Engine(io,delegate(string _) {}).Capture(null);
                uint[] bars=new uint[6]; for(uint i=0;i<6;i++) bars[i]=io.ReadPci(s.GpuBdf,0x10+4*i,4);
                io.BootReadyAfterMs=Int32.MaxValue;
                bool failed=false; try { EfiResetSequence.Once(io,s,bars,6,delegate(string _) {},true); } catch(IOException) { failed=true; }
                Check(failed && io.ElapsedMs==8200 && io.BridgeControls.Count==2,
                    "fast SBR BAR0 timeout keeps the 8 s budget without repeating reset",output);
            }
            using (TraceHardware io=new TraceHardware()) {
                Snapshot s=new Gen2Engine(io,delegate(string _) {}).Capture(null);
                uint[] bars=new uint[6]; for(uint i=0;i<6;i++) bars[i]=io.ReadPci(s.GpuBdf,0x10+4*i,4);
                io.Inner.SetPci(s.GpuBdf,0x10,0,4); io.IgnoreBarRestore=true;
                bool failed=false; try { EfiResetSequence.Once(io,s,bars,6,delegate(string _) {},true); } catch(IOException) { failed=true; }
                Check(failed && io.ElapsedMs==8200,"fast SBR never accepts incorrect BAR restoration",output);
            }
            System.Diagnostics.Stopwatch shortTimer=System.Diagnostics.Stopwatch.StartNew();
            using(TraceHardware io=new TraceHardware()) {
                Snapshot s=new Gen2Engine(io,delegate(string _) {}).Capture(null);
                uint[] bars=new uint[6]; for(uint i=0;i<6;i++) bars[i]=io.ReadPci(s.GpuBdf,0x10+4*i,4);
                io.BootstrapReadyAfterMs=500;
                EfiResetSequence.Once(io,s,bars,6,delegate(string _) {},true,true);
                Check(io.ElapsedMs==700,"managed SBR waits for GFW bootstrap even when BAR0 and the link are already readable",output);
            }
            using(MockHardware io=new MockHardware()) {
                Snapshot s=new Gen2Engine(io,delegate(string _) {}).Capture(null);
                io.WriteMmio(s.Bar0+0x118234,0xffffffff);
                Check(!EfiResetSequence.BootstrapReady(io,s),"all-ones GFW progress cannot satisfy completed-state gate",output);
                io.WriteMmio(s.Bar0+0x118234,0x3ff); io.WriteMmio(s.Bar0+0x100ce0,0);
                Check(!EfiResetSequence.BootstrapReady(io,s),"completed GFW with unset local memory range is refused",output);
            }
            shortTimer.Restart();
            for(int i=0;i<16;i++) WindowsHardware.PreciseShortDelay(1);
            Check(shortTimer.Elapsed.TotalMilliseconds>=16,"precise native delays retain requested duration",output);
            bool invalidDelay=false; try { WindowsHardware.PreciseShortDelay(-1); } catch(ArgumentOutOfRangeException) { invalidDelay=true; }
            Check(invalidDelay,"precise delay rejects invalid duration",output);
            byte[] kernelInfo=new byte[40];
            Buffer.BlockCopy(BitConverter.GetBytes(2U),0,kernelInfo,0,4);
            Buffer.BlockCopy(BitConverter.GetBytes(0x20000000UL),0,kernelInfo,8,8);
            Buffer.BlockCopy(BitConverter.GetBytes(32UL*1024*1024),0,kernelInfo,16,8);
            Buffer.BlockCopy(BitConverter.GetBytes(0x100000000UL),0,kernelInfo,24,8);
            Buffer.BlockCopy(BitConverter.GetBytes(1UL),0,kernelInfo,32,8);
            KernelDma.ValidateInfo(kernelInfo,true);
            Check(true,"kernel DMA protocol accepts bounded low physical address and mapped user range",output);
            Buffer.BlockCopy(BitConverter.GetBytes(1U),0,kernelInfo,0,4);
            bool oldProtocol=false; try { KernelDma.ValidateInfo(kernelInfo,false); } catch(IOException) { oldProtocol=true; }
            Check(oldProtocol,"unified hardware transport refuses the old arena-only driver",output);
            Buffer.BlockCopy(BitConverter.GetBytes(2U),0,kernelInfo,0,4);
            for(uint state=1;state<=2;state++) {
                Buffer.BlockCopy(BitConverter.GetBytes(state),0,kernelInfo,4,4);
                bool blocked=false; try { KernelDma.ValidateInfo(kernelInfo,false); } catch(RetainedDmaException) { blocked=true; }
                Check(blocked,"kernel armed/quarantined state refuses reuse even without a mapping",output);
            }
            Buffer.BlockCopy(BitConverter.GetBytes(0U),0,kernelInfo,4,4);
            Buffer.BlockCopy(BitConverter.GetBytes(0xfffff000UL),0,kernelInfo,8,8);
            bool invalidKernelRange=false; try { KernelDma.ValidateInfo(kernelInfo,true); } catch(IOException) { invalidKernelRange=true; }
            Check(invalidKernelRange,"kernel DMA rejects physical range crossing 4 GiB",output);
            using (SysV abi = new SysV())
            {
                ulong reverse = abi.Reverse(delegate(ulong a, ulong b, ulong c, ulong d, ulong e, ulong f, ulong stack) {
                    return a + 2*b + 3*c + 5*d + 7*e + 11*f;
                });
                SysV.Function forward = abi.Forward(reverse);
                Check(forward(13,17,19,23,29,31) == 13UL+2*17+3*19+5*23+7*29+11*31,
                    "native SysV/Windows ABI round-trip (six distinct arguments)", output);
                GC.Collect(); GC.WaitForPendingFinalizers();
                Check(forward(1,2,3,4,5,6) == 135, "native callback remains rooted across GC", output);
            }
            System.ComponentModel.Win32Exception invalidPath = Native.Failure(123, "StartService test");
            Check(invalidPath.NativeErrorCode == 123 && invalidPath.Message.Contains("Win32=123") &&
                invalidPath.Message.Contains("0x0000007b"), "Win32 errors include native code and system message", output);
            RegisterRule rule = Gen2Engine.Rules[1];
            Check(rule.Apply(0xa5a5f5a5) == ((0xa5a5f5a5U & ~0x7800U) | 0x2800U), "RMW preserves unrelated bits", output);
            using (MockHardware io = new MockHardware())
            {
                RecoveryResult r = new Gen2Engine(io, delegate(string _) { }).Recover(true, null, null);
                Check(r.Code == "GEN2_VERIFIED", "Gen1 policy restore and retrain", output);
                Check(r.After.Gpu.Width == 16 && r.After.Bridge.Width == 16, "width retained", output);
                Check(r.MmioWrites == 5, "five GA102 policy writes", output);
            }
            using (MockHardware io = new MockHardware())
            {
                io.SetGen2(); RecoveryResult r = new Gen2Engine(io, delegate(string _) { }).Recover(true, null, null);
                Check(r.Code == "ALREADY_GEN2" && r.MmioWrites == 0 && r.PciWrites == 0, "already Gen2 is read-only", output);
            }
            using (MockHardware io = new MockHardware())
            {
                io.CompleteRetrain = false; RecoveryResult r = new Gen2Engine(io, delegate(string _) { }).Recover(false, null, null);
                Check(!r.Success, "TLS=2 alone is not success", output);
            }
            using (MockHardware io = new MockHardware())
            {
                Snapshot baseline = new Gen2Engine(io, delegate(string _) { }).Capture(null);
                baseline.Gpu.Status = baseline.Bridge.Status = 0x101;
                io.LoseWidth = true; RecoveryResult r = new Gen2Engine(io, delegate(string _) { }).Recover(true, null, baseline);
                Check(!r.Success, "Gen2 with width loss fails", output);
            }
            using (MockHardware io = new MockHardware())
            {
                io.IgnoreMmioWrites = true; io.ClosePlm();
                RecoveryResult r = new Gen2Engine(io, delegate(string _) { }).Recover(true, null, null);
                Check(r.Code == "PLM_REOPEN_REQUIRED", "locked PLM classification", output);
            }
            using (MockHardware io = new MockHardware())
            {
                io.LoseCoreUnlock(); RecoveryResult r = new Gen2Engine(io, delegate(string _) { }).Recover(true, null, null);
                Check(r.Code == "CORE_UNLOCK_LOST" && r.MmioWrites == 0 && r.PciWrites == 0,
                    "lost EFI core unlock is fail-closed", output);
            }
            using (MockHardware io = new MockHardware())
            using (MemoryStream stream = new MemoryStream())
            {
                Snapshot before = new Gen2Engine(io, delegate(string _) { }).Capture(null);
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(Snapshot));
                serializer.WriteObject(stream, before); stream.Position = 0;
                Snapshot after = (Snapshot)serializer.ReadObject(stream);
                Check(after.GpuBdf == before.GpuBdf && after.Registers.Count == before.Registers.Count,
                    "snapshot JSON round-trip", output);
            }
            output.WriteLine(failures == 0 ? "ALL TESTS PASSED" : failures + " TEST(S) FAILED");
            return failures == 0 ? 0 : 1;
        }
    }
}
