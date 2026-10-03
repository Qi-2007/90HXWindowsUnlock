using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CMP90HX
{
    // Host/DMA RAM only: these helpers must never be used for MMIO. memmove
    // retains the overlap behavior of the former two-copy staging buffer.
    static class NativeBufferMemory
    {
        const ulong Limit=32UL*1024*1024;
        [DllImport("msvcrt.dll",CallingConvention=CallingConvention.Cdecl,EntryPoint="memmove",ExactSpelling=true)]
        static extern IntPtr Move(IntPtr destination,IntPtr source,UIntPtr count);
        [DllImport("msvcrt.dll",CallingConvention=CallingConvention.Cdecl,EntryPoint="memset",ExactSpelling=true)]
        static extern IntPtr Set(IntPtr destination,int value,UIntPtr count);
        static IntPtr Pointer(ulong address,ulong count)
        {
            if(count>Limit || (count!=0 && address==0) || address>(ulong)Int64.MaxValue-count)
                throw new IOException("Invalid native buffer address/length.");
            return new IntPtr((long)address);
        }
        internal static ulong Copy(ulong destination,ulong source,ulong count)
        {
            IntPtr to=Pointer(destination,count), from=Pointer(source,count);
            if(count!=0) Move(to,from,new UIntPtr(count));
            return destination;
        }
        internal static ulong Fill(ulong destination,byte value,ulong count)
        {
            IntPtr to=Pointer(destination,count);
            if(count!=0) Set(to,value,new UIntPtr(count));
            return destination;
        }
    }

    // The ELF uses SysV AMD64; .NET reverse-P/Invoke uses Windows AMD64.
    // Small explicit bridges adapt INTEGER/pointer arguments only. The core
    // and its log callback do not use floating-point arguments.
    sealed class SysV : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate ulong Function(ulong a, ulong b, ulong c, ulong d, ulong e, ulong f);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate ulong Callback(ulong a, ulong b, ulong c, ulong d, ulong e, ulong f, ulong stack);
        readonly List<IntPtr> allocations = new List<IntPtr>();
        readonly List<Delegate> roots = new List<Delegate>();

        internal static IntPtr AllocateLow(int size)
        {
            for (long address = 0x10000000; address + size < 0x78000000; address += 0x10000)
            {
                IntPtr p = CoreNative.VirtualAlloc(new IntPtr(address), new UIntPtr((uint)size), 0x3000, 0x40);
                if (p != IntPtr.Zero) return p;
            }
            throw new IOException("Cannot allocate executable core below 2 GiB (required by ELF 32S relocations).");
        }
        IntPtr Code(List<byte> bytes)
        {
            IntPtr p = AllocateLow(bytes.Count); allocations.Add(p);
            Marshal.Copy(bytes.ToArray(), 0, p, bytes.Count);
            if (!CoreNative.FlushInstructionCache(CoreNative.GetCurrentProcess(), p, new UIntPtr((uint)bytes.Count)))
                throw Native.Failure(Marshal.GetLastWin32Error(), "FlushInstructionCache");
            return p;
        }
        static void Emit(List<byte> b, params byte[] v) { b.AddRange(v); }
        internal ulong Reverse(Callback callback)
        {
            roots.Add(callback);
            List<byte> b = new List<byte>();
            // rsp is 8 mod 16 at SysV entry. 72-byte frame aligns the call,
            // provides Win64 shadow space, 3 stack args, and stack alignment.
            Emit(b, 0x48,0x83,0xec,0x48);
            Emit(b, 0x4c,0x89,0x44,0x24,0x20); // arg5 = r8
            Emit(b, 0x4c,0x89,0x4c,0x24,0x28); // arg6 = r9
            Emit(b, 0x48,0x8d,0x44,0x24,0x50); // original rsp+8: vararg overflow area
            Emit(b, 0x48,0x89,0x44,0x24,0x30);
            Emit(b, 0x49,0x89,0xc9, 0x49,0x89,0xd0, 0x48,0x89,0xf2, 0x48,0x89,0xf9);
            Emit(b, 0x48,0xb8); b.AddRange(BitConverter.GetBytes(Marshal.GetFunctionPointerForDelegate(callback).ToInt64()));
            Emit(b, 0xff,0xd0, 0x48,0x83,0xc4,0x48, 0xc3);
            return (ulong)Code(b).ToInt64();
        }
        internal Function Forward(ulong target)
        {
            List<byte> b = new List<byte>();
            Emit(b, 0x57,0x56, 0x48,0x81,0xec,0xa8,0,0,0);
            // Windows nonvolatile RDI/RSI and XMM6..15 are volatile in SysV.
            for (int reg=6;reg<=15;reg++) {
                Emit(b,0xf3); if (reg>=8) Emit(b,0x44);
                Emit(b,0x0f,0x7f,(byte)(0x84 | ((reg & 7)<<3)),0x24);
                b.AddRange(BitConverter.GetBytes((reg-6)*16));
            }
            Emit(b,0x48,0x89,0xcf, 0x48,0x89,0xd6, 0x4c,0x89,0xc2, 0x4c,0x89,0xc9);
            Emit(b,0x4c,0x8b,0x84,0x24,0xe0,0,0,0, 0x4c,0x8b,0x8c,0x24,0xe8,0,0,0);
            Emit(b,0x48,0xb8); b.AddRange(BitConverter.GetBytes(target));
            Emit(b,0xff,0xd0);
            for (int reg=6;reg<=15;reg++) {
                Emit(b,0xf3); if (reg>=8) Emit(b,0x44);
                Emit(b,0x0f,0x6f,(byte)(0x84 | ((reg & 7)<<3)),0x24);
                b.AddRange(BitConverter.GetBytes((reg-6)*16));
            }
            Emit(b,0x48,0x81,0xc4,0xa8,0,0,0,0x5e,0x5f,0xc3);
            Function f=(Function)Marshal.GetDelegateForFunctionPointer(Code(b),typeof(Function)); roots.Add(f); return f;
        }
        public void Dispose()
        { foreach (IntPtr p in allocations) CoreNative.VirtualFree(p,UIntPtr.Zero,0x8000); allocations.Clear(); roots.Clear(); }
    }

    sealed class ElfCore : IDisposable
    {
        const string Hash="c9702b4887d397272f86dcc25eea2bb11a46d636c91311d7b71f2fb8b01951e5";
        internal const string ManagedHash="b533b7b245ed606c151ca336b9a6bacebe935e3ec478246e879c668a4dd98a6a";
        internal readonly bool Managed;
        internal readonly SysV Abi=new SysV();
        readonly Dictionary<string,SysV.Function> exports=new Dictionary<string,SysV.Function>();
        IntPtr image;
        struct Section { internal uint Type,Link,Info; internal ulong Flags,Offset,Size,Align,Entry,Address; }
        struct Symbol { internal string Name; internal ushort Section; internal ulong Value; }
        static ushort U16(byte[] b,int o) { return BitConverter.ToUInt16(b,o); }
        static uint U32(byte[] b,int o) { return BitConverter.ToUInt32(b,o); }
        static ulong U64(byte[] b,int o) { return BitConverter.ToUInt64(b,o); }
        internal static byte[] PinnedBytes(string path)
        {
            byte[] bytes=File.ReadAllBytes(path);
            IsManaged(bytes);
            return bytes;
        }
        internal static bool IsManaged(byte[] bytes)
        {
            string actual;
            using(SHA256 sha=SHA256.Create()) actual=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
            if(actual!=Hash && actual!=ManagedHash) throw new IOException("NVPermissive core SHA-256 mismatch; refusing unpinned native code.");
            return actual==ManagedHash;
        }
        internal ElfCore(byte[] bytes, Dictionary<string,SysV.Callback> imports)
        {
            try {
                // Always pin at this boundary too: never execute arbitrary ELF.
                Managed=IsManaged(bytes);
                int count=U16(bytes,60), sh=checked((int)U64(bytes,40)), symSection=-1;
                if (count<1 || count>128 || U16(bytes,58)!=64 || U16(bytes,16)!=1 || U16(bytes,18)!=62)
                    throw new IOException("Unsupported ELF layout.");
                Section[] sections=new Section[count]; ulong cursor=0;
                for (int i=0;i<count;i++) {
                    int o=sh+i*64;
                    Section s=new Section {Type=U32(bytes,o+4),Flags=U64(bytes,o+8),Offset=U64(bytes,o+24),Size=U64(bytes,o+32),Link=U32(bytes,o+40),Info=U32(bytes,o+44),Align=U64(bytes,o+48),Entry=U64(bytes,o+56)};
                    if (s.Type==2) symSection=i;
                    if ((s.Flags&2)!=0 && s.Size!=0) { ulong a=Math.Max(s.Align,1); cursor=(cursor+a-1)&~(a-1); s.Address=cursor+1; cursor+=s.Size; }
                    sections[i]=s;
                }
                if (symSection<0) throw new IOException("No ELF symbols.");
                image=SysV.AllocateLow(checked((int)cursor)); ulong address=(ulong)image.ToInt64();
                // VirtualAlloc zeroes NOBITS; copy only allocated PROGBITS.
                for (int i=0;i<count;i++) if (sections[i].Address!=0) {
                    sections[i].Address=address+sections[i].Address-1;
                    if (sections[i].Type!=8) Marshal.Copy(bytes,checked((int)sections[i].Offset),new IntPtr((long)sections[i].Address),checked((int)sections[i].Size));
                }
                Section syms=sections[symSection], strings=sections[syms.Link];
                int symbols=checked((int)(syms.Size/24)); Symbol[] names=new Symbol[symbols]; ulong[] resolved=new ulong[symbols];
                for (int i=0;i<symbols;i++) {
                    int o=checked((int)syms.Offset)+i*24, start=checked((int)(strings.Offset+U32(bytes,o))), end=start;
                    while (bytes[end]!=0) end++;
                    Symbol n=new Symbol {Name=Encoding.ASCII.GetString(bytes,start,end-start),Section=U16(bytes,o+6),Value=U64(bytes,o+8)}; names[i]=n;
                    if (n.Section==0 && n.Name.Length!=0) {
                        SysV.Callback cb; if (!imports.TryGetValue(n.Name,out cb)) throw new IOException("Missing core import "+n.Name);
                        resolved[i]=Abi.Reverse(cb);
                    } else if (n.Section==0xfff1) resolved[i]=n.Value;
                    else if (n.Section!=0) resolved[i]=sections[n.Section].Address+n.Value;
                }
                foreach (Section r in sections) if (r.Type==4) {
                    if (sections[r.Info].Address==0) throw new IOException("Relocation to unallocated section.");
                    for (ulong j=0;j<r.Size;j+=24) {
                        int o=checked((int)(r.Offset+j)); ulong offset=U64(bytes,o), info=U64(bytes,o+8);
                        uint type=unchecked((uint)info); ulong symbol=resolved[info>>32];
                        long addend=BitConverter.ToInt64(bytes,o+16), place=checked((long)(sections[r.Info].Address+offset));
                        int width=type==1?8:4;
                        if (offset+(ulong)width>sections[r.Info].Size || symbol==0) throw new IOException("Invalid core relocation.");
                        long value=checked((long)symbol+addend);
                        if (type==1) Marshal.WriteInt64(new IntPtr(place),value);
                        else {
                            if (type==2 || type==4) value-=place;
                            else if (type!=11) throw new IOException("Unsupported relocation "+type);
                            Marshal.WriteInt32(new IntPtr(place),checked((int)value));
                        }
                    }
                }
                string[] required=Managed?
                    new string[]{"do_permissive_with_handoff","ga102_booter_open_plm_with_handoff","pcie_find_first_closed_plm","pcie_run_pre_reset_group","pcie_check_post_reset_gate","pcie_restore_post_reset_group","gsp_falcon_reset"}:
                    new string[]{"do_permissive","ga102_v67_open_plm","pcie_gen2_find_first_closed_plm","pcie_gen2_run_pre_reset_group","pcie_gen2_check_post_reset_gate","pcie_gen2_restore_post_reset_group","gsp_falcon_reset"};
                foreach (string name in required) {
                    int i=Array.FindIndex(names,delegate(Symbol s){return s.Name==name && s.Section!=0;});
                    if (i<0) throw new IOException("Missing core export "+name);
                    exports[name]=Abi.Forward(resolved[i]);
                }
                if (!CoreNative.FlushInstructionCache(CoreNative.GetCurrentProcess(),image,new UIntPtr(cursor)))
                    throw Native.Failure(Marshal.GetLastWin32Error(),"Flush relocated core");
            } catch { Dispose(); throw; }
        }
        internal int Call(string name,ulong a,ulong b,ulong c,ulong d,ulong e=0,ulong f=0)
        { return unchecked((int)exports[name](a,b,c,d,e,f)); }
        public void Dispose()
        { exports.Clear(); if (image!=IntPtr.Zero) { CoreNative.VirtualFree(image,UIntPtr.Zero,0x8000); image=IntPtr.Zero; } Abi.Dispose(); }
    }
    static class CoreNative
    {
        [DllImport("kernel32.dll",SetLastError=true)] internal static extern IntPtr VirtualAlloc(IntPtr address,UIntPtr size,uint type,uint protect);
        [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool VirtualFree(IntPtr address,UIntPtr size,uint type);
        [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool FlushInstructionCache(IntPtr process,IntPtr address,UIntPtr size);
        [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();
    }
}
