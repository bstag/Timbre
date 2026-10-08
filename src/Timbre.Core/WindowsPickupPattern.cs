using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Timbre.Core;

public sealed record B20PatternHidDevice(string Path, UsbIdentity? Usb, ushort UsagePage, ushort Usage,
    int InputBytes, int OutputBytes, int FeatureBytes);
public static class B20PatternDeviceGuard
{
    public static B20PatternHidDevice Select(AudioEndpoint selected, IReadOnlyList<AudioEndpoint> active, IReadOnlyList<B20PatternHidDevice> interfaces)
    {
        B20PatternProtocol.ValidateEndpoint(selected);
        if (string.IsNullOrWhiteSpace(selected.Usb!.InstanceId) || !selected.Usb.InstanceId.StartsWith("USB\\VID_1395&PID_009F\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("B20 physical identity is missing or incompatible.");
        if (active.Count(e => e.Id == selected.Id && e.ProfileIdentity == selected.ProfileIdentity && e.Direction == AudioDirection.Microphone) != 1)
            throw new InvalidOperationException("The selected B20 microphone is disconnected or its identity changed.");
        var matching = interfaces.Where(d => d.Usb is { } usb && usb.VendorId == "1395" && usb.ProductId == "009F" &&
            usb.InstanceId.Equals(selected.Usb!.InstanceId, StringComparison.OrdinalIgnoreCase) && d.UsagePage == 0xFFCD && d.Usage == 1).ToArray();
        if (matching.Length != 1) throw new InvalidOperationException("Cannot identify one vendor HID interface for this B20.");
        var device = matching[0];
        if (device.InputBytes != 4 || device.OutputBytes != 4 || device.FeatureBytes != 0 || string.IsNullOrWhiteSpace(device.Path))
            throw new InvalidDataException("Unknown B20 vendor HID descriptor.");
        return device;
    }
}
[SupportedOSPlatform("windows")]
public sealed class WindowsPickupPattern(IAudioBackend audio) : IPickupPatternBackend
{
    // Serialize this app's queries; selection changes can cancel pending work.
    private readonly SemaphoreSlim queryLock = new(1, 1);
    public async Task<PickupPatternState> ReadAsync(AudioEndpoint endpoint, CancellationToken cancellation = default)
    {
        B20PatternProtocol.ValidateEndpoint(endpoint);
        await queryLock.WaitAsync(cancellation).ConfigureAwait(false);
        try {
            // Native enumeration/opening stays off the WPF dispatcher. No cached HID handle survives reconnect.
            return await Task.Run(async () => {
                cancellation.ThrowIfCancellationRequested();
                var device = B20PatternDeviceGuard.Select(endpoint, audio.Discover(), Enumerate());
                var backend = new PickupPatternBackend(_ => new Session(device.Path));
                var state = await backend.ReadAsync(endpoint, cancellation).ConfigureAwait(false);
                if (B20PatternDeviceGuard.Select(endpoint, audio.Discover(), Enumerate()) != device)
                    throw new IOException("B20 HID interface changed during the pattern query.");
                return state;
            }, cancellation).ConfigureAwait(false);
        } finally { queryLock.Release(); }
    }
    public static IReadOnlyList<B20PatternHidDevice> Enumerate()
    {
        Native.HidD_GetHidGuid(out var guid);
        var set = Native.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var devices = new List<B20PatternHidDevice>();
        try {
            for (uint index = 0; ; index++) {
                var data = new Native.InterfaceData { Size = Marshal.SizeOf<Native.InterfaceData>() };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data)) {
                    var error = Marshal.GetLastWin32Error(); if (error == 259) break; throw new Win32Exception(error);
                }
                var info = new Native.DeviceInfo { Size = Marshal.SizeOf<Native.DeviceInfo>() };
                Native.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var required, ref info);
                if (required is < 6 or > 65536) throw new InvalidDataException("Invalid HID detail size.");
                var detail = Marshal.AllocHGlobal((int)required);
                try {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out _, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                    if (path is null || !path.Contains("vid_1395&pid_009f", StringComparison.OrdinalIgnoreCase)) continue;
                    var instance = new StringBuilder(1024);
                    if (!Native.SetupDiGetDeviceInstanceId(set, ref info, instance, (uint)instance.Capacity, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var usb = CoreAudioBackend.Native.FindUsbAncestor(instance.ToString());
                    using var handle = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var attrs = new Native.Attributes { Size = Marshal.SizeOf<Native.Attributes>() };
                    if (!Native.HidD_GetAttributes(handle, ref attrs)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (attrs.VendorId != 0x1395 || attrs.ProductId != 0x009F) continue;
                    if (!Native.HidD_GetPreparsedData(handle, out var parsed)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    try {
                        if (Native.HidP_GetCaps(parsed, out var caps) != 0x00110000) throw new InvalidDataException("Cannot read B20 HID caps.");
                        devices.Add(new(path, usb, caps.UsagePage, caps.Usage, caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength));
                    } finally { Native.HidD_FreePreparsedData(parsed); }
                } finally { Marshal.FreeHGlobal(detail); }
            }
        } finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return devices;
    }
    private sealed class Session : IPickupPatternSession
    {
        private readonly FileStream stream;
        private bool querySent;
        public Session(string path)
        {
            var handle = Native.CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            try { stream = new FileStream(handle, FileAccess.ReadWrite, 1, true); }
            catch { handle.Dispose(); throw; }
        }
        public async Task SendStatusQueryAsync(ReadOnlyMemory<byte> query, CancellationToken cancellation)
        {
            if (!query.Span.SequenceEqual(B20PatternProtocol.StatusQuery)) throw new InvalidOperationException("Only the recovered pattern status query is allowed.");
            if (querySent) throw new InvalidOperationException("A status query was already sent on this session.");
            querySent = true;
            // One HID client, write then read; disable buffering for exact report I/O.
            await stream.WriteAsync(query, cancellation).ConfigureAwait(false);
        }
        public async Task<byte[]> ReadReportAsync(CancellationToken cancellation)
        {
            if (!querySent) throw new InvalidOperationException("Send the status query before reading its response.");
            var report = new byte[4]; var size = await stream.ReadAsync(report, cancellation).ConfigureAwait(false);
            if (size != report.Length) throw new IOException($"Short B20 HID report: {size} bytes.");
            return report;
        }
        public void Dispose() => stream.Dispose();
    }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData { public int Size; public Guid ClassGuid; public uint Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] internal struct DeviceInfo { public int Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] internal struct Attributes { public int Size; public ushort VendorId, ProductId, Version; }
        [StructLayout(LayoutKind.Sequential)] internal struct Caps {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
            public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }
        [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, ref DeviceInfo device);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DeviceInfo device, StringBuilder id, uint size, out uint required);
        [DllImport("setupapi.dll")] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFile(string name, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetAttributes(SafeFileHandle file, ref Attributes attrs);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle file, out IntPtr data);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    }
}
