[CmdletBinding()]
param(
    [string]$DriverDirectory = (Join-Path $PSScriptRoot '..\drivers\rt-research-616.56'),
    [string]$Output = (Join-Path $PSScriptRoot '..\logs\rt-research-20261004\static-scan.json'),
    [uint32[]]$Literals=@(0x823808,0x823814,0x823818,0x823828,0x823834)
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'ConsoleEncoding.ps1')
# Byte matches are candidates only. Executable sections still contain data;
# neither instruction boundaries nor function identities are inferred here.
if (-not ('RtBinaryScan' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
public static class RtBinaryScan {
  public static object Scan(string path,uint[] offsets) {
    byte[] b=File.ReadAllBytes(path);
    int pe=BitConverter.ToInt32(b,0x3c);
    if(BitConverter.ToUInt32(b,pe)!=0x4550 || BitConverter.ToUInt16(b,pe+24)!=0x20b) throw new Exception("Expected PE32+");
    int n=BitConverter.ToUInt16(b,pe+6), start=pe+24+BitConverter.ToUInt16(b,pe+20);
    ulong imageBase=BitConverter.ToUInt64(b,pe+48);
    var sections=new List<object>();var hits=new List<object>();
    bool[] lowBytes=new bool[256];foreach(uint value in offsets) lowBytes[value&255]=true;
    for(int j=0;j<n;j++) {
      int s=start+40*j;string name=Encoding.ASCII.GetString(b,s,8).TrimEnd('\0');
      uint rva=BitConverter.ToUInt32(b,s+12),size=BitConverter.ToUInt32(b,s+16),raw=BitConverter.ToUInt32(b,s+20),flags=BitConverter.ToUInt32(b,s+36);
      sections.Add(new {Name=name,Rva="0x"+rva.ToString("x"),Raw="0x"+raw.ToString("x"),Size=size,Executable=(flags&0x20000000)!=0});
      for(long i=raw;i+4<=Math.Min((long)b.Length,(long)raw+size);i++) {
        if(!lowBytes[b[i]]) continue;
        uint value=BitConverter.ToUInt32(b,(int)i);
        if(Array.IndexOf(offsets,value)<0) continue;
        uint hitRva=rva+(uint)(i-raw);
        int contextStart=(int)Math.Max(raw,i-16),length=(int)Math.Min(36,(long)raw+size-contextStart);
        hits.Add(new {Literal="0x"+value.ToString("x8"),Section=name,Executable=(flags&0x20000000)!=0,FileOffset="0x"+i.ToString("x"),Rva="0x"+hitRva.ToString("x"),Va="0x"+(imageBase+hitRva).ToString("x"),Context=BitConverter.ToString(b,contextStart,length)});
      }
    }
    string ascii=Encoding.ASCII.GetString(b);
    var strings=new List<object>();
    foreach(Match m in Regex.Matches(ascii,@"[\x20-\x7e]{6,}")) {
      if(Regex.IsMatch(m.Value,@"grGetRTCoreCount|SM_TTU|FEATURE_OVERRIDE_QUADRO|FEATURE_READOUT|\.pdb$",RegexOptions.IgnoreCase))
        strings.Add(new {FileOffset="0x"+m.Index.ToString("x"),Text=m.Value});
    }
    return new {File=Path.GetFileName(path),ImageBase="0x"+imageBase.ToString("x"),Sections=sections,LiteralCandidates=hits,Strings=strings,Limits="Little-endian 32-bit literals and ASCII strings only; not exhaustive, no xrefs or function identification."};
  }
}
'@
}
$rtScan = foreach ($rtName in @('nvlddmkm.sys','nvwgf2umx.dll','nvrtum64.dll','nvapi64_impl.dll')) {
    [RtBinaryScan]::Scan((Join-Path $DriverDirectory $rtName),$Literals)
}
$rtScan | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Output -Encoding utf8
$rtScan | ForEach-Object {
    [pscustomobject]@{File=$_.File;LiteralCandidates=$_.LiteralCandidates.Count;RelevantStrings=$_.Strings.Count}
} | Format-Table -AutoSize
