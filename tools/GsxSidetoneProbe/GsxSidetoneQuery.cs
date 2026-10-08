using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace EposResearch;

// Research-only getter and exact-capture recovery. No firmware or general report API.
public static class GsxSidetoneQuery
{
    public static byte[] Request {
        get { var report = new byte[39]; report[0] = 4; report[2] = 1; report[3] = 0x12; report[4] = 0x7B; return report; }
    }
    public static HidInterface Select(string physicalInstance, IReadOnlyList<HidInterface> interfaces)
    {
        if (string.IsNullOrWhiteSpace(physicalInstance) || !Regex.IsMatch(physicalInstance, @"^USB\\VID_1395&PID_0098\\[^\\]+$", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("An explicit GSX 300 physical USB instance is required.");
        var matching = interfaces.Where(d => string.Equals(d.UsbInstanceId, physicalInstance, StringComparison.OrdinalIgnoreCase) &&
            d.VendorId == "1395" && d.ProductId == "0098" && d.UsagePage == "000C" && d.Usage == "0001").ToArray();
        if (matching.Length != 1) throw new InvalidOperationException("Expected one descriptor-validated GSX control collection for this physical device.");
        var device = matching[0];
        if (!string.IsNullOrEmpty(device.Error) || device.InputReportBytes != 35 || device.OutputReportBytes != 39 || device.FeatureReportBytes != 0 ||
            string.IsNullOrWhiteSpace(device.Path) || !device.Path.StartsWith("\\\\?\\hid#vid_1395&pid_0098&mi_03&col01#", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unknown GSX control HID descriptor/path.");
        return device;
    }
    public static async Task<byte[]> CaptureAsync(HidInterface device, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        // Revalidate even if a caller supplies an interface directly.
        Select(device.UsbInstanceId, new[] { device });
        using var handle = CreateFile(device.Path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var stream = new FileStream(handle, FileAccess.ReadWrite, 1, true);
        await stream.WriteAsync(Request, cancellation).ConfigureAwait(false);
        var response = new byte[35];
        var count = await stream.ReadAsync(response, cancellation).ConfigureAwait(false);
        if (count != response.Length) throw new InvalidDataException($"Short GSX input report: {count} bytes.");
        ValidateResponse(response);
        return response;
    }
    public static void ValidateResponse(ReadOnlySpan<byte> response)
    {
        if (response.Length != 35 || response[0] != 5 || response[2..].IndexOfAnyExcept((byte)0) >= 0 ||
            (response[1] != 0 && response[1] < 199))
            throw new InvalidDataException("Not a supported complete GSX sidetone status capture.");
    }
    // Recovery only: exact value from an earlier, complete getter capture.
    // Setter layout recovered at 004D923E/004D9245/004D9599 in the installed service.
    public static byte[] RestoreRequest(ReadOnlySpan<byte> capturedResponse)
    {
        ValidateResponse(capturedResponse);
        var report = Request; report[1] = 0x40; report[5] = capturedResponse[1]; return report;
    }
    public static async Task RestoreCapturedAsync(HidInterface device, byte[] capturedResponse, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var report = RestoreRequest(capturedResponse);
        Select(device.UsbInstanceId, new[] { device });
        using var handle = CreateFile(device.Path, 0x40000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var stream = new FileStream(handle, FileAccess.Write, 1, true);
        await stream.WriteAsync(report, cancellation).ConfigureAwait(false);
    }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
}
