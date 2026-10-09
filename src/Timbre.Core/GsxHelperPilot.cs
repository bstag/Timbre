using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Timbre.Core;

public sealed record GsxHelperPilotStatus(bool Active, string Message, string? ReportDirectory = null, bool? Passed = null);
public interface IGsxHelperPilot
{
    string? UnavailableReason { get; }
    GsxHelperPilotStatus Start(IReadOnlyList<AudioEndpoint> endpoints, bool reconnect);
    GsxHelperPilotStatus Poll();
}

// Owns a guided administrator validation process, not the processing objects or
// Windows services. The existing bounded runner owns baseline capture and recovery.
public sealed class GsxHelperPilot : IGsxHelperPilot
{
    private readonly string? root;
    private readonly string build;
    private readonly Func<ProcessStartInfo, IGsxPilotProcess> launch;
    private IGsxPilotProcess? process;
    private string? instance;
    private bool reconnect;
    private GsxHelperPilotStatus status = new(false, "No helper check has run in this app session.");
    public GsxHelperPilot(string buildDirectory) : this(buildDirectory, StartElevated) { }
    internal GsxHelperPilot(string buildDirectory, Func<ProcessStartInfo, IGsxPilotProcess> launch)
    {
        build = Path.GetFullPath(buildDirectory); this.launch = launch;
        for (var directory = new DirectoryInfo(build); directory is not null; directory = directory.Parent) {
            if (!File.Exists(Path.Combine(directory.FullName, "tools", "Test-GsxInitialization.ps1"))) continue;
            root = directory.FullName; break;
        }
    }
    public string? UnavailableReason {
        get {
            if (status.Active) return "A helper check is already running.";
            if (root is null) return "Helper checks require a verified source checkout. The portable app does not include this experimental helper.";
            var copies = new[] { Path.Combine(build, "Timbre.Core.dll"), Path.Combine(build, "host", "Timbre.Core.dll"),
                Path.Combine(root, "tests", "Timbre.Tests", "bin", "Release", "net9.0", "Timbre.Core.dll") };
            var required = copies.Concat(new[] { Path.Combine(build, "Timbre.exe"), Path.Combine(build, "host", "Timbre.Host.exe"),
                Path.Combine(root, "tests", "Timbre.Tests", "bin", "Release", "net9.0", "Timbre.Tests.dll") });
            try {
                if (required.Any(path => !File.Exists(path))) return "Build and verify the app, helper and tests before running a helper check.";
                if (copies.Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).Distinct().Count() != 1)
                    return "App, helper and test binaries differ. Build and verify matching outputs first.";
                return null;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "Cannot verify helper files: " + ex.Message; }
        }
    }
    public GsxHelperPilotStatus Start(IReadOnlyList<AudioEndpoint> endpoints, bool reconnect)
    {
        if (UnavailableReason is { } reason) throw new InvalidOperationException(reason);
        var pair = GsxProcessingRestoreSession.SelectPair(endpoints);
        var report = Path.Combine(root!, "artifacts", "gsx-initialization", "ui-pilot-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(report) || File.Exists(report)) throw new IOException("Choose a new helper report directory.");
        var info = new ProcessStartInfo {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Normal, WorkingDirectory = root!
        };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root!, "tools", "Test-GsxInitialization.ps1"),
            "-ManagedLifecycle", "-StopSuiteTemporarily", "-BuildDirectory", build, "-ReportDirectory", report,
            "-ExpectedDeviceInstance", pair.Microphone.Usb!.InstanceId }) info.ArgumentList.Add(argument);
        if (reconnect) info.ArgumentList.Add("-ManagedReconnect");
        try { process = launch(info); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new InvalidOperationException("Administrator approval was canceled. The helper check was not started.", ex); }
        instance = pair.Microphone.Usb!.InstanceId; this.reconnect = reconnect;
        return status = new(true, "Preparing the helper check in administrator PowerShell. Keep other controllers idle.", report);
    }
    public GsxHelperPilotStatus Poll()
    {
        if (!status.Active || status.ReportDirectory is null) return status;
        try {
            var summaryPath = Path.Combine(status.ReportDirectory, "summary.json");
            if (File.Exists(summaryPath)) {
                using var summary = Read(summaryPath);
                var report = summary.RootElement;
                var passed = IsPassed(report, instance!, reconnect);
                var message = passed ? "Helper check passed. Starting controls and service state were restored."
                    : "Helper check failed or recovery was incomplete. Review the reports before another check.";
                process?.Dispose(); process = null;
                return status = new(false, message, status.ReportDirectory, passed);
            }
            if (RunnerExited()) {
                process?.Dispose(); process = null;
                return status = new(false, "The administrator runner exited without a recovery report. Check its output and service state.", status.ReportDirectory, false);
            }
            var heartbeat = Path.Combine(status.ReportDirectory, "managed", "heartbeat.json");
            if (File.Exists(heartbeat)) {
                using var document = Read(heartbeat);
                var value = document.RootElement;
                var state = value.GetProperty("State").GetString();
                var generation = value.GetProperty("Generation").GetInt32();
                var message = state switch {
                    "WaitingForDevice" => "Helper observed removal. Follow the PowerShell prompt to reconnect GSX.",
                    "Connected" => $"Helper connected · connection {generation}. Checking controls and recovery…",
                    "Retrying" => "Helper is retrying connection within its bounded limit…",
                    _ => "Helper reported a failure. The runner is checking recovery…"
                };
                status = status with { Message = message };
            }
            return status;
        } catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException) {
            // Summary writes can be observed while incomplete. Keep the runner's
            // controls paused, and never turn malformed evidence into success.
            if (RunnerExited()) {
                process?.Dispose(); process = null;
                return status = new(false, "Helper recovery report is unreadable. Review its output and service state: " + ex.Message, status.ReportDirectory, false);
            }
            return status with { Message = "Waiting for a complete helper report…" };
        }
    }
    private bool RunnerExited()
    {
        try { return process?.HasExited == true; }
        catch (Win32Exception) { return false; } // Recovery evidence remains the authority when Windows hides elevated process details.
    }
    internal static bool IsPassed(JsonElement report, string instance, bool reconnect)
    {
        bool True(string name) => report.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        bool Empty(string name) => report.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0;
        string? Text(string name) => report.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var b20 = report.TryGetProperty("FullB20BufferPreserved", out var preserved) && preserved.ValueKind == JsonValueKind.Null &&
            report.TryGetProperty("FullB20BufferPreservedAfterRelease", out var released) && released.ValueKind == JsonValueKind.Null ||
            True("FullB20BufferPreserved") && True("FullB20BufferPreservedAfterRelease");
        return Text("Outcome") == "Passed" && Text("DeviceInstance")?.Equals(instance, StringComparison.OrdinalIgnoreCase) == true &&
            True("Prepared") && True("AutomaticRestore") && Text("ManagedOutcome") == "Passed" &&
            True("ServiceStopRequested") && True("ServiceStopAttempted") && True("ManagedLifecycleRequested") && True("ManagedRestoreVerified") &&
            Text("ControlOutcome") == "Passed" && True("VendorHandoffObserved") && True("SavedStateUnchanged") &&
            True("FullGsxBufferRestored") && True("FullGsxBufferRestoredAfterRelease") && b20 &&
            Text("FinalServiceState") == "Running" && Text("FinalAudioServiceState") == "Running" && Empty("Errors") && Empty("SettingsDifferences") &&
            (!reconnect || True("ManagedReconnectRequested") && True("DisconnectObserved") && True("ReturnObserved") && True("ManagedRemovalObserved") &&
                True("ManagedReconnectRecoveryTested") && report.TryGetProperty("ManagedReconnectGeneration", out var generation) && generation.ValueKind == JsonValueKind.Number && generation.TryGetInt32(out var count) && count == 2);
    }
    private static JsonDocument Read(string path)
    {
        if (new FileInfo(path).Length > 262144) throw new InvalidDataException("Helper report exceeds the size limit.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 262144) throw new InvalidDataException("Helper report exceeds the size limit.");
        return JsonDocument.Parse(bytes);
    }
    private static IGsxPilotProcess StartElevated(ProcessStartInfo info)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return new PilotProcess(Process.Start(info) ?? throw new IOException("The administrator runner did not start."));
    }
    private sealed class PilotProcess(Process process) : IGsxPilotProcess
    {
        public bool HasExited => process.HasExited;
        public void Dispose() => process.Dispose(); // Never kills the runner during recovery.
    }
}
internal interface IGsxPilotProcess : IDisposable { bool HasExited { get; } }

public sealed class DemoGsxHelperPilot : IGsxHelperPilot
{
    public string? UnavailableReason => Status.Active ? "A demo helper check is already running." : null;
    public GsxHelperPilotStatus Status { get; set; } = new(false, "Demo helper checks do not launch processes or change services.");
    public int Starts { get; private set; }
    public bool LastReconnect { get; private set; }
    public GsxHelperPilotStatus Start(IReadOnlyList<AudioEndpoint> endpoints, bool reconnect)
    {
        if (UnavailableReason is { } reason) throw new InvalidOperationException(reason);
        GsxProcessingRestoreSession.SelectPair(endpoints); Starts++; LastReconnect = reconnect;
        return Status = new(true, "Demo helper connected · no hardware or service changes.");
    }
    public GsxHelperPilotStatus Poll() => Status;
}
