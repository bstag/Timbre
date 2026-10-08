using System.Text.Json;
using Timbre.Core;

internal static class ProcessingStateTests
{
    private static readonly AudioEndpoint Mic = new DemoAudioBackend().Discover().First();
    private static readonly MicrophoneEffects Desired = new(49.411766f, 1, new(true, false, true, true, MicrophoneEqPresets.Clear));
    private static void WithStore(Action<ProcessingStateStore> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Timbre.State." + Guid.NewGuid().ToString("N"));
        try { action(new(directory)); } finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    public static void Run(TestSuite suite)
    {
        suite.Case("Missing processing state does not create files or enable restore", () => WithStore(store => {
            TestSuite.Assert(store.Load(Mic) is null && !Directory.Exists(store.DirectoryPath));
        }));
        suite.Case("Complete processing survives a new store instance exactly", () => WithStore(store => {
            var saved = store.Remember(Mic, Desired);
            TestSuite.Assert(new ProcessingStateStore(store.DirectoryPath).Load(Mic) == saved && !saved.RestoreOnConnect && saved.Effects == Desired);
            TestSuite.Assert(!File.ReadAllText(store.StatePath(Mic)).Contains("DiagnosticSeed"));
        }));
        suite.Case("Processing identity survives endpoint GUID and friendly-name changes", () => WithStore(store => {
            store.Remember(Mic, Desired);
            TestSuite.Assert(store.Load(Mic with { Id = "new-guid", Name = "Renamed mic", AdapterName = "Renamed adapter" })!.Effects == Desired);
            TestSuite.Assert(store.Load(Mic with { Usb = Mic.Usb! with { InstanceId = Mic.Usb.InstanceId.ToLowerInvariant() } })!.Effects == Desired);
        }));
        suite.Case("Another physical B20 has separate processing and restore policy", () => WithStore(store => {
            var other = Mic with { Usb = Mic.Usb! with { InstanceId = "OTHER" } };
            store.Remember(Mic, Desired); store.SetRestoreOnConnect(Mic, true);
            TestSuite.Assert(store.Load(other) is null);
            store.Remember(other, Desired with { GatePercent = 12 });
            TestSuite.Assert(store.Load(Mic)!.RestoreOnConnect && !store.Load(other)!.RestoreOnConnect && store.Load(Mic)!.Effects == Desired);
        }));
        suite.Case("Unsupported endpoints reject before processing file access", () => WithStore(store => {
            foreach (var endpoint in new[] { Mic with { Direction = AudioDirection.Playback }, Mic with { Usb = null },
                Mic with { Usb = Mic.Usb! with { VendorId = "1234" } }, Mic with { Usb = Mic.Usb! with { ProductId = "0098" } },
                Mic with { Usb = Mic.Usb! with { InstanceId = " " } } }) TestSuite.Reject(() => store.Load(endpoint));
            TestSuite.Assert(!Directory.Exists(store.DirectoryPath));
        }));
        suite.Case("Invalid or incomplete settings cannot replace the last good processing", () => WithStore(store => {
            var before = store.Remember(Mic, Desired);
            foreach (var bad in new[] { new MicrophoneEffects(20, 1), Desired with { GatePercent = 101 }, Desired with { FilterLevel = 3 },
                Desired with { Processing = Desired.Processing! with { Equalizer = MicrophoneEqualizer.Flat with { Band9 = -7 } } } })
                TestSuite.Reject(() => store.Remember(Mic, bad));
            TestSuite.Assert(store.Load(Mic) == before);
        }));
        suite.Case("Restore preference requires saved settings and survives subsequent applies", () => WithStore(store => {
            TestSuite.Reject(() => store.SetRestoreOnConnect(Mic, true));
            store.Remember(Mic, Desired); store.SetRestoreOnConnect(Mic, true);
            TestSuite.Assert(store.Remember(Mic, Desired with { GatePercent = 20 }).RestoreOnConnect);
            store.SetRestoreOnConnect(Mic, false); TestSuite.Assert(!store.Load(Mic)!.RestoreOnConnect);
        }));
        suite.Case("Processing files reject missing fields, unknown fields and broken JSON", () => WithStore(store => {
            store.Remember(Mic, Desired); var path = store.StatePath(Mic); var valid = File.ReadAllText(path);
            foreach (var text in new[] { "{}", "{", valid.Replace("\"RestoreOnConnect\": false", "\"Unexpected\": false"),
                valid.Replace("\"GateEnabled\": false,", "") }) {
                File.WriteAllText(path, text); TestSuite.Throws<JsonException>(() => store.Load(Mic));
            }
        }));
        suite.Case("Processing files reject future versions, wrong identity, invalid timestamp and null", () => WithStore(store => {
            var valid = store.Remember(Mic, Desired); var path = store.StatePath(Mic);
            foreach (var state in new[] { valid with { SchemaVersion = 2 }, valid with { DeviceIdentity = "OTHER" },
                valid with { SavedAtUtc = default }, valid with { SavedAtUtc = valid.SavedAtUtc.ToOffset(TimeSpan.FromHours(1)) }, valid with { Effects = new(20, 1) } }) {
                File.WriteAllText(path, JsonSerializer.Serialize(state)); TestSuite.Reject(() => store.Load(Mic));
            }
            File.WriteAllText(path, "null"); TestSuite.Reject(() => store.Load(Mic));
            File.WriteAllText(path, new string(' ', 16385)); TestSuite.Reject(() => store.Load(Mic));
        }));
        suite.Case("Corrupt previous state is preserved instead of silently replaced", () => WithStore(store => {
            store.Remember(Mic, Desired); File.WriteAllText(store.StatePath(Mic), "{");
            TestSuite.Throws<JsonException>(() => store.Remember(Mic, Desired));
            TestSuite.Assert(File.ReadAllText(store.StatePath(Mic)) == "{");
        }));
        suite.Case("A rejected hardware apply leaves saved processing untouched", () => WithStore(store => {
            var before = store.Remember(Mic, Desired); var backend = new DemoMicrophoneEffectsBackend();
            TestSuite.Reject(() => store.ApplyAndSave(Mic, backend, Desired, Desired with { GatePercent = 12 }));
            TestSuite.Assert(store.Load(Mic) == before);
        }));
        suite.Case("A successful hardware apply saves verified fractional values", () => WithStore(store => {
            var backend = new DemoMicrophoneEffectsBackend();
            var result = store.ApplyAndSave(Mic, backend, backend.Read(Mic), Desired);
            TestSuite.Assert(result.PersistenceError is null && result.Effects == Desired && store.Load(Mic)!.Effects == Desired);
        }));
        suite.Case("Save failure preserves hardware success and last good file, with no temporary residue", () => WithStore(store => {
            if (!OperatingSystem.IsWindows()) return; // Windows delete-sharing is the production failure seam.
            var before = store.Remember(Mic, Desired); var backend = new DemoMicrophoneEffectsBackend();
            using (var denyReplace = new FileStream(store.StatePath(Mic), FileMode.Open, FileAccess.Read, FileShare.Read)) {
                var result = store.ApplyAndSave(Mic, backend, backend.Read(Mic), Desired with { GatePercent = 12 });
                TestSuite.Assert(result.PersistenceError is not null && backend.Read(Mic).GatePercent == 12 && store.Load(Mic) == before);
            }
            TestSuite.Assert(!Directory.EnumerateFiles(store.DirectoryPath, "*.tmp").Any());
        }));
        suite.Case("An apply returning mismatched readback is not persisted", () => WithStore(store => {
            var before = store.Remember(Mic, Desired);
            var result = store.ApplyAndSave(Mic, new BadReadback(), Desired, Desired);
            TestSuite.Assert(result.PersistenceError is not null && store.Load(Mic) == before);
        }));
        suite.Case("Restore validates the current physical device before any write", () => WithStore(store => {
            store.Remember(Mic, Desired); var backend = new DemoMicrophoneEffectsBackend(); var before = backend.Read(Mic);
            TestSuite.Reject(() => store.Restore(Mic with { Id = "disconnected-guid" }, new DemoAudioBackend(), backend));
            TestSuite.Assert(backend.Read(Mic) == before);
        }));
        suite.Case("Manual restore uses complete typed settings with automatic policy off", () => WithStore(store => {
            store.Remember(Mic, Desired); var backend = new DemoMicrophoneEffectsBackend();
            TestSuite.Assert(store.Restore(Mic, new DemoAudioBackend(), backend) == Desired && !store.Load(Mic)!.RestoreOnConnect);
        }));
        suite.Case("Automatic restore is off by default and polling preserves external changes", () => WithStore(store => {
            store.Remember(Mic, Desired); var audio = new DemoAudioBackend(); var backend = new DemoMicrophoneEffectsBackend(); var session = new ProcessingRestoreSession(store);
            session.Observe(audio.Discover()); var original = backend.Read(Mic);
            TestSuite.Assert(session.RestoreIfEnabled(Mic, audio, backend) is null && backend.Read(Mic) == original);
            store.SetRestoreOnConnect(Mic, true);
            TestSuite.Assert(session.RestoreIfEnabled(Mic, audio, backend) is null && backend.Read(Mic) == original);
        }));
        suite.Case("Opt-in startup and reconnect restore once without repeated overwrites", () => WithStore(store => {
            store.Remember(Mic, Desired); store.SetRestoreOnConnect(Mic, true); var audio = new DemoAudioBackend(); var backend = new DemoMicrophoneEffectsBackend(); var session = new ProcessingRestoreSession(store);
            session.Observe(audio.Discover()); TestSuite.Assert(session.RestoreIfEnabled(Mic, audio, backend) == Desired);
            var external = backend.Apply(Mic, Desired, Desired with { GatePercent = 12 }); session.Observe(audio.Discover());
            TestSuite.Assert(session.RestoreIfEnabled(Mic, audio, backend) is null && backend.Read(Mic) == external);
            session.Observe([]); session.Observe(audio.Discover()); TestSuite.Assert(session.RestoreIfEnabled(Mic, audio, backend) == Desired);
            TestSuite.Assert(new ProcessingRestoreSession(new(store.DirectoryPath)).RestoreIfEnabled(Mic, audio, backend) == Desired);
        }));
        suite.Case("A failed automatic restore is not retried every polling tick", () => WithStore(store => {
            store.Remember(Mic, Desired); store.SetRestoreOnConnect(Mic, true); var session = new ProcessingRestoreSession(store); var backend = new DemoMicrophoneEffectsBackend();
            var unavailable = Mic with { Id = "gone" };
            TestSuite.Reject(() => session.RestoreIfEnabled(unavailable, new DemoAudioBackend(), backend));
            TestSuite.Assert(session.RestoreIfEnabled(unavailable, new DemoAudioBackend(), backend) is null);
        }));
        suite.Case("A changed endpoint GUID restores only when its matching USB instance is connected", () => WithStore(store => {
            store.Remember(Mic, Desired); store.SetRestoreOnConnect(Mic, true); var backend = new DemoMicrophoneEffectsBackend(); var session = new ProcessingRestoreSession(store);
            session.Observe([Mic]); session.RestoreIfEnabled(Mic, new TestAudio([Mic]), backend);
            backend.Apply(Mic, Desired, Desired with { GatePercent = 12 });
            var reconnected = Mic with { Id = "new-guid", Name = "Renamed microphone" }; session.Observe([reconnected]);
            TestSuite.Assert(session.RestoreIfEnabled(reconnected, new TestAudio([reconnected]), backend) == Desired);
        }));
        suite.Case("Saved processing cannot restore through ambiguous same-model shared objects", () => WithStore(store => {
            store.Remember(Mic, Desired); var backend = new DemoMicrophoneEffectsBackend(); var before = backend.Read(Mic);
            var second = Mic with { Id = "second-guid", Usb = Mic.Usb! with { InstanceId = "SECOND" } };
            TestSuite.Reject(() => store.Restore(Mic, new TestAudio([Mic, second]), backend));
            TestSuite.Assert(backend.Read(Mic) == before);
        }));
        suite.Case("Separate store writers serialize atomic settings and preference updates", () => WithStore(store => {
            store.Remember(Mic, Desired);
            Parallel.Invoke(() => new ProcessingStateStore(store.DirectoryPath).SetRestoreOnConnect(Mic, true),
                () => new ProcessingStateStore(store.DirectoryPath).Remember(Mic, Desired with { GatePercent = 12 }));
            TestSuite.Assert(store.Load(Mic) is { RestoreOnConnect: true, Effects.GatePercent: 12 });
        }));
        suite.Case("Saved processing restores through the real native transport after fresh object recreation", () => WithStore(store => {
            if (!OperatingSystem.IsWindows()) return;
            var name = "Local\\Timbre.Tests." + Guid.NewGuid().ToString("N");
            var backend = new ApoMicrophoneEffectsBackend(_ => OperatingSystem.IsWindows()
                ? new WindowsApoMemory.Session(name) : throw new PlatformNotSupportedException());
            using (var first = WindowsApoObjectHost.OpenPrivate(name, true)) {
                var applied = store.ApplyAndSave(Mic, backend, first.Read(), Desired);
                TestSuite.Assert(applied.PersistenceError is null && ApoMicrophoneCodec.Matches(first.Read(), Desired));
            }
            using var second = WindowsApoObjectHost.OpenPrivate(name, true);
            var saved = new ProcessingStateStore(store.DirectoryPath);
            var before = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "apo-cold-initializer-009f.bin"));
            var expected = before.ToArray();
            foreach (var patch in ApoMicrophoneCodec.Prepare(before, saved.Load(Mic)!.Effects)) patch.After.CopyTo(expected, patch.Offset);
            saved.Restore(Mic, new TestAudio([Mic]), backend);
            using var session = new WindowsApoMemory.Session(name);
            TestSuite.Assert(session.Read().SequenceEqual(expected));
            using var changed = EventWaitHandle.OpenExisting(name + "_event"); TestSuite.Assert(changed.WaitOne(0));
        }));
        suite.Case("Busy processing state lock fails within a bounded interval and preserves data", () => WithStore(store => {
            var before = store.Remember(Mic, Desired);
            using (var held = new FileStream(store.StatePath(Mic) + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                TestSuite.Throws<IOException>(() => store.Remember(Mic, Desired with { GatePercent = 12 }));
            TestSuite.Assert(store.Load(Mic) == before);
        }));
    }
    private sealed class BadReadback : IMicrophoneEffectsBackend
    {
        public MicrophoneEffects Read(AudioEndpoint endpoint) => Desired with { GatePercent = 12 };
        public MicrophoneEffects Apply(AudioEndpoint endpoint, MicrophoneEffects expected, MicrophoneEffects desired) => Read(endpoint);
    }
    private sealed class TestAudio(IReadOnlyList<AudioEndpoint> endpoints) : IAudioBackend
    {
        public IReadOnlyList<AudioEndpoint> Discover() => endpoints;
        public AudioState Read(string id) => throw new NotSupportedException();
        public AudioState Apply(string id, float level, bool muted) => throw new NotSupportedException();
    }
}
