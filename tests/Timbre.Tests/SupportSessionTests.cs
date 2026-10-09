using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Timbre.Core;
using static GsxProcessingStateTests;

internal static class SupportSessionTests
{
    public static void Run(TestSuite suite)
    {
        suite.Case("Support session launch binds elevation, caller storage and controller process identity without creating reports", () => {
            using var rig = new Rig(); var started = rig.Session.Start(new DemoAudioBackend().Discover());
            var args = rig.Info!.ArgumentList.ToArray();
            TestSuite.Assert(started.State == "Starting" && rig.Starts == 1 && !Directory.Exists(started.ReportDirectory) && rig.Info.Verb == "runas" && rig.Info.WindowStyle == ProcessWindowStyle.Hidden);
            TestSuite.Assert(args.Contains("-ControllerProcessId") && args.Contains(Environment.ProcessId.ToString()) && args.Contains("-ControllerStartedUtc") &&
                args.Contains("-ExpectedB20Instance") && args.Contains(rig.Data) && !args.Contains("-RestartAudioEngine") && !args.Contains("-PrepareOnly"));
            TestSuite.Throws<InvalidOperationException>(() => rig.Session.Start([Mic, Output])); TestSuite.Assert(rig.Starts == 1);
        });
        suite.Case("Support session refuses incomplete GSX and ambiguous B20 before elevation", () => {
            using var rig = new Rig(); var b20 = new DemoAudioBackend().Discover().First();
            TestSuite.Reject(() => rig.Session.Start([Mic])); TestSuite.Reject(() => rig.Session.Start([Mic, Output, b20, b20 with { Id = "duplicate" }]));
            TestSuite.Assert(rig.Starts == 0);
        });
        suite.Case("Support session cancellation preserves idle state and all files", () => {
            using var rig = new Rig(info => throw new System.ComponentModel.Win32Exception(1223));
            TestSuite.Throws<InvalidOperationException>(() => rig.Session.Start([Mic, Output]));
            TestSuite.Assert(!rig.Session.Poll().Active && !Directory.Exists(Path.Combine(rig.Root, "artifacts", "support-session")));
        });
        suite.Case("Support session missing or mismatched installation cannot launch", () => {
            using var rig = new Rig(); File.WriteAllText(Path.Combine(rig.Build, "host", "Timbre.Core.dll"), "other");
            TestSuite.Assert(rig.Session.UnavailableReason is not null); TestSuite.Reject(() => rig.Session.Start([Mic, Output])); TestSuite.Assert(rig.Starts == 0);
        });
        suite.Case("Support running permits edits, cooperative stop cannot be undone by stale running heartbeat", () => {
            using var rig = new Rig(); var started = rig.Session.Start([Mic, Output]); var directory = started.ReportDirectory!;
            Write(directory, "session.json", new { State = "Running", DeviceInstance = Mic.Usb!.InstanceId, ServiceState = "Stopped" });
            TestSuite.Assert(rig.Session.Poll() is { State: "Running", Paused: false, Active: true });
            TestSuite.Assert(rig.Session.Stop() is { State: "Recovering", Paused: true } && File.Exists(Path.Combine(directory, "stop-session")));
            TestSuite.Assert(rig.Session.Poll().State == "Recovering" && rig.Process.Disposals == 0);
            Write(directory, "summary.json", Evidence()); TestSuite.Assert(rig.Session.Poll().State == "Stopped" && rig.Process.Disposals == 1);
            TestSuite.Assert(!rig.Session.Stop().Active);
        });
        suite.Case("Support malformed progress requests recovery without releasing a running supervisor", () => {
            using var rig = new Rig(); var directory = rig.Session.Start([Mic, Output]).ReportDirectory!;
            Write(directory, "session.json", new { State = "Running", DeviceInstance = "other", ServiceState = "Stopped" });
            TestSuite.Assert(rig.Session.Poll() is { State: "Recovering", Active: true } && File.Exists(Path.Combine(directory, "stop-session")) && rig.Process.Disposals == 0);
            File.WriteAllText(Path.Combine(directory, "summary.json"), "{"); TestSuite.Assert(rig.Session.Poll().Active);
            rig.Process.HasExited = true; TestSuite.Assert(rig.Session.Poll().State == "Failed" && rig.Process.Disposals == 1);
        });
        suite.Case("Support supervisor disappearance without final evidence cannot be reported as recovery", () => {
            using var rig = new Rig(); rig.Session.Start([Mic, Output]); rig.Process.HasExited = true;
            TestSuite.Assert(rig.Session.Poll().State == "Failed" && rig.Process.Disposals == 1);
        });
        suite.Case("Hidden support startup failures expose the runner reason instead of claiming recovery", () => {
            using var rig = new Rig(); var directory = rig.Session.Start([Mic, Output]).ReportDirectory!;
            Write(directory, "summary.json", new { Outcome = "Failed", DeviceInstance = Mic.Usb!.InstanceId, Errors = new[] { "Another support supervisor is already running." } });
            var status = rig.Session.Poll(); TestSuite.Assert(status.State == "Failed" && status.Message.Contains("Another support supervisor") && rig.Process.Disposals == 1);
        });
        foreach(var field in new[] { "ServiceStopAttempted", "VendorHandoffObserved", "HelpersStopped", "LatestControlsPreserved", "FullBuffersPreserved" })
            suite.Case("Support recovery refuses missing false or nonboolean evidence: " + field, () => {
                foreach(var bad in new JsonNode?[] { null, JsonValue.Create(false), JsonValue.Create("true") }) { var evidence = Evidence(); evidence[field] = bad; TestSuite.Assert(!Passed(evidence)); }
            });
        suite.Case("Support recovery refuses preparation-only, foreign devices, service failures and control differences", () => {
            TestSuite.Assert(Passed(Evidence()));
            foreach(var field in new[] { "Outcome", "DeviceInstance", "FinalServiceState", "FinalAudioServiceState" }) { var report = Evidence(); report[field] = "other"; TestSuite.Assert(!Passed(report)); }
            foreach(var field in new[] { "Errors", "SettingsDifferences" }) { var report = Evidence(); report[field] = new JsonArray("difference"); TestSuite.Assert(!Passed(report)); }
            foreach(var value in new JsonNode?[] { null, JsonValue.Create(true), JsonValue.Create("false") }) { var report = Evidence(); report["PrepareOnly"] = value; TestSuite.Assert(!Passed(report)); }
        });
        suite.Case("Supervisor lifetime refuses dead, reused or inaccessible process identities and accepts offset-equivalent instants", () => {
            var time = DateTimeOffset.Parse("2026-10-09T12:00:00Z");
            TestSuite.Assert(SupervisorIdentity.IsAlive(42, time, _ => time.ToOffset(TimeSpan.FromHours(-5))));
            TestSuite.Assert(!SupervisorIdentity.IsAlive(42, time, _ => null) && !SupervisorIdentity.IsAlive(42, time, _ => time.AddSeconds(1)) && !SupervisorIdentity.IsAlive(0, time, _ => time));
        });
        suite.Case("Long-running host durations require explicit managed supervisor identity and retain diagnostic bounds", () => {
            string[] common = ["--initial-state", "seed", "--state-directory", "store", "--device-instance", "device", "--report-directory", "report", "--stop-file", "stop", "--seconds", "86400"];
            TestSuite.Assert(ApoHostOptions.Parse(["--manage-gsx", .. common, "--supervisor-id", "42", "--supervisor-started-utc", "2026-10-09T12:00:00Z"]).Seconds == 86400);
            TestSuite.Assert(ApoHostOptions.Parse(["--manage-b20", .. common, "--supervisor-id", "42", "--supervisor-started-utc", "2026-10-09T12:00:00Z"]).SupervisorId == 42);
            TestSuite.Throws<ArgumentException>(() => ApoHostOptions.Parse(["--manage-gsx", .. common]));
            TestSuite.Throws<ArgumentException>(() => ApoHostOptions.Parse(["--manage-gsx", .. common, "--supervisor-id", "42"]));
            TestSuite.Throws<ArgumentException>(() => ApoHostOptions.Parse(["--manage-gsx", .. common, "--supervisor-id", "0", "--supervisor-started-utc", "2026-10-09T12:00:00Z"]));
            TestSuite.Throws<ArgumentException>(() => ApoHostOptions.Parse(["--validate-managed-gsx", .. common, "--supervisor-id", "42", "--supervisor-started-utc", "2026-10-09T12:00:00Z"]));
        });
        suite.Case("Support preparation validates both current seeds and saved policies without writing user data", () => {
            using var rig = new Rig(); var endpoints = new DemoAudioBackend().Discover(); var b20 = endpoints.First();
            var result = SupportSessionPreparer.Prepare(endpoints, Mic.Usb!.InstanceId, b20.Usb!.InstanceId, rig.Data, Capture);
            TestSuite.Assert(result.GsxSaved is null && result.B20Saved is null && result.B20Seed is not null && !Directory.Exists(rig.Data));
            TestSuite.Assert(result.GsxSeed.DiagnosticSeed.SequenceEqual(Seed()));
            TestSuite.Reject(() => SupportSessionPreparer.Prepare(endpoints, "other", b20.Usb.InstanceId, rig.Data, Capture));
            TestSuite.Reject(() => SupportSessionPreparer.Prepare(endpoints, Mic.Usb.InstanceId, null, rig.Data, Capture));
        });
        suite.Case("Support preparation refuses corrupt opted-in stores and unsupported GSX restores before service changes", () => {
            using var rig = new Rig(); var store = new GsxProcessingStateStore(Path.Combine(rig.Data, "gsx-processing-state")); var state = GsxProcessingState.Read(Seed());
            store.Remember(Mic, Output, state with { Microphone = state.Microphone with { Processing = state.Microphone.Processing! with { HighPassEnabled = !state.Microphone.Processing!.HighPassEnabled } } });
            store.SetRestoreOnConnect(Mic, Output, true);
            TestSuite.Reject(() => SupportSessionPreparer.Prepare([Mic, Output], Mic.Usb!.InstanceId, null, rig.Data, Capture));
            File.WriteAllText(store.StatePath(Mic, Output), "{}");
            TestSuite.Throws<JsonException>(() => SupportSessionPreparer.Prepare([Mic, Output], Mic.Usb!.InstanceId, null, rig.Data, Capture));
        });
        suite.Case("Session volatile GSX seed preserves latest edits across changed endpoint IDs without persisting or sharing byte arrays", () => {
            var cache = new SessionSeedCache(); var bytes = Seed(); var state = GsxProcessingState.Read(bytes); var desired = Desired(state);
            foreach(var patch in ApoMicrophoneCodec.Prepare(bytes, desired.Microphone).Concat(ApoPlaybackCodec.Prepare(bytes, desired.Playback))) patch.After.CopyTo(bytes, patch.Offset);
            cache.ObserveGsx(Mic, Output, bytes); var saved = cache.Gsx("missing", Mic with { Id = "new" }, Output with { Id = "new-output" });
            TestSuite.Assert(new GsxProcessingState(saved.Microphone, saved.Playback).Matches(desired));
            bytes[0] = 99; saved.DiagnosticSeed[0] = 99; TestSuite.Assert(cache.Gsx("missing", Mic, Output).DiagnosticSeed[0] != 99);
            TestSuite.Reject(() => cache.Gsx("missing", Mic with { Usb = Mic.Usb! with { InstanceId = "other" } }, Output));
        });
        suite.Case("Session volatile B20 seed preserves applied processing across renamed endpoints without touching saves", () => {
            var b20 = new DemoAudioBackend().Discover().First(); var bytes = Capture(b20); var cache = new SessionSeedCache();
            cache.ObserveB20(b20, bytes); var cached = cache.B20("missing", b20 with { Id = "new", Name = "renamed" });
            TestSuite.Assert(ApoMicrophoneCodec.Matches(cached.Effects, ApoMicrophoneCodec.Read(bytes)));
            TestSuite.Reject(() => cache.B20("missing", b20 with { Usb = b20.Usb! with { InstanceId = "other" } }));
        });
    }
    private static byte[] Capture(AudioEndpoint endpoint) => endpoint.Usb!.ProductId == "0098" ? Seed() : File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "apo-memory-009f.bin"));
    private static bool Passed(JsonObject value) { using var doc = JsonDocument.Parse(value.ToJsonString()); return SupportSession.RecoveryPassed(doc.RootElement, Mic.Usb!.InstanceId); }
    private static JsonObject Evidence() => new() { ["DeviceInstance"]=Mic.Usb!.InstanceId,["Outcome"]="Passed",["PrepareOnly"]=false,["ServiceStopAttempted"]=true,["FinalServiceState"]="Running",["FinalAudioServiceState"]="Running",
        ["VendorHandoffObserved"]=true,["HelpersStopped"]=true,["LatestControlsPreserved"]=true,["FullBuffersPreserved"]=true,["Errors"]=new JsonArray(),["SettingsDifferences"]=new JsonArray() };
    private static void Write(string directory, string file, object value) { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, file), JsonSerializer.Serialize(value)); }
    private sealed class Rig : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "timbre support's " + Guid.NewGuid().ToString("N"));
        public string Build => Path.Combine(Root, "artifacts", "verified build");
        public string Data => Path.Combine(Root, "user settings");
        public SupportSession Session { get; }
        public ProcessStartInfo? Info; public int Starts; public FakeProcess Process { get; } = new();
        public Rig(Func<ProcessStartInfo, IGsxPilotProcess>? launch = null)
        {
            foreach(var name in new[] { "tools/Test-GsxInitialization.ps1", "tools/Start-SupportSession.ps1", "artifacts/verified build/Timbre.exe", "artifacts/verified build/Timbre.Core.dll", "artifacts/verified build/host/Timbre.Host.exe", "artifacts/verified build/host/Timbre.Core.dll", "tests/Timbre.Tests/bin/Release/net9.0/Timbre.Core.dll", "tests/Timbre.Tests/bin/Release/net9.0/Timbre.Tests.dll" }) {
                var path = Path.Combine(Root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "matching");
            }
            Session = new(Build, Data, info => { Info = info; Starts++; return launch is null ? Process : launch(info); });
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
    private sealed class FakeProcess : IGsxPilotProcess { public bool HasExited { get; set; } public int Disposals; public void Dispose() => Disposals++; }
}
