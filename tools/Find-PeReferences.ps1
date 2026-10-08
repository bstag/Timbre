[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string[]]$Strings)
$ErrorActionPreference='Stop'
# Static PE32 data inspection only. Does not load or execute the inspected image.
if (-not ('EposResearch.PeReferences' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
namespace EposResearch {
    public sealed class PeReference {
        public string Text, Encoding, Address;
        public string[] References;
    }
    public static class PeReferences {
        static IEnumerable<int> Find(byte[] bytes, byte[] needle) {
            for(int i=0;i<=bytes.Length-needle.Length;i++) {
                if(bytes[i]!=needle[0]) continue;
                int j=1; while(j<needle.Length && bytes[i+j]==needle[j]) j++;
                if(j==needle.Length) yield return i;
            }
        }
        static uint Rva(byte[] bytes,int sections,int count,int offset) {
            for(int i=0;i<count;i++) {
                int s=sections+i*40;
                uint raw=BitConverter.ToUInt32(bytes,s+20), size=BitConverter.ToUInt32(bytes,s+16);
                if(offset>=raw && offset-raw<size) return BitConverter.ToUInt32(bytes,s+12)+(uint)(offset-raw);
            }
            return (uint)offset;
        }
        public static PeReference[] Scan(string path,string[] strings) {
            byte[] bytes=File.ReadAllBytes(path);
            int pe=BitConverter.ToInt32(bytes,0x3c), optional=pe+24;
            if(BitConverter.ToUInt32(bytes,pe)!=0x4550 || BitConverter.ToUInt16(bytes,optional)!=0x10b)
                throw new InvalidDataException("Only PE32 images are supported by this probe.");
            int count=BitConverter.ToUInt16(bytes,pe+6), sections=optional+BitConverter.ToUInt16(bytes,pe+20);
            uint image=BitConverter.ToUInt32(bytes,optional+28);
            var result=new List<PeReference>();
            foreach(string text in strings) foreach(var encoding in new[]{System.Text.Encoding.ASCII,System.Text.Encoding.Unicode}) {
                foreach(int offset in Find(bytes,encoding.GetBytes(text+"\0"))) {
                    uint address=image+Rva(bytes,sections,count,offset);
                    var refs=new List<string>();
                    foreach(int reference in Find(bytes,BitConverter.GetBytes(address))) refs.Add((image+Rva(bytes,sections,count,reference)).ToString("X8"));
                    result.Add(new PeReference{Text=text,Encoding=encoding.WebName,Address=address.ToString("X8"),References=refs.ToArray()});
                }
            }
            return result.ToArray();
        }
    }
}
'@
}
[EposResearch.PeReferences]::Scan((Resolve-Path -LiteralPath $Path).Path,$Strings)
