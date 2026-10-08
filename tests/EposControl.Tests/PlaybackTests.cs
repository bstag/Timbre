using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using System.Buffers.Binary;
using System.Text.Json;
using EposControl.Core;

internal static class PlaybackTests
{
    private static readonly AudioEndpoint Output = new DemoAudioBackend().Discover().First(e => e.Direction == AudioDirection.Playback);
    private static byte[] Fixture(string name = "gsx-playback-stereo") => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".bin"));
    public static void Run(TestSuite suite)
    {
        suite.Case("GSX stereo / 7.1 / stereo live fixtures decode in both directions", () => {
            TestSuite.Assert(ApoPlaybackCodec.Matches(ApoPlaybackCodec.Read(Fixture()), new(false, PlaybackEqualizer.Flat)));
            TestSuite.Assert(ApoPlaybackCodec.Matches(ApoPlaybackCodec.Read(Fixture("gsx-playback-surround71")), new(true, PlaybackEqualizer.Flat)));
            TestSuite.Assert(Fixture().SequenceEqual(Fixture("gsx-playback-stereo-return")));
        });
        suite.Case("Surround patch matches the live mode field and preserves Suite's separate reverb change", () => {
            var original = Fixture(); var bytes = original.ToArray();
            foreach (var patch in ApoPlaybackCodec.Prepare(bytes, new(true))) patch.After.CopyTo(bytes, patch.Offset);
            var live = Fixture("gsx-playback-surround71");
            TestSuite.Assert(bytes[64] == live[64] && original[69] != live[69]);
            for (var i = 0; i < bytes.Length; i++) if (i != 64) TestSuite.Assert(bytes[i] == original[i], $"Unowned field {i} changed");
            TestSuite.Assert(ApoMicrophoneCodec.Read(bytes) == ApoMicrophoneCodec.Read(original));
            foreach (var patch in ApoPlaybackCodec.Prepare(bytes, new(false))) patch.After.CopyTo(bytes, patch.Offset);
            TestSuite.Assert(bytes.SequenceEqual(original));
        });
        foreach (var length in new[] { 0, 65, 4095, 4097 }) suite.Case($"Playback rejects unknown buffer size {length}", () => TestSuite.Reject(() => ApoPlaybackCodec.Read(new byte[length])));
        suite.Case("Playback rejects corrupt header and nonboolean mode", () => {
            var bytes = Fixture(); bytes[4] = 3; TestSuite.Reject(() => ApoPlaybackCodec.Read(bytes));
            bytes = Fixture(); bytes[64] = 2; TestSuite.Reject(() => ApoPlaybackCodec.Read(bytes));
        });
        suite.Case("Playback write whitelist allows reverb but excludes microphone and partial fields", () => {
            ApoPlaybackCodec.ValidatePatch(64, 1);
            ApoPlaybackCodec.ValidatePatch(69, 1); ApoPlaybackCodec.ValidatePatch(148, 4);
            for (var i = 0; i < 9; i++) ApoPlaybackCodec.ValidatePatch(76 + i * 4, 4);
            foreach (var (offset, length) in new[] { (64, 4), (65, 1), (66, 1), (69, 4), (71, 1), (76, 1), (77, 4), (108, 8), (112, 4), (148, 1), (149, 4), (152, 4), (160, 4), (-1, 1) })
                TestSuite.Reject(() => ApoPlaybackCodec.ValidatePatch(offset, length));
            TestSuite.Reject(() => ApoMicrophoneCodec.ValidatePatch(64, 1));
        });
        suite.Case("Playback apply verifies readback and releases its session", () => {
            var fake = new Memory(Fixture()); var result = Backend(fake).Apply(Output, new(false), new(true));
            TestSuite.Assert(result.SurroundEnabled && fake.Writes == 1 && fake.Notifications == 1 && fake.Disposed);
        });
        suite.Case("Unchanged sound mode writes and notifies nothing", () => {
            var fake = new Memory(Fixture()); Backend(fake).Apply(Output, new(false), new(false));
            TestSuite.Assert(fake.Writes == 0 && fake.Notifications == 0 && fake.Disposed);
        });
        suite.Case("Stale sound mode rejects without writes", () => {
            var fake = new Memory(Fixture()); TestSuite.Reject(() => Backend(fake).Apply(Output, new(true), new(false)));
            TestSuite.Assert(fake.Writes == 0 && fake.Disposed);
        });
        foreach (var wrong in new[] { Output with { Direction = AudioDirection.Microphone }, Output with { Usb = null },
            Output with { Usb = Output.Usb! with { ProductId = "009f" } }, Output with { Usb = Output.Usb! with { VendorId = "1234" } },
            Output with { Usb = Output.Usb! with { InstanceId = "" } } }) {
            suite.Case($"Playback refuses unvalidated endpoint {wrong.Direction} {wrong.Usb}", () => {
                var opened = false; var backend = new ApoPlaybackEffectsBackend(_ => { opened = true; throw new Exception(); });
                TestSuite.Reject(() => backend.Read(wrong)); TestSuite.Assert(!opened);
            });
        }
        suite.Case("Playback notification failure restores mode and preserves concurrent microphone/EQ/reverb edits", () => {
            var fake = new Memory(Fixture()) { OnNotify = (f, count) => { if (count == 1) { f.Bytes[69] = 1; f.Bytes[68] = 1; f.Bytes[79] = 64; throw new IOException("Injected failure"); } } };
            TestSuite.Throws<IOException>(() => Backend(fake).Apply(Output, new(false), new(true)));
            TestSuite.Assert(!ApoPlaybackCodec.Read(fake.Bytes).SurroundEnabled && fake.Bytes[69] == 1 && fake.Bytes[68] == 1 && fake.Bytes[79] == 64 && fake.Disposed);
        });
        suite.Case("Playback write failure restores only the attempted mode field", () => {
            var fake = new Memory(Fixture()) { FailWrite = 1 }; TestSuite.Throws<IOException>(() => Backend(fake).Apply(Output, new(false), new(true)));
            TestSuite.Assert(fake.Bytes.SequenceEqual(Fixture()) && fake.Disposed);
        });
        suite.Case("Playback readback mismatch restores starting mode", () => {
            var fake = new Memory(Fixture()) { OnNotify = (f, count) => { if (count == 1) f.Bytes[64] = 0; } };
            TestSuite.Throws<IOException>(() => Backend(fake).Apply(Output, new(false), new(true)));
            TestSuite.Assert(fake.Bytes.SequenceEqual(Fixture()));
        });
        suite.Case("Playback unknown state on failure is preserved and reports restoration failure", () => {
            var fake = new Memory(Fixture()) { OnNotify = (f, count) => { if (count == 1) f.Bytes[64] = 2; } };
            TestSuite.Throws<AggregateException>(() => Backend(fake).Apply(Output, new(false), new(true)));
            TestSuite.Assert(fake.Bytes[64] == 2 && fake.Writes == 1 && fake.Disposed);
        });
        suite.Case("Playback identity rejects absent, changed, duplicate and cross-direction endpoints", () => {
            TestSuite.Reject(() => WindowsApoMemory.ValidatePlaybackIdentity(Output, []));
            TestSuite.Reject(() => WindowsApoMemory.ValidatePlaybackIdentity(Output, [Output with { Name = "Changed" }]));
            TestSuite.Reject(() => WindowsApoMemory.ValidatePlaybackIdentity(Output, [Output, Output with { Id = "second", Usb = Output.Usb! with { InstanceId = "SECOND" } }]));
            TestSuite.Reject(() => WindowsApoMemory.ValidatePlaybackIdentity(Output, [Output with { Direction = AudioDirection.Microphone }]));
            WindowsApoMemory.ValidatePlaybackIdentity(Output, [Output, Output with { Id = "input", Direction = AudioDirection.Microphone }]);
        });
        suite.Case("Demo playback isolates physical units and rejects stale changes", () => {
            var backend = new DemoPlaybackEffectsBackend(); backend.Apply(Output, new(false), new(true));
            TestSuite.Assert(!backend.Read(Output with { Usb = Output.Usb! with { InstanceId = "SECOND" } }).SurroundEnabled);
            TestSuite.Reject(() => backend.Apply(Output, new(false), new(false)));
        });
        suite.Case("Playback profile saves and restores sound mode with volume/mute", () => {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
            try {
                var store = new ProfileStore(path); var profile = Profile(); store.Save(profile);
                TestSuite.Assert(store.Load().Single() == profile);
                var audio = new DemoAudioBackend(); var playback = new DemoPlaybackEffectsBackend();
                ProfileStore.Apply(audio, Output, profile, playback: playback);
                TestSuite.Assert(playback.Read(Output).SurroundEnabled && audio.Read(Output.Id).Muted && audio.Read(Output.Id).Level == .2f);
            } finally { if (File.Exists(path)) File.Delete(path); if (File.Exists(path + ".lock")) File.Delete(path + ".lock"); }
        });
        suite.Case("Playback profile requires adapter before endpoint changes", () => {
            var audio = new DemoAudioBackend(); var before = audio.Read(Output.Id);
            TestSuite.Reject(() => ProfileStore.Apply(audio, Output, Profile()));
            TestSuite.Assert(audio.Read(Output.Id) == before);
        });
        suite.Case("Playback profile failed processing restores accepted volume/mute", () => {
            var audio = new DemoAudioBackend(); var before = audio.Read(Output.Id); var fake = new Memory(Fixture()) { FailWrite = 1 };
            TestSuite.Throws<IOException>(() => ProfileStore.Apply(audio, Output, Profile(), playback: Backend(fake)));
            TestSuite.Assert(audio.Read(Output.Id) == before && fake.Bytes.SequenceEqual(Fixture()));
        });
        suite.Case("Playback profile preserves a newer endpoint edit during failure", () => {
            var audio = new DemoAudioBackend();
            var fake = new Memory(Fixture()) { OnNotify = (f, count) => { if (count == 1) { audio.Apply(Output.Id, .77f, false); throw new IOException(); } } };
            TestSuite.Throws<AggregateException>(() => ProfileStore.Apply(audio, Output, Profile(), playback: Backend(fake)));
            TestSuite.Assert(audio.Read(Output.Id).Level == .77f && !audio.Read(Output.Id).Muted);
        });
        suite.Case("Legacy playback profile leaves sound mode unchanged", () => {
            var audio = new DemoAudioBackend(); var playback = new DemoPlaybackEffectsBackend(); playback.Apply(Output, new(false), new(true));
            ProfileStore.Apply(audio, Output, Profile() with { Playback = null }, playback: playback);
            TestSuite.Assert(playback.Read(Output).SurroundEnabled);
        });
        suite.Case("Microphone profile cannot contain sound effects", () => {
            var mic = new DemoAudioBackend().Discover().First(); var audio = new DemoAudioBackend(); var before = audio.Read(mic.Id);
            TestSuite.Reject(() => ProfileStore.Apply(audio, mic, Profile() with { EndpointId = mic.Id, DeviceIdentity = mic.ProfileIdentity, Direction = AudioDirection.Microphone }));
            TestSuite.Assert(audio.Read(mic.Id) == before);
        });
        suite.Case("GSX golden EQ endpoint bands decode +6 and -6 dB with the other eight flat", () => {
            TestSuite.Assert(ApoPlaybackCodec.Read(Fixture("gsx-playback-eq-band1-plus6")).Equalizer == new PlaybackEqualizer(6, 0, 0, 0, 0, 0, 0, 0, 0));
            TestSuite.Assert(ApoPlaybackCodec.Read(Fixture("gsx-playback-eq-band9-minus6")).Equalizer == new PlaybackEqualizer(0, 0, 0, 0, 0, 0, 0, 0, -6));
        });
        suite.Case("Playback EQ labels and prepared band offsets match the independent native/screenshot mapping", () => {
            using var mapping = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gsx-playback-control-map.json")));
            var root = mapping.RootElement;
            TestSuite.Assert(root.GetProperty("NativeServiceSha256").GetString() == "350D5EC4DEA1F6440C83DA82A0155AF1D7ACA02250E3F31EB7E3EBF472B8DD44");
            TestSuite.Assert(root.GetProperty("BandLabels").EnumerateArray().Select(v => v.GetString()).SequenceEqual(PlaybackEqualizer.Gsx300BandLabels));
            var offsets = root.GetProperty("EqualizerOffsets").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            for (var i = 0; i < 9; i++) {
                var levels = new float[9]; levels[i] = 1;
                TestSuite.Assert(ApoPlaybackCodec.Prepare(Fixture(), new(false, PlaybackEqualizer.FromLevels(levels))).Single().Offset == offsets[i]);
            }
        });
        foreach (var (name, eq) in new[] { ("gsx-playback-eq-band1-plus6", new PlaybackEqualizer(6, 0, 0, 0, 0, 0, 0, 0, 0)),
            ("gsx-playback-eq-band9-minus6", new PlaybackEqualizer(0, 0, 0, 0, 0, 0, 0, 0, -6)) })
            suite.Case($"Playback EQ transition exactly reproduces golden {name}", () => {
                var bytes = Fixture(); foreach (var patch in ApoPlaybackCodec.Prepare(bytes, new(false, eq))) patch.After.CopyTo(bytes, patch.Offset);
                TestSuite.Assert(bytes.SequenceEqual(Fixture(name)));
            });
        suite.Case("Playback EQ all nine bands preserve every unrelated byte and reverse exactly", () => {
            for (var i = 0; i < 9; i++) foreach (var gain in new[] { -6f, -2.75f, 0, 1.25f, 6 }) {
                var original = Fixture(); var bytes = original.ToArray(); var levels = new float[9]; levels[i] = gain;
                var desired = new PlaybackEffects(true, PlaybackEqualizer.FromLevels(levels));
                foreach (var patch in ApoPlaybackCodec.Prepare(bytes, desired)) patch.After.CopyTo(bytes, patch.Offset);
                TestSuite.Assert(ApoPlaybackCodec.Matches(ApoPlaybackCodec.Read(bytes), desired));
                for (var j = 0; j < bytes.Length; j++) if (j != 64 && (j < 76 || j >= 112)) TestSuite.Assert(bytes[j] == original[j]);
                foreach (var patch in ApoPlaybackCodec.Prepare(bytes, new(false, PlaybackEqualizer.Flat))) patch.After.CopyTo(bytes, patch.Offset);
                TestSuite.Assert(bytes.SequenceEqual(Fixture()));
            }
        });
        foreach (var gain in new[] { float.NaN, float.PositiveInfinity, -6.01f, 6.01f }) suite.Case($"Playback rejects invalid EQ gain {gain} before transport", () => {
            var opened = false; var backend = new ApoPlaybackEffectsBackend(_ => { opened = true; throw new Exception(); });
            TestSuite.Reject(() => backend.Apply(Output, new(false, PlaybackEqualizer.Flat), new(true, new(gain, 0, 0, 0, 0, 0, 0, 0, 0))));
            TestSuite.Assert(!opened);
        });
        suite.Case("Corrupt playback EQ cannot reach writes", () => {
            var bytes = Fixture(); BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(108), float.NaN); var fake = new Memory(bytes);
            TestSuite.Reject(() => Backend(fake).Apply(Output, new(false), new(true))); TestSuite.Assert(fake.Writes == 0 && fake.Disposed);
        });
        suite.Case("Playback EQ update requires a complete expected snapshot", () => {
            var fake = new Memory(Fixture()); TestSuite.Reject(() => Backend(fake).Apply(Output, new(false), new(true, PlaybackEqualizer.Flat)));
            TestSuite.Assert(fake.Writes == 0);
        });
        suite.Case("Surround-only legacy update preserves exact fractional playback EQ", () => {
            var bytes = Fixture(); BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(84), 1.234567f);
            var fake = new Memory(bytes); Backend(fake).Apply(Output, new(false), new(true));
            TestSuite.Assert(ApoPlaybackCodec.Read(fake.Bytes).Equalizer!.Band3 == 1.234567f);
        });
        suite.Case("Mixed surround / EQ write failure restores attempted fields", () => {
            var fake = new Memory(Fixture()) { FailWrite = 3 }; var before = ApoPlaybackCodec.Read(fake.Bytes);
            TestSuite.Throws<IOException>(() => Backend(fake).Apply(Output, before, new(true, new(2, -2, 1, -1, 0, 3, -3, 4, -4))));
            TestSuite.Assert(fake.Bytes.SequenceEqual(Fixture()));
        });
        suite.Case("Playback EQ rollback preserves an untouched newer band", () => {
            var fake = new Memory(Fixture()) { OnNotify = (f, count) => { if (count == 1) { BinaryPrimitives.WriteSingleLittleEndian(f.Bytes.AsSpan(108), -3.5f); throw new IOException(); } } };
            TestSuite.Throws<IOException>(() => Backend(fake).Apply(Output, new(false, PlaybackEqualizer.Flat), new(false, new(2, 0, 0, 0, 0, 0, 0, 0, 0))));
            TestSuite.Assert(ApoPlaybackCodec.Read(fake.Bytes).Equalizer is { Band1: 0, Band9: -3.5f });
        });
        suite.Case("Playback EQ rollback preserves a newer edit to an owned band", () => {
            var fake = new Memory(Fixture()) { OnNotify = (f, count) => { if (count == 1) BinaryPrimitives.WriteSingleLittleEndian(f.Bytes.AsSpan(76), 4.5f); } };
            TestSuite.Throws<AggregateException>(() => Backend(fake).Apply(Output, new(false, PlaybackEqualizer.Flat), new(false, new(2, 0, 0, 0, 0, 0, 0, 0, 0))));
            TestSuite.Assert(ApoPlaybackCodec.Read(fake.Bytes).Equalizer!.Band1 == 4.5f);
        });
        suite.Case("Incomplete playback EQ JSON cannot guess bands or surround", () => {
            TestSuite.Throws<JsonException>(() => JsonSerializer.Deserialize<PlaybackEffects>("{}"));
            TestSuite.Throws<JsonException>(() => JsonSerializer.Deserialize<PlaybackEqualizer>("{\"Band1\":1}"));
        });
        suite.Case("Playback EQ profile round-trips and restores its full curve", () => {
            var eq = new PlaybackEqualizer(1, -1, 2, -2, 3, -3, 4, -4, 5);
            var profile = Profile() with { Playback = new(true, eq) };
            var decoded = JsonSerializer.Deserialize<AudioProfile>(JsonSerializer.Serialize(profile))!;
            TestSuite.Assert(profile == decoded);
            var backend = new DemoPlaybackEffectsBackend(); ProfileStore.Apply(new DemoAudioBackend(), Output, decoded, playback: backend);
            TestSuite.Assert(ApoPlaybackCodec.Matches(backend.Read(Output), decoded.Playback!));
        });
        if (OperatingSystem.IsWindows()) RunNative(suite);
    }
    [SupportedOSPlatform("windows")]
    private static void RunNative(TestSuite suite)
    {
        suite.Case("Native playback transport enforces its own whitelist and notification", () => {
            var name = "Local\\EposControl.Tests." + Guid.NewGuid().ToString("N");
            using var mutex = new Mutex(false, name + "_mutex");
            using var map = MemoryMappedFile.CreateNew(name + "_memory", 4096);
            using var view = map.CreateViewAccessor(); view.WriteArray(0, Fixture(), 0, 4096);
            using var changed = new EventWaitHandle(false, EventResetMode.ManualReset, name + "_event");
            using (var session = new WindowsApoMemory.Session(name, ApoPlaybackCodec.ValidatePatch)) {
                TestSuite.Reject(() => session.Write(68, [1])); TestSuite.Reject(() => session.Write(70, [1])); TestSuite.Reject(() => session.Write(148, [1]));
                session.Write(64, [1]); session.Notify(); TestSuite.Assert(ApoPlaybackCodec.Read(session.Read()).SurroundEnabled && changed.WaitOne(0));
            }
            TestSuite.Assert(view.ReadByte(68) == Fixture()[68] && mutex.WaitOne(0)); mutex.ReleaseMutex();
        });
    }
    private static AudioProfile Profile() => new("Surround", Output.Id, Output.ProfileIdentity, AudioDirection.Playback, .2f, true, Playback: new(true));
    private static ApoPlaybackEffectsBackend Backend(Memory memory) => new(_ => memory);
    private sealed class Memory(byte[] bytes) : IEffectMemorySession
    {
        public byte[] Bytes = bytes;
        public int Writes, Notifications, FailWrite;
        public bool Disposed;
        public Action<Memory, int>? OnNotify;
        public byte[] Read() => Bytes.ToArray();
        public void Write(int offset, byte[] data) { if (++Writes == FailWrite) throw new IOException("Injected write failure"); data.CopyTo(Bytes, offset); }
        public void Notify() { ++Notifications; OnNotify?.Invoke(this, Notifications); }
        public void Dispose() => Disposed = true;
    }
}
