using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Timbre.Core;

public sealed record SupportSessionStatus(string State, string Message, string? ReportDirectory = null)
{
    public bool Active => State is "Starting" or "Running" or "Recovering";
    public bool Paused => State is "Starting" or "Recovering";
}
public interface ISupportSession
{
    string? UnavailableReason { get; }
    SupportSessionStatus Start(IReadOnlyList<AudioEndpoint> endpoints);
    SupportSessionStatus Stop();
    SupportSessionStatus Poll();
}

// The elevated supervisor owns service changes and recovery. The app only launches,
// requests cooperative stop and consumes reports bound to this unique session.
public sealed class SupportSession : ISupportSession
{
    private readonly HelperInstallation installation;
    private readonly string data;
    private readonly Func<ProcessStartInfo, IGsxPilotProcess> launch;
    private IGsxPilotProcess? process;
    private string? instance;
    private SupportSessionStatus status = new("Idle", "Background support is off. Start it explicitly for this app session.");
    public SupportSession(string buildDirectory, string dataDirectory) : this(buildDirectory, dataDirectory,
        info => new SessionProcess(Process.Start(info) ?? throw new IOException("Support supervisor did not start."))) { }
    internal SupportSession(string buildDirectory, string dataDirectory, Func<ProcessStartInfo, IGsxPilotProcess> launch)
    { installation = new(buildDirectory); data = Path.GetFullPath(dataDirectory); this.launch = launch; }
    public string? UnavailableReason => status.Active ? "Background support is already active." : installation.UnavailableReason("Start-SupportSession.ps1");
    public SupportSessionStatus Start(IReadOnlyList<AudioEndpoint> endpoints)
    {
        if (UnavailableReason is { } reason) throw new InvalidOperationException(reason);
        var pair = GsxProcessingRestoreSession.SelectPair(endpoints);
        var b20 = endpoints.Where(e => e.Direction == AudioDirection.Microphone && e.Usb is { } usb &&
            usb.VendorId.Equals("1395", StringComparison.OrdinalIgnoreCase) && usb.ProductId.Equals("009f", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (b20.Length > 1) throw new InvalidOperationException("Background support requires at most one physical B20 microphone.");
        instance = pair.Microphone.Usb!.InstanceId;
        var report = Path.Combine(installation.Root!, "artifacts", "support-session", Guid.NewGuid().ToString("N"));
        using var controller = Process.GetCurrentProcess();
        var info = new ProcessStartInfo { FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"), UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = installation.Root! };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(installation.Root!, "tools", "Start-SupportSession.ps1"),
            "-BuildDirectory", installation.Build, "-StateDirectory", data, "-ReportDirectory", report,
            "-ExpectedDeviceInstance", instance, "-ControllerProcessId", Environment.ProcessId.ToString(), "-ControllerStartedUtc", controller.StartTime.ToUniversalTime().ToString("o") })
            info.ArgumentList.Add(argument);
        if (b20.Length == 1) { info.ArgumentList.Add("-ExpectedB20Instance"); info.ArgumentList.Add(b20[0].Usb!.InstanceId); }
        try { process = launch(info); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new InvalidOperationException("Administrator approval was canceled. Background support was not started.", ex); }
        return status = new("Starting", "Starting background support…", report);
    }
    public SupportSessionStatus Stop()
    {
        if (!status.Active || status.ReportDirectory is null) return status;
        Directory.CreateDirectory(status.ReportDirectory);
        File.WriteAllText(Path.Combine(status.ReportDirectory, "stop-session"), "stop");
        return status = status with { State = "Recovering", Message = "Returning support to EPOS. Keep devices connected until recovery finishes…" };
    }
    public SupportSessionStatus Poll()
    {
        if (!status.Active || status.ReportDirectory is null) return status;
        try {
            var summary = Path.Combine(status.ReportDirectory, "summary.json");
            if (File.Exists(summary)) {
                using var report = Read(summary); var value = report.RootElement;
                var passed = RecoveryPassed(value, instance!);
                var reason = value.TryGetProperty("Errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0 &&
                    errors[0].ValueKind == JsonValueKind.String ? errors[0].GetString() : null;
                process?.Dispose(); process = null;
                return status = status with { State = passed ? "Stopped" : "Failed", Message = passed
                    ? "Background support stopped. Latest settings were preserved and EPOS support recovered."
                    : "Background support failed. " + (reason is null ? "Review the reports and EPOS service state." : reason[..Math.Min(reason.Length, 240)]) };
            }
            if (Exited()) {
                process?.Dispose(); process = null;
                return status = status with { State = "Failed", Message = "The supervisor exited without a recovery report. Check EPOS support before continuing." };
            }
            var heartbeat = Path.Combine(status.ReportDirectory, "session.json");
            if (File.Exists(heartbeat)) {
                using var report = Read(heartbeat); var value = report.RootElement;
                if (value.GetProperty("DeviceInstance").GetString() != instance) throw new InvalidDataException("Session device identity differs.");
                var state = value.GetProperty("State").GetString();
                if (state == "Running" && status.State != "Recovering") {
                    if (value.GetProperty("ServiceState").GetString() != "Stopped") throw new InvalidDataException("Vendor service was not stopped.");
                    status = status with { State = "Running", Message = "Background support is active. Controls and explicit saved-state policies remain available." };
                } else if (state == "Recovering") status = status with { State = "Recovering", Message = "Returning support to EPOS and checking latest settings…" };
            }
            return status;
        } catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException) {
            if (Exited()) { process?.Dispose(); process = null; return status = status with { State = "Failed", Message = "Session recovery evidence is unreadable: " + ex.Message }; }
            // A broken readiness report cannot leave competing UI edits enabled.
            Stop();
            return status = status with { State = "Recovering", Message = "Session evidence is unreadable; requesting recovery…" };
        }
    }
    private bool Exited() { try { return process?.HasExited == true; } catch (Win32Exception) { return false; } }
    internal static bool RecoveryPassed(JsonElement value, string instance)
    {
        bool True(string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.True;
        bool Text(string name, string expected) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String && item.GetString() == expected;
        bool Empty(string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Array && item.GetArrayLength() == 0;
        bool False(string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.False;
        return Text("DeviceInstance", instance) && Text("Outcome", "Passed") && Text("FinalServiceState", "Running") && Text("FinalAudioServiceState", "Running") &&
            False("PrepareOnly") && True("ServiceStopAttempted") && True("VendorHandoffObserved") && True("HelpersStopped") && True("LatestControlsPreserved") && True("FullBuffersPreserved") && Empty("Errors") && Empty("SettingsDifferences");
    }
    private static JsonDocument Read(string path)
    { if (new FileInfo(path).Length > 262144) throw new InvalidDataException("Session report exceeds its size limit.");
        var bytes = File.ReadAllBytes(path); if (bytes.Length > 262144) throw new InvalidDataException("Session report exceeds its size limit."); return JsonDocument.Parse(bytes); }
    private sealed class SessionProcess(Process process) : IGsxPilotProcess
    { public bool HasExited => process.HasExited; public void Dispose() => process.Dispose(); }
}

public sealed class DemoSupportSession : ISupportSession
{
    public string? UnavailableReason => Status.Active ? "Demo support is already active." : null;
    public SupportSessionStatus Status { get; set; } = new("Idle", "Demo support never changes services or starts processes.");
    public int Starts { get; private set; }
    public SupportSessionStatus Start(IReadOnlyList<AudioEndpoint> endpoints)
    { if (UnavailableReason is { } reason) throw new InvalidOperationException(reason); GsxProcessingRestoreSession.SelectPair(endpoints); Starts++; return Status = new("Starting", "Demo support starting…"); }
    public SupportSessionStatus Stop() => Status.Active ? Status = new("Recovering", "Demo support recovering…") : Status;
    public SupportSessionStatus Poll() => Status;
}
