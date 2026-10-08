using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EposResearch {
    public sealed class HidInterface {
        public string Path { get; set; }
        public string Product { get; set; }
        public string VendorId { get; set; }
        public string ProductId { get; set; }
        public string UsbInstanceId { get; set; }
        public string UsagePage { get; set; }
        public string Usage { get; set; }
        public ushort InputReportBytes { get; set; }
        public ushort OutputReportBytes { get; set; }
        public ushort FeatureReportBytes { get; set; }
        public string Error { get; set; }
    }
    public static class HidProbe {
        [StructLayout(LayoutKind.Sequential)] struct InterfaceData { public int Size; public Guid ClassGuid; public uint Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] struct DeviceInfo { public int Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] struct Attributes { public int Size; public ushort VendorId, ProductId, Version; }
        [StructLayout(LayoutKind.Sequential)] struct Caps {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst=17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
            public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }
        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError=true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr device);
        [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, ref DeviceInfo device);
        [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode)] static extern uint CM_Get_Device_IDW(uint node, System.Text.StringBuilder id, uint length, uint flags);
        [DllImport("cfgmgr32.dll")] static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern SafeFileHandle CreateFile(string name, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("hid.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_GetAttributes(SafeFileHandle file, ref Attributes attrs);
        [DllImport("hid.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_GetPreparsedData(SafeFileHandle file, out IntPtr data);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out Caps caps);
        [DllImport("hid.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_GetProductString(SafeFileHandle file, byte[] buffer, uint size);
        public static HidInterface[] Enumerate() {
            HidD_GetHidGuid(out Guid guid);
            IntPtr set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
            if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new List<HidInterface>();
            try {
                for (uint index=0; ; index++) {
                    var data = new InterfaceData { Size=Marshal.SizeOf(typeof(InterfaceData)) };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data)) {
                        int err=Marshal.GetLastWin32Error(); if(err==259) break; throw new Win32Exception(err);
                    }
                    uint required;
                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out required, IntPtr.Zero);
                    if (required < 6) throw new InvalidOperationException("Invalid interface detail size");
                    IntPtr detail=Marshal.AllocHGlobal((int)required);
                    try {
                        Marshal.WriteInt32(detail, IntPtr.Size==8 ? 8 : 6);
                        var device=new DeviceInfo { Size=Marshal.SizeOf(typeof(DeviceInfo)) };
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out required, ref device)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        string path=Marshal.PtrToStringUni(IntPtr.Add(detail,4));
                        if (path == null || path.IndexOf("vid_1395&pid_0098",StringComparison.OrdinalIgnoreCase)<0 && path.IndexOf("vid_1395&pid_009f",StringComparison.OrdinalIgnoreCase)<0) continue;
                        var entry=new HidInterface { Path=path, UsbInstanceId=UsbAncestor(device.DevInst) }; result.Add(entry);
                        // Zero desired access: descriptor queries only. No reports are sent or consumed.
                        using (var file=CreateFile(path,0,3,IntPtr.Zero,3,0,IntPtr.Zero)) {
                            if(file.IsInvalid) { entry.Error=new Win32Exception(Marshal.GetLastWin32Error()).Message; continue; }
                            var attrs=new Attributes { Size=Marshal.SizeOf(typeof(Attributes)) };
                            if(HidD_GetAttributes(file,ref attrs)) { entry.VendorId=attrs.VendorId.ToString("X4"); entry.ProductId=attrs.ProductId.ToString("X4"); }
                            var product=new byte[256];
                            if(HidD_GetProductString(file,product,(uint)product.Length)) entry.Product=System.Text.Encoding.Unicode.GetString(product).TrimEnd('\0');
                            IntPtr parsed;
                            if(!HidD_GetPreparsedData(file,out parsed)) { entry.Error=new Win32Exception(Marshal.GetLastWin32Error()).Message; continue; }
                            try {
                                Caps caps; int status=HidP_GetCaps(parsed,out caps);
                                if(status!=0x00110000) { entry.Error="HidP_GetCaps status: "+status.ToString("X8"); continue; }
                                entry.Usage=caps.Usage.ToString("X4"); entry.UsagePage=caps.UsagePage.ToString("X4");
                                entry.InputReportBytes=caps.InputReportByteLength; entry.OutputReportBytes=caps.OutputReportByteLength; entry.FeatureReportBytes=caps.FeatureReportByteLength;
                            } finally { HidD_FreePreparsedData(parsed); }
                        }
                    } finally { Marshal.FreeHGlobal(detail); }
                }
            } finally { SetupDiDestroyDeviceInfoList(set); }
            return result.ToArray();
        }
        static string UsbAncestor(uint node) {
            for(int depth=0; depth<20; depth++) {
                var id=new System.Text.StringBuilder(1024);
                if(CM_Get_Device_IDW(node,id,(uint)id.Capacity,0)!=0) return null;
                string value=id.ToString();
                if(value.StartsWith("USB\\VID_",StringComparison.OrdinalIgnoreCase) && value.IndexOf("&MI_",StringComparison.OrdinalIgnoreCase)<0) return value;
                if(CM_Get_Parent(out node,node,0)!=0) return null;
            }
            return null;
        }
    }
}
