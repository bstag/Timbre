using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace EposControl.Core;

public sealed record GsxSidetoneHidDevice(string Path, UsbIdentity? Usb, ushort UsagePage, ushort Usage,
    int InputBytes, int OutputBytes, int FeatureBytes);
public static class GsxSidetoneDeviceGuard
{
    public static GsxSidetoneHidDevice Select(AudioEndpoint selected, IReadOnlyList<AudioEndpoint> active, IReadOnlyList<GsxSidetoneHidDevice> interfaces)
    {
        GsxSidetoneProtocol.ValidateEndpoint(selected);
        if (string.IsNullOrWhiteSpace(selected.Usb!.InstanceId) || !selected.Usb.InstanceId.StartsWith("USB\\VID_1395&PID_0098\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GSX physical identity is missing.");
        if (active.Count(e => e.Id == selected.Id && ProfileStore.DeviceIdentity(e) == ProfileStore.DeviceIdentity(selected)) != 1)
            throw new InvalidOperationException("The selected GSX microphone disconnected or changed identity.");
        var matching = interfaces.Where(d => d.Usb is { } usb && usb.VendorId == "1395" && usb.ProductId == "0098" &&
            string.Equals(usb.InstanceId, selected.Usb.InstanceId, StringComparison.OrdinalIgnoreCase) && d.UsagePage == 0x000C && d.Usage == 1).ToArray();
        if (matching.Length != 1) throw new InvalidOperationException("Cannot identify one GSX control collection for this physical device.");
        var device = matching[0];
        if (device.InputBytes != 35 || device.OutputBytes != 39 || device.FeatureBytes != 0 || string.IsNullOrWhiteSpace(device.Path) ||
            !device.Path.StartsWith("\\\\?\\hid#vid_1395&pid_0098&mi_03&col01#", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unknown GSX sidetone HID descriptor.");
        return device;
    }
}
[SupportedOSPlatform("windows")]
public static class WindowsGsxSidetone
{
    public static IGsxSidetoneBackend Create(IAudioBackend audio) => new GsxSidetoneBackend(endpoint => new Session(audio, endpoint));
    private sealed class Session : IGsxSidetoneSession
    {
        private readonly IAudioBackend audio;
        private readonly AudioEndpoint endpoint;
        private readonly Mutex mutex;
        private readonly GsxSidetoneHidDevice device;
        private bool owned;
        private bool disposed;
        private readonly int thread = Environment.CurrentManagedThreadId;
        public Session(IAudioBackend audio, AudioEndpoint endpoint)
        {
            this.audio = audio; this.endpoint = endpoint;
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.Usb!.InstanceId.ToUpperInvariant())));
            mutex = new Mutex(false, "Local\\EPOS-Control-GsxSidetone-" + key);
            try {
                try { owned = mutex.WaitOne(1000); }
                catch (AbandonedMutexException) { owned = true; throw new IOException("A previous GSX controller stopped unexpectedly. Reload to revalidate hardware."); }
                if (!owned) throw new TimeoutException("Another GSX sidetone operation is busy.");
                device = GsxSidetoneDeviceGuard.Select(endpoint, audio.Discover(), GsxHidInventory.Enumerate());
            } catch { Dispose(); throw; }
        }
        private void Check(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(disposed, this);
            if (thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Use a GSX transaction on its owning thread.");
            var current = GsxSidetoneDeviceGuard.Select(endpoint, audio.Discover(), GsxHidInventory.Enumerate());
            if (current != device) throw new IOException("GSX HID identity changed during the transaction.");
        }
        public GsxSidetoneState Read(CancellationToken cancellation)
        {
            Check(cancellation);
            var value = QueryAsync(device.Path, cancellation).GetAwaiter().GetResult();
            Check(cancellation);
            return new(endpoint.Usb!.InstanceId.ToUpperInvariant() + "|" + device.Path, value);
        }
        public void Write(GsxSidetoneSettings desired, CancellationToken cancellation)
        {
            desired.Validate(); Check(cancellation);
            WriteAsync(device.Path, desired, cancellation).GetAwaiter().GetResult(); Check(cancellation);
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (owned) { mutex.ReleaseMutex(); owned = false; } mutex.Dispose();
        }
    }
    private static async Task<GsxSidetoneSettings> QueryAsync(string path, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(1200);
        try {
            // A new handle for every request avoids reusing queued reports from an earlier query.
            using var handle = Open(path, 0xC0000000);
            using var stream = new FileStream(handle, FileAccess.ReadWrite, 1, true);
            await stream.WriteAsync(GsxSidetoneProtocol.Query, deadline.Token).ConfigureAwait(false);
            var report = new byte[35]; var count = await stream.ReadAsync(report, deadline.Token).ConfigureAwait(false);
            if (count != report.Length) throw new IOException("Short GSX sidetone status report.");
            return GsxSidetoneProtocol.Decode(report);
        } catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new TimeoutException("GSX sidetone did not respond in time."); }
    }
    private static async Task WriteAsync(string path, GsxSidetoneSettings settings, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(1200);
        try {
            using var handle = Open(path, 0x40000000);
            using var stream = new FileStream(handle, FileAccess.Write, 1, true);
            await stream.WriteAsync(GsxSidetoneProtocol.Set(settings), deadline.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new TimeoutException("GSX sidetone write timed out."); }
    }
    private static SafeFileHandle Open(string path, uint access)
    {
        var handle = CreateFile(path, access, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        return handle;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
}
