using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace EposControl.Core;

[SupportedOSPlatform("windows")]
public sealed class CoreAudioBackend : IAudioBackend
{
    private readonly bool activateDiscoveredControls;
    private GsxInitializationDiscovery? preparedGsx;
    internal bool UsesPausedGsxDiscovery => preparedGsx is not null;
    public IReadOnlyList<AudioEndpoint> LastMetadataDiscovery => preparedGsx?.LastEndpoints ?? [];
    public IReadOnlyList<string> LastPhysicalGsxInstances => preparedGsx?.LastPhysicalInstances ?? [];
    public string DiscoveryMethod => preparedGsx?.Method ?? "ActiveEndpoints";
    public CoreAudioBackend() : this(true) { }
    // Initializers need identity discovery before audio-client activation can retain APO objects.
    public CoreAudioBackend(bool activateDiscoveredControls) => this.activateDiscoveredControls = activateDiscoveredControls;
    public static CoreAudioBackend PrepareGsxInitialization(GsxApoStartupState startup)
    {
        ArgumentNullException.ThrowIfNull(startup);
        if (WindowsApoObjectHost.AudioServiceState != 4) throw new InvalidOperationException("Prepare GSX while Windows Audio is running.");
        var live = new CoreAudioBackend(false);
        var discovery = new GsxInitializationDiscovery(startup, live.Discover,
            () => WindowsApoObjectHost.AudioServiceState is 1 or 2 or 3, Native.PresentUsbInstances);
        discovery.Prepare();
        return new(false) { preparedGsx = discovery };
    }
    public static bool CanRetainB20WhileAudioStopped(AudioEndpoint endpoint)
    {
        if (endpoint.Usb is not { VendorId: "1395" } usb || !usb.ProductId.Equals("009f", StringComparison.OrdinalIgnoreCase)) return false;
        try { WindowsApoObjectHost.RequireAudioStopped(); }
        catch (InvalidOperationException) { return false; }
        return Native.IsPresent(usb.InstanceId);
    }
    private static readonly Guid Context = new("6BC73F3C-56B2-4953-B6F1-4898340C9C0B");
    public IReadOnlyList<AudioEndpoint> Discover()
    {
        if (preparedGsx is not null) return preparedGsx.Discover();
        var enumerator = Native.CreateEnumerator();
        var result = new List<AudioEndpoint>();
        try {
            foreach (var direction in Enum.GetValues<AudioDirection>()) {
                Native.Check(enumerator.EnumAudioEndpoints(direction == AudioDirection.Playback ? 0 : 1, 1, out var devices));
                try {
                    Native.Check(devices.GetCount(out var count));
                    for (uint index = 0; index < count; index++) {
                        Native.Check(devices.Item(index, out var device));
                        try {
                            Native.Check(device.GetId(out var id));
                            Native.Check(device.OpenPropertyStore(0, out var store));
                            string name, adapter, instance;
                            try {
                                name = Native.StringProperty(store, new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14) ?? id;
                                adapter = Native.StringProperty(store, new Guid("026E516E-B814-414B-83CD-856D6FEF4822"), 2) ?? "Unknown adapter";
                                instance = Native.StringProperty(store, new Guid("78C34FC8-104A-4ACA-9EA4-524D52996E57"), 256) ?? "SWD\\MMDEVAPI\\" + id;
                            } finally { Native.Release(store); }
                            var usb = Native.FindUsbAncestor(instance);
                            var endpoint = new AudioEndpoint(id, name, adapter, direction, usb, null, null, null);
                            if (!DeviceCatalog.IsEpos(endpoint)) continue;
                            if (activateDiscoveredControls) {
                                try { endpoint = endpoint with { State = ReadVolume(device), Format = Native.MixFormat(device) }; }
                                catch (Exception ex) { endpoint = endpoint with { Error = ex.Message }; }
                            }
                            result.Add(endpoint);
                        } finally { Native.Release(device); }
                    }
                } finally { Native.Release(devices); }
            }
        } finally { Native.Release(enumerator); }
        return result;
    }
    public AudioState Read(string endpointId) => WithDevice(endpointId, ReadVolume);
    public AudioState Apply(string endpointId, float level, bool muted)
    {
        if (!float.IsFinite(level) || level is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(level));
        return WithDevice(endpointId, device => {
            var volume = Native.ActivateVolume(device);
            try {
                var before = Native.Read(volume); var context = Context;
                try {
                    // Muting happens before a level change; unmuting happens afterward.
                    if (muted && !before.Muted) Native.Check(volume.SetMute(true, ref context));
                    Native.Check(volume.SetMasterVolumeLevelScalar(level, ref context));
                    Native.Check(volume.SetMute(muted, ref context));
                    return Native.Read(volume);
                } catch (Exception writeError) {
                    try { Native.Check(volume.SetMasterVolumeLevelScalar(before.Level, ref context)); Native.Check(volume.SetMute(before.Muted, ref context)); }
                    catch (Exception restoreError) { throw new AggregateException("Audio update failed and the previous state could not be restored.", writeError, restoreError); }
                    throw;
                }
            } finally { Native.Release(volume); }
        });
    }
    private static AudioState ReadVolume(Native.Device device)
    {
        var volume = Native.ActivateVolume(device);
        try { return Native.Read(volume); } finally { Native.Release(volume); }
    }
    private static T WithDevice<T>(string endpointId, Func<Native.Device, T> action)
    {
        var enumerator = Native.CreateEnumerator();
        try {
            Native.Check(enumerator.GetDevice(endpointId, out var device));
            try { Native.Check(device.GetState(out var state)); if ((state & 1) == 0) throw new InvalidOperationException("This device is disconnected. Refresh the device list."); return action(device); }
            finally { Native.Release(device); }
        } finally { Native.Release(enumerator); }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct PropertyKey { public Guid Format; public uint Id; }
        [StructLayout(LayoutKind.Explicit, Size = 24)] internal struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Locate_DevNodeW(out uint node, string id, uint flags);
        [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_IDW(uint node, StringBuilder buffer, uint length, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint length, uint flags);
        internal static IReadOnlyList<string> PresentUsbInstances()
        {
            const uint presentUsb = 0x101; // PRESENT | ENUMERATOR, no service-generated devnodes.
            for (var attempt = 0; attempt < 3; attempt++) {
                var status = CM_Get_Device_ID_List_SizeW(out var length, "USB", presentUsb);
                if (status != 0 || length is < 2 or > 1048576) throw new IOException("Cannot size the present USB instance list: " + status);
                var buffer = new char[length];
                status = CM_Get_Device_ID_ListW("USB", buffer, length, presentUsb);
                if (status == 26) continue; // CR_BUFFER_SMALL after a concurrent device-tree change.
                if (status != 0 || buffer[^1] != '\0') throw new IOException("Cannot read the present USB instance list: " + status);
                return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            }
            throw new IOException("USB device tree kept changing; GSX initialization refused.");
        }
        internal static bool IsPresent(string instance) => CM_Locate_DevNodeW(out _, instance, 0) == 0;
        internal static UsbIdentity? FindUsbAncestor(string instance)
        {
            if (CM_Locate_DevNodeW(out var node, instance, 0) != 0) return null;
            UsbIdentity? best = null;
            for (var depth = 0; depth < 20; depth++) {
                var buffer = new StringBuilder(1024);
                if (CM_Get_Device_IDW(node, buffer, (uint)buffer.Capacity, 0) != 0) break;
                var id = buffer.ToString(); var match = Regex.Match(id, @"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);
                if (match.Success) {
                    best = new UsbIdentity(match.Groups[1].Value.ToUpperInvariant(), match.Groups[2].Value.ToUpperInvariant(), id);
                    if (!id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)) break;
                }
                if (CM_Get_Parent(out node, node, 0) != 0) break;
            }
            return best;
        }
        internal static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
        internal static void Release(object? obj) { if (obj is not null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj); }
        internal static Enumerator CreateEnumerator()
        {
            if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("Use the 64-bit build.");
            return (Enumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
        }
        internal static string? StringProperty(Store store, Guid guid, uint id)
        {
            var key = new PropertyKey { Format = guid, Id = id };
            if (store.GetValue(ref key, out var value) < 0) return null;
            try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null; }
            finally { PropVariantClear(ref value); }
        }
        internal static Volume ActivateVolume(Device device)
        {
            var iid = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A"); Check(device.Activate(ref iid, 23, IntPtr.Zero, out var obj)); return (Volume)obj;
        }
        internal static AudioState Read(Volume volume)
        {
            Check(volume.GetMasterVolumeLevelScalar(out var scalar)); Check(volume.GetMasterVolumeLevel(out var db));
            Check(volume.GetMute(out var muted)); Check(volume.QueryHardwareSupport(out var hardware));
            return new AudioState(scalar, muted, db, hardware);
        }
        internal static AudioFormat? MixFormat(Device device)
        {
            var iid = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
            if (device.Activate(ref iid, 23, IntPtr.Zero, out var obj) < 0) return null;
            try {
                var client = (AudioClient)obj;
                if (client.GetMixFormat(out var format) < 0) return null;
                try { return new AudioFormat((ushort)Marshal.ReadInt16(format, 2), Marshal.ReadInt32(format, 4), (ushort)Marshal.ReadInt16(format, 14)); }
                finally { Marshal.FreeCoTaskMem(format); }
            } finally { Release(obj); }
        }
        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface Enumerator {
            [PreserveSig] int EnumAudioEndpoints(int flow, uint state, out Collection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out Device device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out Device device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
        }
        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface Collection { [PreserveSig] int GetCount(out uint count); [PreserveSig] int Item(uint index, out Device device); }
        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface Device {
            [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
            [PreserveSig] int OpenPropertyStore(uint access, out Store store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out uint state);
        }
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface Store {
            [PreserveSig] int GetCount(out uint count); [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value); [PreserveSig] int Commit();
        }
        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface Volume {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify); [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context); [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
            [PreserveSig] int GetMasterVolumeLevel(out float level); [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context); [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
            [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level); [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context); [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
            [PreserveSig] int GetVolumeStepInfo(out uint step, out uint count); [PreserveSig] int VolumeStepUp(ref Guid context); [PreserveSig] int VolumeStepDown(ref Guid context);
            [PreserveSig] int QueryHardwareSupport(out uint support); [PreserveSig] int GetVolumeRange(out float min, out float max, out float increment);
        }
        [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface AudioClient {
            [PreserveSig] int Initialize(int mode, uint flags, long duration, long periodicity, IntPtr format, IntPtr session);
            [PreserveSig] int GetBufferSize(out uint size); [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint frames); [PreserveSig] int IsFormatSupported(int mode, IntPtr format, out IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start(); [PreserveSig] int Stop(); [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        }
    }
}
