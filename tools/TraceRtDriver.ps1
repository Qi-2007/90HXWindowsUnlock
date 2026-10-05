[CmdletBinding()]
param(
    [string]$Binary=(Join-Path $PSScriptRoot '..\drivers\rt-research-616.56\nvlddmkm.sys'),
    [uint32[]]$TargetRva=@(0x9becf0),
    [string]$Output=(Join-Path $PSScriptRoot '..\logs\rt-research-20261004\xref-candidates.json')
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'ConsoleEncoding.ps1')
if (-not ('RtPeTrace' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
public class RtPeTrace {
  class Section {public string Name;public uint Rva,Raw,Size,Flags;}
  static string H(ulong v){return "0x"+v.ToString("x");}
  public static object Scan(string path,uint[] targets) {
    byte[] b=File.ReadAllBytes(path);int pe=BitConverter.ToInt32(b,0x3c);
    if(BitConverter.ToUInt32(b,pe)!=0x4550 || BitConverter.ToUInt16(b,pe+24)!=0x20b) throw new Exception("Expected PE32+");
    ulong imageBase=BitConverter.ToUInt64(b,pe+48);
    int n=BitConverter.ToUInt16(b,pe+6),start=pe+24+BitConverter.ToUInt16(b,pe+20);
    var sections=new List<Section>();var functions=new List<uint[]>();
    for(int j=0;j<n;j++) {
      int s=start+40*j;
      var section=new Section{Name=Encoding.ASCII.GetString(b,s,8).TrimEnd('\0'),Rva=BitConverter.ToUInt32(b,s+12),Size=BitConverter.ToUInt32(b,s+16),Raw=BitConverter.ToUInt32(b,s+20),Flags=BitConverter.ToUInt32(b,s+36)};
      sections.Add(section);
      if(section.Name==".pdata") for(long i=section.Raw;i+12<=Math.Min(b.Length,(long)section.Raw+section.Size);i+=12) {
        uint begin=BitConverter.ToUInt32(b,(int)i),end=BitConverter.ToUInt32(b,(int)i+4);
        if(begin!=0 && end>begin) functions.Add(new uint[]{begin,end});
      }
    }
    var references=new List<object>();var exports=new List<object>();
    uint[] methods={0x801104,0x801110,0x20801201,0x20801228,0x20800a2a,0x20800a2b};
    foreach(Section s in sections) for(long i=s.Raw;i<Math.Min(b.Length,(long)s.Raw+s.Size);i++) {
      uint rva=s.Rva+(uint)(i-s.Raw);string kind=null;uint dest=0;
      // NVIDIA's published NVOC_EXPORTED_METHOD_DEF layout places pFunc
      // sixteen bytes BEFORE methodId, not at the next table entry.
      if(s.Name==".rdata" && i>=s.Raw+16 && i+16<=Math.Min(b.Length,(long)s.Raw+s.Size)) {
        uint method=BitConverter.ToUInt32(b,(int)i);
        if(Array.IndexOf(methods,method)>=0) {
          ulong func=BitConverter.ToUInt64(b,(int)i-16),classInfo=BitConverter.ToUInt64(b,(int)i+8);
          bool executable=false;
          if(func>=imageBase && func-imageBase<=uint.MaxValue) foreach(Section code in sections)
            if((code.Flags&0x20000000)!=0 && func-imageBase>=code.Rva && func-imageBase<(ulong)code.Rva+code.Size) {executable=true;break;}
          if(executable && classInfo>=imageBase && classInfo-imageBase<=uint.MaxValue)
            exports.Add(new{MethodId=H(method),RecordRva=H(rva-16),HandlerRva=H(func-imageBase),Flags=H(BitConverter.ToUInt32(b,(int)i-8)),AccessRight=H(BitConverter.ToUInt32(b,(int)i-4)),ParamSize=H(BitConverter.ToUInt32(b,(int)i+4)),ClassInfoRva=H(classInfo-imageBase)});
        }
      }
      if((s.Flags&0x20000000)!=0 && i+5<=b.Length && (b[i]==0xe8 || b[i]==0xe9)) {
        dest=unchecked((uint)((long)rva+5+BitConverter.ToInt32(b,(int)i+1)));
        if(Array.IndexOf(targets,dest)>=0) kind=b[i]==0xe8?"rel32-call-candidate":"rel32-jump-candidate";
      }
      if((s.Flags&0x20000000)!=0 && i+7<=b.Length && (b[i]&0xf0)==0x40 && b[i+1]==0x8d && (b[i+2]&0xc7)==5) {
        uint lea=unchecked((uint)((long)rva+7+BitConverter.ToInt32(b,(int)i+3)));
        if(Array.IndexOf(targets,lea)>=0) {kind="rip-lea-candidate";dest=lea;}
      }
      if(i+8<=b.Length) {
        ulong ptr=BitConverter.ToUInt64(b,(int)i);
        if(ptr>=imageBase && ptr-imageBase<=uint.MaxValue && Array.IndexOf(targets,(uint)(ptr-imageBase))>=0) {kind="absolute-pointer-candidate";dest=(uint)(ptr-imageBase);}
      }
      if(kind==null) continue;
      uint begin=0,end=0;
      foreach(uint[] f in functions) if(rva>=f[0] && rva<f[1]) {begin=f[0];end=f[1];break;}
      references.Add(new {Kind=kind,Section=s.Name,FileOffset=H((ulong)i),Rva=H(rva),Va=H(imageBase+rva),TargetRva=H(dest),FunctionBegin=H(begin),FunctionEnd=H(end)});
    }
    var bounds=new List<object>();
    foreach(uint target in targets) foreach(uint[] f in functions) if(target>=f[0] && target<f[1]) {bounds.Add(new{TargetRva=H(target),Begin=H(f[0]),End=H(f[1])});break;}
    return new {Binary=path,ImageBase=H(imageBase),NvOcExportRecords=exports,FunctionBounds=bounds,References=references,Limits="Byte candidates, not decoded instructions; verify boundaries with disassembly. Absolute pointers may be tables or data. Bounds from .pdata are unwind regions and may cover only part of a logical function. NVOC records inferred using NVIDIA public layout and executable-pointer checks."};
  }
}
'@
}
$rtTrace=[RtPeTrace]::Scan([IO.Path]::GetFullPath($Binary),$TargetRva)
$rtTrace | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Output -Encoding utf8
$rtTrace.NvOcExportRecords | Format-Table -AutoSize
$rtTrace.FunctionBounds | Format-Table -AutoSize
$rtTrace.References | Group-Object TargetRva,Kind | Select-Object Name,Count | Format-Table -AutoSize
$rtTrace.References | Select-Object -First 20 | Format-Table -AutoSize
if ($rtTrace.References.Count -gt 20) { Write-Host "All $($rtTrace.References.Count) candidates saved to $Output" }
