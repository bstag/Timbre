using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace EposResearch {
    public sealed class AudioEndpoint {
        public string Id { get; set; }
        public string Name { get; set; }
        public float VolumeScalar { get; set; }
        public float VolumeDb { get; set; }
        public bool Muted { get; set; }
        public uint HardwareSupportMask { get; set; }
        public string Error { get; set; }
    }
    public static class AudioProbe {
        [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid Format; public uint Id; }
        [StructLayout(LayoutKind.Explicit, Size=24)] struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);
        [ComImport,Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface Enumerator {
            [PreserveSig] int EnumAudioEndpoints(int flow,uint state,out Collection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow,int role,out Device device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id,out Device device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
        }
        [ComImport,Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface Collection { [PreserveSig] int GetCount(out uint count); [PreserveSig] int Item(uint index,out Device device); }
        [ComImport,Guid("D666063F-1587-4E43-81F1-B948E807363F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface Device {
            [PreserveSig] int Activate(ref Guid iid,uint context,IntPtr parameters,[MarshalAs(UnmanagedType.IUnknown)] out object result);
            [PreserveSig] int OpenPropertyStore(uint access,out Store store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out uint state);
        }
        [ComImport,Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface Store {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index,out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key,out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key,ref PropVariant value);
            [PreserveSig] int Commit();
        }
        [ComImport,Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface Volume {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float level,ref Guid context);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level,ref Guid context);
            [PreserveSig] int GetMasterVolumeLevel(out float level);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint channel,float level,ref Guid context);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint channel,float level,ref Guid context);
            [PreserveSig] int GetChannelVolumeLevel(uint channel,out float level);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint channel,out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute,ref Guid context);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
            [PreserveSig] int GetVolumeStepInfo(out uint step,out uint count);
            [PreserveSig] int VolumeStepUp(ref Guid context);
            [PreserveSig] int VolumeStepDown(ref Guid context);
            [PreserveSig] int QueryHardwareSupport(out uint support);
            [PreserveSig] int GetVolumeRange(out float min,out float max,out float increment);
        }
        static void Check(int hr) { Marshal.ThrowExceptionForHR(hr); }
        static void Release(object obj) { if (obj != null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj); }
        public static AudioEndpoint[] Enumerate() {
            if (IntPtr.Size != 8) throw new PlatformNotSupportedException("Use 64-bit PowerShell for this audio probe.");
            var enumerator=(Enumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
            Collection devices=null;
            var result=new List<AudioEndpoint>();
            try {
                Check(enumerator.EnumAudioEndpoints(2,1,out devices)); Check(devices.GetCount(out uint count));
                for(uint i=0;i<count;i++) {
                    Device device=null; Store store=null; object volumeObj=null;
                    try {
                        Check(devices.Item(i,out device)); Check(device.OpenPropertyStore(0,out store));
                        var key=new PropertyKey { Format=new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id=14 };
                        PropVariant value; Check(store.GetValue(ref key,out value));
                        string name;
                        try { name=value.Type==31 ? Marshal.PtrToStringUni(value.Pointer) : null; }
                        finally { PropVariantClear(ref value); }
                        if(name==null || name.IndexOf("EPOS B20",StringComparison.OrdinalIgnoreCase)<0 && name.IndexOf("EPOS GSX 300",StringComparison.OrdinalIgnoreCase)<0) continue;
                        var entry=new AudioEndpoint { Name=name }; result.Add(entry);
                        try {
                            Check(device.GetId(out string id)); entry.Id=id;
                            var iid=new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
                            Check(device.Activate(ref iid,23,IntPtr.Zero,out volumeObj)); var volume=(Volume)volumeObj;
                            Check(volume.GetMasterVolumeLevelScalar(out float scalar)); entry.VolumeScalar=scalar;
                            Check(volume.GetMasterVolumeLevel(out float db)); entry.VolumeDb=db;
                            Check(volume.GetMute(out bool mute)); entry.Muted=mute;
                            Check(volume.QueryHardwareSupport(out uint support)); entry.HardwareSupportMask=support;
                        } catch(Exception e) { entry.Error=e.Message; }
                    } finally { Release(volumeObj); Release(store); Release(device); }
                }
            } finally { Release(devices); Release(enumerator); }
            return result.ToArray();
        }
    }
}
