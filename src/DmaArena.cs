using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace CMP90HX
{
    sealed class RetainedDmaException : IOException
    {
        internal RetainedDmaException(string message,Exception inner=null) : base(message,inner) { }
    }
    sealed class ArenaDescriptor
    {
        internal ulong Physical, Length, Cookie;
    }
    sealed class DmaArena : IDisposable
    {
        readonly WindowsHardware io;
        readonly Dictionary<ulong,ulong> active=new Dictionary<ulong,ulong>();
        readonly KernelDma kernel;
        internal readonly ArenaDescriptor Descriptor;
        internal bool HasOutstanding { get { return active.Count!=0 || kernel.Pending; } }
        IntPtr mapping;
        ulong cursor;
        internal DmaArena(WindowsHardware hardware,KernelDma driver)
        {
            io=hardware; kernel=driver; Descriptor=driver.Descriptor; mapping=driver.Mapping;
        }
        internal void TestMapping()
        {
            byte[] saved=new byte[32]; Marshal.Copy(mapping,saved,0,saved.Length);
            try {
                for (int i=0;i<32;i+=4) {
                    uint value=0x90580000U+(uint)i;
                    Marshal.WriteInt32(mapping,i,unchecked((int)value));
                    System.Threading.Thread.MemoryBarrier();
                    if (io.ReadMmio(Descriptor.Physical+(uint)i)!=value)
                        throw new IOException("DMA user mapping/physical readback disagree.");
                }
            } finally { Marshal.Copy(saved,0,mapping,saved.Length); System.Threading.Thread.MemoryBarrier(); }
        }
        internal ulong Allocate(ulong size,ulong physicalOut)
        {
            if (size==0 || size>Descriptor.Length) return 0;
            ulong rounded=(size+4095)&~4095UL;
            if (active.Count==0) cursor=0;
            if (rounded>Descriptor.Length-cursor) return 0;
            ulong result=checked((ulong)mapping.ToInt64()+cursor), physical=Descriptor.Physical+cursor;
            if(active.Count==0) kernel.Acquire();
            active.Add(result,rounded); cursor+=rounded;
            NativeBufferMemory.Fill(result,0,rounded);
            Marshal.WriteInt64(new IntPtr(checked((long)physicalOut)),checked((long)physical));
            return result;
        }
        internal void Free(ulong address,ulong size,ulong physical)
        {
            ulong rounded;
            if (!active.TryGetValue(address,out rounded) || rounded!=((size+4095)&~4095UL) ||
                physical!=Descriptor.Physical+address-(ulong)mapping.ToInt64())
                throw new IOException("Core DMA free does not match its arena allocation.");
            active.Remove(address);
            if(active.Count==0) kernel.Release();
        }
        public void Dispose()
        {
            // Reserved physical memory is NOT freed/overwritten, even on an
            // incomplete firmware operation. A cold boot owns its lifecycle.
            if (mapping!=IntPtr.Zero) {
                kernel.Dispose();
                mapping=IntPtr.Zero;
            }
            active.Clear();
        }
    }
}
