using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Timbre.Core;

public sealed record SessionHandoffResult(bool Passed, int VendorProcessId, bool DevicePresent, string? Error);

// A supervised host keeps its lease through vendor ownership inspection even if
// the supervisor disappears. Recovery never stops Windows Audio or writes DSP.
[SupportedOSPlatform("windows")]
public static class WindowsSessionRecovery
{
    public static SessionHandoffResult Recover(string product, string instance, IAudioBackend audio)
    {
        try {
            if (product is not ("0098" or "009f")) throw new ArgumentException("Mapped product required.");
            var manager = OpenSCManagerW(null, null, 1);
            if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var service = IntPtr.Zero;
            try {
                service = OpenServiceW(manager, "EPOSGamingSuiteService", 4 | 16);
                if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastPInvokeError());
                if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<Status>(), out _)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                if (status.State == 1 && !StartServiceW(service, 0, IntPtr.Zero) && Marshal.GetLastPInvokeError() != 1056) throw new Win32Exception(Marshal.GetLastPInvokeError());
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed.TotalSeconds < 45) {
                    if (!QueryServiceStatusEx(service, 0, out status, Marshal.SizeOf<Status>(), out _)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                    if (status.State == 1 && !StartServiceW(service, 0, IntPtr.Zero) && Marshal.GetLastPInvokeError() != 1056)
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    if (status.State == 4 && status.ProcessId > 0) {
                        var devices = audio.Discover();
                        var present = devices.Any(e => e.Usb is { } usb && usb.VendorId.Equals("1395", StringComparison.OrdinalIgnoreCase) &&
                            usb.ProductId.Equals(product, StringComparison.OrdinalIgnoreCase) && usb.InstanceId.Equals(instance, StringComparison.OrdinalIgnoreCase));
                        if (!present) return new(true, (int)status.ProcessId, false, null);
                        var owners = EposResearch.ApoHandleOwners.Read(product);
                        var names = owners.Where(owner => owner.ProcessId == status.ProcessId).Select(owner => owner.ObjectName).ToHashSet();
                        var prefix = "Global\\CF4B411F-BE2B-4D84-8106-EC27CA0F8F05_1395_" + product;
                        if (new[] { "_memory", "_event", "_mutex" }.All(suffix => names.Contains(prefix + suffix)))
                            return new(true, (int)status.ProcessId, true, null);
                    }
                    Thread.Sleep(250);
                }
                return new(false, (int)status.ProcessId, true, "Vendor ownership was not observed within 45 seconds.");
            } finally { if (service != IntPtr.Zero) CloseServiceHandle(service); CloseServiceHandle(manager); }
        } catch (Exception ex) { return new(false, 0, true, ex.Message); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Status { public uint Type, State, Controls, ExitCode, SpecificExitCode, CheckPoint, WaitHint, ProcessId, Flags; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatusEx(IntPtr service, int level, out Status status, int size, out int needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StartServiceW(IntPtr service, int count, IntPtr arguments);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
}
