using System.Text.Json;
using Timbre.Core;

internal static class ApoStartupStateTests
{
    public static void Run(TestSuite suite)
    {
        var mic = new DemoAudioBackend().Discover().First();
        var effects = new MicrophoneEffects(49.411766f, 1, new(true, true, true, true, MicrophoneEqPresets.Clear));
        var valid = new ApoStartupState(1, mic.ProfileIdentity, effects);
        void WithFile(string text, Action<string> action)
        {
            var path = Path.Combine(Path.GetTempPath(), "Timbre.Startup." + Guid.NewGuid().ToString("N") + ".json");
            try { File.WriteAllText(path, text); action(path); } finally { File.Delete(path); }
        }
        suite.Case("Explicit startup snapshot preserves complete fractional processing state", () => WithFile(JsonSerializer.Serialize(valid), path => {
            var state = ApoStartupState.LoadForB20(path, mic);
            TestSuite.Assert(state == valid && ApoMicrophoneCodec.Matches(state.Effects, effects));
        }));
        suite.Case("Startup snapshot rejects another physical microphone", () => WithFile(JsonSerializer.Serialize(valid), path =>
            TestSuite.Reject(() => ApoStartupState.LoadForB20(path, mic with { Usb = mic.Usb! with { InstanceId = "OTHER" } }))));
        suite.Case("Canonical startup identity tolerates friendly-name changes but rejects a different USB instance", () => {
            var canonical = valid with { DeviceIdentity = ProcessingStateStore.DeviceIdentity(mic) };
            WithFile(JsonSerializer.Serialize(canonical), path => {
                TestSuite.Assert(ApoStartupState.LoadForB20(path, mic with { Id = "new-guid", Name = "Renamed mic" }) == canonical);
                TestSuite.Reject(() => ApoStartupState.LoadForB20(path, mic with { Usb = mic.Usb! with { InstanceId = "OTHER" } }));
            });
        });
        suite.Case("Startup snapshot rejects another schema version", () => WithFile(JsonSerializer.Serialize(valid with { SchemaVersion = 2 }), path =>
            TestSuite.Reject(() => ApoStartupState.LoadForB20(path, mic))));
        suite.Case("Startup snapshot cannot guess missing required fields", () => {
            foreach (var text in new[] { "{}", "{\"SchemaVersion\":1,\"ProfileIdentity\":\"x\"}", "{\"SchemaVersion\":1,\"Effects\":{}}" })
                WithFile(text, path => TestSuite.Throws<JsonException>(() => ApoStartupState.LoadForB20(path, mic)));
        });
        suite.Case("Legacy gate/filter-only startup state is refused", () => WithFile(JsonSerializer.Serialize(valid with { Effects = new(20, 1) }), path =>
            TestSuite.Reject(() => ApoStartupState.LoadForB20(path, mic))));
        suite.Case("Invalid startup control values reject before native creation", () => WithFile(JsonSerializer.Serialize(valid with { Effects = effects with { GatePercent = 101 } }), path =>
            TestSuite.Reject(() => ApoStartupState.LoadForB20(path, mic))));
        suite.Case("Unrecognized startup fields are refused", () => WithFile(JsonSerializer.Serialize(valid).Replace("\"SchemaVersion\":1", "\"SchemaVersion\":1,\"RawMemory\":[]"), path =>
            TestSuite.Throws<JsonException>(() => ApoStartupState.LoadForB20(path, mic))));
        suite.Case("Null and oversized startup files are refused", () => {
            WithFile("null", path => TestSuite.Reject(() => ApoStartupState.LoadForB20(path, mic)));
            WithFile(new string(' ', 16385), path => TestSuite.Reject(() => ApoStartupState.LoadForB20(path, mic)));
        });
        suite.Case("Playback, GSX and foreign-vendor startup reject before file access", () => {
            foreach (var endpoint in new[] { mic with { Direction = AudioDirection.Playback }, mic with { Usb = mic.Usb! with { ProductId = "0098" } }, mic with { Usb = mic.Usb! with { VendorId = "1234" } } })
                TestSuite.Reject(() => ApoStartupState.LoadForB20("file-that-does-not-exist", endpoint));
        });
        suite.Case("Device-matched diagnostic startup snapshot round-trips its captured layout", () => {
            var seed = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "apo-memory-009f.bin"));
            var state = new ApoStartupState(1, mic.ProfileIdentity, ApoMicrophoneCodec.Read(seed), seed);
            WithFile(JsonSerializer.Serialize(state), path => TestSuite.Assert(ApoStartupState.LoadForB20(path, mic).DiagnosticSeed!.SequenceEqual(seed)));
        });
        suite.Case("Diagnostic startup refuses mismatched typed settings", () => {
            var seed = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "apo-memory-009f.bin"));
            WithFile(JsonSerializer.Serialize(valid with { DiagnosticSeed = seed }), path => TestSuite.Reject(() => ApoStartupState.LoadForB20(path, mic)));
        });
    }
}
