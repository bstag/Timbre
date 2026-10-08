[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
# Research probe only. Run in a child process with an external timeout because
# this intentionally mirrors the vendor's synchronous WriteFile/ReadFile sequence.
Add-Type -Path (Join-Path $PSScriptRoot 'HidProbe.cs')
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class B20SynchronousStatus {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool WriteFile(SafeFileHandle h,byte[] bytes,uint count,out uint written,IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool ReadFile(SafeFileHandle h,byte[] bytes,uint count,out uint read,IntPtr overlapped);
    public static void Query(string path) {
        using(var handle=CreateFile(path,0xC0000000,3,IntPtr.Zero,3,0,IntPtr.Zero)) {
            if(handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            byte[] query={0xF0,5,0,0xFF}; uint size;
            if(!WriteFile(handle,query,4,out size,IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if(size!=4) throw new InvalidOperationException("Short status-query write.");
            Console.WriteLine("Query sent: F0 05 00 FF"); Console.Out.Flush();
            for(int i=0;i<32;i++) {
                byte[] report=new byte[4];
                if(!ReadFile(handle,report,4,out size,IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
                Console.WriteLine("Report: "+BitConverter.ToString(report)); Console.Out.Flush();
                if(size!=4) throw new InvalidOperationException("Short input report.");
                if(report[0]==0x80 && report[1]==3 && report[2]>=1 && report[2]<=4 && report[3]==0xFF) return;
            }
            throw new InvalidOperationException("Too many unrelated reports.");
        }
    }
}
'@
$devices=@([EposResearch.HidProbe]::Enumerate() | Where-Object {$_.VendorId -eq '1395' -and $_.ProductId -eq '009F' -and $_.UsagePage -eq 'FFCD' -and $_.Usage -eq '0001' -and $_.InputReportBytes -eq 4 -and $_.OutputReportBytes -eq 4})
if($devices.Count -ne 1){throw 'Research probe requires exactly one descriptor-validated B20 vendor collection.'}
[B20SynchronousStatus]::Query($devices[0].Path)
