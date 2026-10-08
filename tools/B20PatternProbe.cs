using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace EposResearch {
    public sealed class PatternProbeResult {
        public string QueryHex, ResponseHex, Error;
        public string[] Reports;
    }
    // The only outgoing report is the exact four-byte getMicDirection query
    // statically recovered from the installed Suite service. No setters or firmware commands.
    public static class B20PatternProbe {
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        static extern SafeFileHandle CreateFile(string name,uint access,uint sharing,IntPtr security,uint disposition,uint flags,IntPtr template);
        public static PatternProbeResult Query(HidInterface device) {
            if(device.VendorId!="1395" || device.ProductId!="009F" || device.UsagePage!="FFCD" || device.Usage!="0001" || device.InputReportBytes!=4 || device.OutputReportBytes!=4)
                throw new InvalidOperationException("Only the descriptor-validated B20 vendor collection is supported.");
            var result=new PatternProbeResult {QueryHex="F0 05 00 FF"};
            var reports=new List<string>();
            using(var handle=CreateFile(device.Path,0xC0000000,3,IntPtr.Zero,3,0x40000000,IntPtr.Zero)) {
                if(handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                using(var stream=new FileStream(handle,FileAccess.ReadWrite,4,true))
                using(var cancel=new CancellationTokenSource(1500)) {
                    try {
                        var response=new byte[4];
                        var query=new byte[]{0xF0,0x05,0,0xFF};
                        stream.WriteAsync(query,0,query.Length,cancel.Token).GetAwaiter().GetResult();
                        stream.FlushAsync(cancel.Token).GetAwaiter().GetResult();
                        while(reports.Count<32) {
                            int read=stream.ReadAsync(response,0,response.Length,cancel.Token).GetAwaiter().GetResult();
                            if(read!=4) throw new IOException("Short B20 input report: "+read);
                            result.ResponseHex=BitConverter.ToString(response).Replace('-',' ');
                            reports.Add(result.ResponseHex);
                        }
                    } catch(Exception ex) { result.Error=ex.GetType().Name+": "+ex.Message; }
                }
            }
            result.Reports=reports.ToArray();
            return result;
        }
    }
}
