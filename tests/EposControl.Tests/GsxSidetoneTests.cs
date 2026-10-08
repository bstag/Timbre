using System.Text.Json;
using EposControl.Core;

internal static class GsxSidetoneTests
{
    private static readonly AudioEndpoint Mic = new DemoAudioBackend().Discover().Single(e => e.Id == "demo-gsx-mic");
    private sealed class CountingAudio : IAudioBackend
    {
        private readonly DemoAudioBackend inner = new();
        public int Writes;
        public IReadOnlyList<AudioEndpoint> Discover() => inner.Discover();
        public AudioState Read(string id) => inner.Read(id);
        public AudioState Apply(string id, float level, bool muted) { Writes++; return inner.Apply(id, level, muted); }
    }
    private sealed class Session : IGsxSidetoneSession
    {
        public GsxSidetoneState State = new("test-gsx", new(226));
        public int Reads, Writes; public bool Disposed;
        public Action<Session>? OnRead, OnWrite;
        public GsxSidetoneState Read(CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); Reads++; OnRead?.Invoke(this); return State; }
        public void Write(GsxSidetoneSettings desired, CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); Writes++; State = State with { Settings = desired }; OnWrite?.Invoke(this); }
        public void Dispose() => Disposed = true;
    }
    public static void Run(TestSuite suite)
    {
        void Case(string name, Action action) => suite.Case(name, action);
        Case("GSX production percentage conversion matches every recovered table entry", () => {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gsx-sidetone-ui-map.json")));
            var forward = json.RootElement.GetProperty("SliderToHardwareByte").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            var inverse = json.RootElement.GetProperty("HardwareByteToSlider").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            for (var i = 0; i < 256; i++) TestSuite.Assert(GsxSidetoneSettings.FromPercent(i * 100.0 / 255).RawValue == forward[i]);
            foreach (var raw in forward.Distinct()) TestSuite.Assert(new GsxSidetoneSettings(raw).Percent == inverse[raw] * 100.0 / 255);
            TestSuite.Assert(GsxSidetoneSettings.FromPercent(100).RawValue == 0 && GsxSidetoneSettings.FromPercent(0).RawValue == 199);
        });
        Case("GSX production packets contain only the recovered control command", () => {
            var query = new byte[39]; new byte[] { 4, 0, 1, 0x12, 0x7B }.CopyTo(query, 0);
            TestSuite.Assert(GsxSidetoneProtocol.Query.SequenceEqual(query));
            var expected = query.ToArray(); expected[1] = 0x40; expected[5] = 226;
            TestSuite.Assert(GsxSidetoneProtocol.Set(new(226)).SequenceEqual(expected));
            var copy = GsxSidetoneProtocol.Query; copy[0] = 9; TestSuite.Assert(GsxSidetoneProtocol.Query[0] == 4);
            foreach (var stage in new[] { "baseline", "min", "mid", "max", "restored" }) {
                var report = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"gsx-sidetone-{stage}.bin"));
                TestSuite.Assert(GsxSidetoneProtocol.Decode(report).RawValue == report[1]);
            }
        });
        Case("GSX rejects malformed status frames and unsupported values", () => {
            foreach (var raw in new[] { -1, 1, 198, 256 }) TestSuite.Throws<InvalidDataException>(() => new GsxSidetoneSettings(raw).Validate());
            foreach (var percent in new[] { double.NaN, double.PositiveInfinity, -.01, 100.01 }) TestSuite.Throws<ArgumentOutOfRangeException>(() => GsxSidetoneSettings.FromPercent(percent));
            var report = new byte[35]; report[0] = 5; report[1] = 226;
            foreach (var invalid in new[] { Array.Empty<byte>(), report[..34], new byte[35] }) TestSuite.Throws<InvalidDataException>(() => GsxSidetoneProtocol.Decode(invalid));
            report[2] = 1; TestSuite.Throws<InvalidDataException>(() => GsxSidetoneProtocol.Decode(report));
        });
        Case("GSX stable reads use independent samples and release the transaction", () => {
            var session = new Session(); TestSuite.Assert(new GsxSidetoneBackend(_ => session).Read(Mic) == session.State && session.Reads == 2 && session.Disposed && session.Writes == 0);
        });
        Case("GSX unstable samples fail without changing hardware", () => {
            var session = new Session { OnRead = s => { if (s.Reads == 2) s.State = s.State with { Settings = new(250) }; } };
            TestSuite.Throws<IOException>(() => new GsxSidetoneBackend(_ => session).Read(Mic)); TestSuite.Assert(session.Writes == 0 && session.Disposed);
        });
        Case("GSX validated update verifies its exact raw readback", () => {
            var session = new Session(); var result = new GsxSidetoneBackend(_ => session).Apply(Mic, session.State, new(239));
            TestSuite.Assert(result.Settings.RawValue == 239 && session.Writes == 1 && session.Disposed);
        });
        Case("GSX no-op sends no setting command", () => {
            var session = new Session(); new GsxSidetoneBackend(_ => session).Apply(Mic, session.State, session.State.Settings); TestSuite.Assert(session.Writes == 0);
        });
        Case("GSX stale draft rejects before setting writes", () => {
            var session = new Session(); TestSuite.Throws<InvalidOperationException>(() => new GsxSidetoneBackend(_ => session).Apply(Mic, session.State with { Settings = new(222) }, new(239))); TestSuite.Assert(session.Writes == 0);
        });
        Case("GSX external edit before write preserves the newer value", () => {
            var session = new Session { OnRead = s => { if (s.Reads == 3) s.State = s.State with { Settings = new(250) }; } };
            TestSuite.Throws<IOException>(() => new GsxSidetoneBackend(_ => session).Apply(Mic, session.State, new(239))); TestSuite.Assert(session.Writes == 0 && session.State.Settings.RawValue == 250);
        });
        Case("GSX failed readback restores only its owned value", () => {
            var session = new Session { OnRead = s => { if (s.Reads == 5) throw new TimeoutException("Injected timeout"); } }; var original = session.State;
            TestSuite.Throws<TimeoutException>(() => new GsxSidetoneBackend(_ => session).Apply(Mic, original, new(239))); TestSuite.Assert(session.State == original && session.Writes == 2 && session.Disposed);
        });
        Case("GSX newer value during failure is preserved instead of rolled back", () => {
            var session = new Session { OnRead = s => { if (s.Reads == 5) s.State = s.State with { Settings = new(250) }; } };
            TestSuite.Throws<AggregateException>(() => new GsxSidetoneBackend(_ => session).Apply(Mic, session.State, new(239))); TestSuite.Assert(session.Writes == 1 && session.State.Settings.RawValue == 250);
        });
        Case("GSX changed control identity cannot receive a rollback", () => {
            var session = new Session { OnWrite = s => s.State = s.State with { ControlIdentity = "replacement-device" } };
            TestSuite.Throws<AggregateException>(() => new GsxSidetoneBackend(_ => session).Apply(Mic, session.State, new(239))); TestSuite.Assert(session.Writes == 1 && session.Disposed);
        });
        Case("GSX cancellation after submission restores and verifies the starting byte", () => {
            using var cancellation = new CancellationTokenSource(); var session = new Session { OnWrite = s => { if (s.Writes == 1) cancellation.Cancel(); } }; var original = session.State;
            TestSuite.Throws<OperationCanceledException>(() => new GsxSidetoneBackend(_ => session).Apply(Mic, original, new(239), cancellation.Token)); TestSuite.Assert(session.State == original && session.Writes == 2);
        });
        Case("GSX invalid endpoints and cancellation open no transport", () => {
            var opened = false; var backend = new GsxSidetoneBackend(_ => { opened = true; throw new Exception(); });
            foreach (var bad in new[] { Mic with { Direction = AudioDirection.Playback }, Mic with { Usb = null }, Mic with { Usb = Mic.Usb! with { ProductId = "009F" } } })
                TestSuite.Throws<InvalidOperationException>(() => backend.Read(bad));
            using var canceled = new CancellationTokenSource(); canceled.Cancel(); TestSuite.Throws<OperationCanceledException>(() => backend.Read(Mic, canceled.Token));
            TestSuite.Assert(!opened);
        });
        Case("GSX HID routing binds physical identity and known descriptor", () => {
            var good = new GsxSidetoneHidDevice("\\\\?\\hid#vid_1395&pid_0098&mi_03&col01#demo", Mic.Usb, 12, 1, 35, 39, 0);
            TestSuite.Assert(GsxSidetoneDeviceGuard.Select(Mic, [Mic], [good]) == good);
            foreach (var bad in new[] { good with { Usb = Mic.Usb! with { InstanceId = "other" } }, good with { OutputBytes = 2 }, good with { UsagePage = 0xFFFF }, good with { Path = null! }, good with { Usb = Mic.Usb! with { InstanceId = null! } }, good with { Path = "\\\\?\\hid#vid_1395&pid_009f&mi_03&col01#demo" } })
                TestSuite.Reject(() => GsxSidetoneDeviceGuard.Select(Mic, [Mic], [bad]));
            TestSuite.Reject(() => GsxSidetoneDeviceGuard.Select(Mic, [Mic], [good, good]));
            TestSuite.Reject(() => GsxSidetoneDeviceGuard.Select(Mic, [], [good]));
        });
        Case("GSX profile field round-trips exact raw state and leaves legacy profiles alone", () => {
            var audio = new DemoAudioBackend(); var monitor = new DemoGsxSidetoneBackend(); var state = audio.Read(Mic.Id);
            var profile = new AudioProfile("Gaming", Mic.Id, ProfileStore.DeviceIdentity(Mic), Mic.Direction, state.Level, state.Muted, GsxSidetone: new(226));
            var json = JsonSerializer.Serialize(profile); var copy = JsonSerializer.Deserialize<AudioProfile>(json)!; TestSuite.Assert(copy == profile && !json.Contains("Percent"));
            monitor.Apply(Mic, monitor.Read(Mic), new(250)); ProfileStore.Apply(audio, Mic, copy, gsxSidetone: monitor); TestSuite.Assert(monitor.Read(Mic).Settings.RawValue == 226);
            monitor.Apply(Mic, monitor.Read(Mic), new(222)); ProfileStore.Apply(audio, Mic, profile with { GsxSidetone = null }); TestSuite.Assert(monitor.Read(Mic).Settings.RawValue == 222);
            TestSuite.Throws<JsonException>(() => JsonSerializer.Deserialize<GsxSidetoneSettings>("{}"));
        });
        Case("GSX setup capture stores exact raw state and excludes unselected devices", () => {
            var audio = new DemoAudioBackend(); var monitor = new DemoGsxSidetoneBackend(); var controller = new SetupProfileController(audio, new DemoMicrophoneEffectsBackend(), new DemoSidetoneBackend(), new DemoPlaybackEffectsBackend(), monitor);
            var setup = controller.Capture("Meeting", [Mic]); TestSuite.Assert(setup.Devices.Single().Settings.GsxSidetone?.RawValue == 226);
            monitor.Apply(Mic, monitor.Read(Mic), new(250)); TestSuite.Assert(controller.Apply(setup).Single().Status == SetupApplyStatus.Applied && monitor.Read(Mic).Settings.RawValue == 226);
            monitor.Apply(Mic, monitor.Read(Mic), new(239)); var b20 = audio.Discover().First(); controller.Apply(controller.Capture("B20 only", [b20])); TestSuite.Assert(monitor.Read(Mic).Settings.RawValue == 239);
        });
        Case("GSX profile USB preflight failure leaves audio and processing untouched", () => {
            var audio = new CountingAudio(); var effects = new DemoMicrophoneEffectsBackend(); var original = effects.Read(Mic);
            var profile = new AudioProfile("Failure", Mic.Id, ProfileStore.DeviceIdentity(Mic), Mic.Direction, .2f, true, original with { GatePercent = 50 }, GsxSidetone: new(239));
            var session = new Session { OnRead = _ => throw new TimeoutException("Injected USB preflight failure") };
            TestSuite.Throws<TimeoutException>(() => ProfileStore.Apply(audio, Mic, profile, effects, gsxSidetone: new GsxSidetoneBackend(_ => session)));
            TestSuite.Assert(audio.Writes == 0 && session.Writes == 0 && effects.Read(Mic) == original && session.Disposed);
        });
        Case("GSX profile USB readback failure restores audio processing and raw monitor state", () => {
            var audio = new CountingAudio(); var originalAudio = audio.Read(Mic.Id); var effects = new DemoMicrophoneEffectsBackend(); var originalEffects = effects.Read(Mic);
            var session = new Session { OnRead = s => { if (s.Reads == 7) throw new TimeoutException("Injected post-write failure"); } }; var originalMonitor = session.State;
            var profile = new AudioProfile("Failure", Mic.Id, ProfileStore.DeviceIdentity(Mic), Mic.Direction, .2f, true, originalEffects with { GatePercent = 50 }, GsxSidetone: new(239));
            TestSuite.Throws<TimeoutException>(() => ProfileStore.Apply(audio, Mic, profile, effects, gsxSidetone: new GsxSidetoneBackend(_ => session)));
            TestSuite.Assert(audio.Read(Mic.Id) == originalAudio && audio.Writes == 2 && effects.Read(Mic) == originalEffects && session.State == originalMonitor && session.Writes == 2);
        });
    }
}
