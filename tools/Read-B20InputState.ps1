[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
# Research only: request the observed input report ID 0x80. No output/feature setter.
Add-Type -Path (Join-Path $PSScriptRoot 'HidProbe.cs')
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class B20InputState {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("hid.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetInputReport(SafeFileHandle device, byte[] report, uint length);
    public static string Read(string path) {
        using(var handle=CreateFile(path,0x80000000,3,IntPtr.Zero,3,0,IntPtr.Zero)) {
            if(handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            byte[] report={0x80,0,0,0};
            if(!HidD_GetInputReport(handle,report,4)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return BitConverter.ToString(report);
        }
    }
}
'@
$devices=@([EposResearch.HidProbe]::Enumerate() | Where-Object {$_.VendorId -eq '1395' -and $_.ProductId -eq '009F' -and $_.UsagePage -eq 'FFCD' -and $_.Usage -eq '0001' -and $_.InputReportBytes -eq 4 -and $_.OutputReportBytes -eq 4})
if($devices.Count -ne 1){throw 'Research probe requires exactly one descriptor-validated B20 vendor collection.'}
try { [pscustomobject]@{ObservedReportId='80';Report=[B20InputState]::Read($devices[0].Path);Error=$null} }
catch { [pscustomobject]@{ObservedReportId='80';Report=$null;Error=$_.Exception.Message} }
