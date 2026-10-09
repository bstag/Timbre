using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Timbre.Core;
using static GsxProcessingStateTests;

internal static class GsxHelperPilotTests
{
    public static void Run(TestSuite suite)
    {
        suite.Case("Guided GSX pilot resolves quoted source/build paths and uses literal elevated arguments", () => {
            using var rig = new Rig();
            var status = rig.Pilot.Start([Mic, Output], true);
            var info = rig.Info!;
            TestSuite.Assert(status.Active && rig.Starts == 1 && info.UseShellExecute && info.Verb == "runas" && info.ArgumentList.Contains("-ManagedReconnect") &&
                info.ArgumentList.Contains(rig.Build) && info.ArgumentList.Contains(Mic.Usb!.InstanceId) && !info.ArgumentList.Contains("-RestartAudioEngine") &&
                !Directory.Exists(status.ReportDirectory));
            TestSuite.Throws<InvalidOperationException>(() => rig.Pilot.Start([Mic, Output], false));
        });
        suite.Case("Guided GSX pilot refuses incomplete or ambiguous pairs before process launch", () => {
            using var rig = new Rig();
            foreach(var endpoints in new AudioEndpoint[][] { [Mic], [Output], [Mic, Output, Mic] })
                TestSuite.Reject(() => rig.Pilot.Start(endpoints, false));
            TestSuite.Assert(rig.Starts == 0);
        });
        suite.Case("Guided GSX pilot requires matching app helper and test binaries", () => {
            using var rig = new Rig(); File.WriteAllText(Path.Combine(rig.Build, "host", "Timbre.Core.dll"), "changed");
            TestSuite.Assert(rig.Pilot.UnavailableReason!.Contains("differ"));
            TestSuite.Reject(() => rig.Pilot.Start([Mic, Output], false)); TestSuite.Assert(rig.Starts == 0);
        });
        suite.Case("Guided GSX pilot is unavailable in a portable directory without the source runner", () => {
            var directory = Path.Combine(Path.GetTempPath(), "timbre-portable-" + Guid.NewGuid().ToString("N"));
            var pilot = new GsxHelperPilot(directory, _ => throw new Exception("Must not launch"));
            TestSuite.Assert(pilot.UnavailableReason!.Contains("source checkout"));
        });
        suite.Case("Canceled administrator launch does not leave a running session or modify reports", () => {
            using var rig = new Rig(); var pilot = new GsxHelperPilot(rig.Build, _ => throw new Win32Exception(1223));
            var error = TestSuite.Throws<InvalidOperationException>(() => pilot.Start([Mic, Output], false));
            TestSuite.Assert(error.Message.Contains("canceled") && !pilot.Poll().Active && pilot.UnavailableReason is null);
        });
        suite.Case("Guided GSX pilot waiting and connected reports show live progress without stopping its process", () => {
            using var rig = new Rig(); var status = rig.Pilot.Start([Mic, Output], true);
            var directory = Path.Combine(status.ReportDirectory!, "managed"); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "heartbeat.json");
            File.WriteAllText(path, "{\"State\":\"WaitingForDevice\",\"Generation\":1}");
            TestSuite.Assert(rig.Pilot.Poll() is { Active: true } waiting && waiting.Message.Contains("reconnect"));
            File.WriteAllText(path, "{\"State\":\"Connected\",\"Generation\":2}");
            TestSuite.Assert(rig.Pilot.Poll().Message.Contains("connection 2") && rig.Process.Disposals == 0);
        });
        suite.Case("Guided GSX pilot completion requires recovered settings and releases only the process handle", () => {
            using var rig = new Rig(); var status = rig.Pilot.Start([Mic, Output], true);
            Directory.CreateDirectory(status.ReportDirectory!); File.WriteAllText(Path.Combine(status.ReportDirectory!, "summary.json"), Evidence().ToJsonString());
            TestSuite.Assert(rig.Pilot.Poll() is { Active: false, Passed: true } && rig.Process.Disposals == 1 && !rig.Process.HasExited);
        });
        suite.Case("Guided GSX pilot malformed or oversized reports cannot become success or unblock a running check", () => {
            using var rig = new Rig(); var status = rig.Pilot.Start([Mic, Output], false); Directory.CreateDirectory(status.ReportDirectory!);
            var path = Path.Combine(status.ReportDirectory!, "summary.json"); File.WriteAllText(path, "{");
            TestSuite.Assert(rig.Pilot.Poll().Active && rig.Process.Disposals == 0);
            File.WriteAllText(path, new string(' ', 262145)); TestSuite.Assert(rig.Pilot.Poll().Active);
            rig.Process.HasExited = true; TestSuite.Assert(rig.Pilot.Poll() is { Active: false, Passed: false });
        });
        suite.Case("Guided GSX pilot runner exit without recovery evidence is a failure", () => {
            using var rig = new Rig(); rig.Pilot.Start([Mic, Output], false); rig.Process.HasExited = true;
            TestSuite.Assert(rig.Pilot.Poll() is { Active: false, Passed: false } && rig.Process.Disposals == 1);
        });
        foreach(var field in new[] { "Prepared", "AutomaticRestore", "ServiceStopRequested", "ServiceStopAttempted", "ManagedLifecycleRequested", "ManagedRestoreVerified", "VendorHandoffObserved", "SavedStateUnchanged",
            "FullGsxBufferRestored", "FullGsxBufferRestoredAfterRelease", "FullB20BufferPreserved", "FullB20BufferPreservedAfterRelease", "ManagedReconnectRequested", "DisconnectObserved", "ReturnObserved", "ManagedRemovalObserved", "ManagedReconnectRecoveryTested" })
            suite.Case("Guided GSX success refuses missing false or nonboolean recovery evidence: " + field, () => {
                foreach(var bad in new JsonNode?[] { null, JsonValue.Create(false), JsonValue.Create("true") }) {
                    var evidence = Evidence(); evidence[field] = bad;
                    TestSuite.Assert(!Passed(evidence));
                }
            });
        suite.Case("Guided GSX success refuses foreign devices, wrong states, errors and stale reconnect generations", () => {
            foreach(var field in new[] { "Outcome", "DeviceInstance", "ManagedOutcome", "ControlOutcome", "FinalServiceState", "FinalAudioServiceState" }) {
                var evidence = Evidence(); evidence[field] = "wrong"; TestSuite.Assert(!Passed(evidence));
            }
            foreach(var field in new[] { "Errors", "SettingsDifferences" }) {
                var evidence = Evidence(); evidence[field] = new JsonArray("failure"); TestSuite.Assert(!Passed(evidence));
            }
            foreach(var bad in new JsonNode?[] { null, JsonValue.Create(1), JsonValue.Create("2") }) {
                var evidence = Evidence(); evidence["ManagedReconnectGeneration"] = bad; TestSuite.Assert(!Passed(evidence));
            }
        });
        suite.Case("Guided GSX success can omit B20 preservation only when both recorded buffer results are null", () => {
            var evidence = Evidence(); evidence["FullB20BufferPreserved"] = null;
            TestSuite.Assert(!Passed(evidence)); evidence["FullB20BufferPreservedAfterRelease"] = null; TestSuite.Assert(Passed(evidence));
        });
    }
    private static bool Passed(JsonObject evidence) { using var document = JsonDocument.Parse(evidence.ToJsonString()); return GsxHelperPilot.IsPassed(document.RootElement, Mic.Usb!.InstanceId, true); }
    private static JsonObject Evidence() => new() {
        ["Outcome"]="Passed",["DeviceInstance"]=Mic.Usb!.InstanceId,["Prepared"]=true,["AutomaticRestore"]=true,["ManagedOutcome"]="Passed",
        ["ServiceStopRequested"]=true,["ServiceStopAttempted"]=true,["ManagedLifecycleRequested"]=true,["ManagedRestoreVerified"]=true,
        ["ControlOutcome"]="Passed",["VendorHandoffObserved"]=true,["SavedStateUnchanged"]=true,["FullGsxBufferRestored"]=true,["FullGsxBufferRestoredAfterRelease"]=true,
        ["FullB20BufferPreserved"]=true,["FullB20BufferPreservedAfterRelease"]=true,["FinalServiceState"]="Running",["FinalAudioServiceState"]="Running",
        ["Errors"]=new JsonArray(),["SettingsDifferences"]=new JsonArray(),["ManagedReconnectRequested"]=true,["DisconnectObserved"]=true,["ReturnObserved"]=true,
        ["ManagedRemovalObserved"]=true,["ManagedReconnectRecoveryTested"]=true,["ManagedReconnectGeneration"]=2
    };
    private sealed class Rig : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "timbre pilot's files " + Guid.NewGuid().ToString("N"));
        public string Build => Path.Combine(Root, "artifacts", "verified build");
        public GsxHelperPilot Pilot { get; }
        public ProcessStartInfo? Info;
        public int Starts;
        public FakeProcess Process { get; } = new();
        public Rig()
        {
            foreach(var path in new[] { "tools/Test-GsxInitialization.ps1", "artifacts/verified build/Timbre.Core.dll", "artifacts/verified build/Timbre.exe", "artifacts/verified build/host/Timbre.Core.dll", "artifacts/verified build/host/Timbre.Host.exe",
                "tests/Timbre.Tests/bin/Release/net9.0/Timbre.Core.dll", "tests/Timbre.Tests/bin/Release/net9.0/Timbre.Tests.dll" }) {
                var file = Path.Combine(Root, path); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "matching");
            }
            Pilot = new(Build, info => { Info = info; Starts++; return Process; });
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private sealed class FakeProcess : IGsxPilotProcess
    {
        public bool HasExited { get; set; }
        public int Disposals;
        public void Dispose() => Disposals++;
    }
}
