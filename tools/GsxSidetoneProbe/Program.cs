using System.Text.Json;
using EposResearch;

if (args.Contains("--self-test")) return QueryGuardTests.Run();
if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows HID probe only.");
var query = args.Contains("--query");
string Option(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var destination = Option("--report") ?? throw new ArgumentException("Specify --report output.json.");
var instance = Option("--instance");
var restoreFrom = Option("--restore-from");
if (restoreFrom != null && !query) throw new ArgumentException("Restoration requires --query for fresh verification.");
if (query && instance == null) throw new ArgumentException("A query requires --instance with the exact physical USB instance from diagnostics.");
var interfaces = HidProbe.Enumerate();
byte[] response = null;
HidInterface selected = null;
string error = null;
var restoreAttempted = false;
var restoreVerified = false;
try {
    if (query) {
        selected = GsxSidetoneQuery.Select(instance, interfaces);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        byte[] original = null;
        if (restoreFrom != null) {
            using var originalJson = JsonDocument.Parse(File.ReadAllText(restoreFrom));
            var root = originalJson.RootElement;
            if (root.GetProperty("Error").ValueKind != JsonValueKind.Null || !root.GetProperty("QueryRequested").GetBoolean() ||
                !string.Equals(root.GetProperty("Selected").GetProperty("UsbInstanceId").GetString(), instance, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Restoration requires a successful baseline for this physical GSX.");
            original = Convert.FromHexString(root.GetProperty("ResponseHex").GetString());
            _ = GsxSidetoneQuery.RestoreRequest(original);
            restoreAttempted = true;
            await GsxSidetoneQuery.RestoreCapturedAsync(selected, original, deadline.Token);
        }
        response = await GsxSidetoneQuery.CaptureAsync(selected, deadline.Token);
        GsxSidetoneQuery.Select(instance, HidProbe.Enumerate());
        if (original != null) {
            if (!response.SequenceEqual(original)) throw new IOException("Restored GSX status does not match its complete baseline response.");
            restoreVerified = true;
        }
    }
} catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
var report = new {
    CollectedAt = DateTimeOffset.UtcNow, ResearchOnly = true, SettingsWrites = restoreAttempted,
    RestoreAttempted = restoreAttempted, RestoreVerified = restoreVerified,
    QueryRequested = query, RequestHex = selected == null ? null : Convert.ToHexString(GsxSidetoneQuery.Request),
    ResponseHex = response == null ? null : Convert.ToHexString(response),
    // Static service getter reads response[1]. No claim of calibrated dB or mute semantics.
    CandidateHardwareByte = response == null ? (int?)null : response[1],
    LiveProtocolValidated = false, Selected = selected, Interfaces = interfaces, Error = error
};
File.WriteAllText(Path.GetFullPath(destination), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(error ?? (query ? "GSX status response captured; compare with Suite before using it." : "HID descriptors saved; no reports sent."));
return error == null ? 0 : 1;
