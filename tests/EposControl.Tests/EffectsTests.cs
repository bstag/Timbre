using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using EposControl.Core;

internal static class EffectsTests
{
    private static readonly AudioEndpoint Mic = new DemoAudioBackend().Discover().First();
    private static byte[] Fixture(string name = "apo-memory-009f") => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".bin"));
    public static void Run(TestSuite suite)
    {
        void Case(string name, Action action) => suite.Case(name, action);
        Case("Golden captures match their checked-in SHA256 manifest", () => {
            var entries = JsonSerializer.Deserialize<List<Manifest>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "manifest.json")))!;
            foreach (var entry in entries) TestSuite.Assert(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", entry.File)))) == entry.Hash, entry.File);
        });
        foreach (var (before, after, gate) in new[] { ("gsx-microphone-gate-zero", "gsx-microphone-gate-max", 100f), ("gsx-microphone-gate-max", "gsx-microphone-gate-return", 0f) })
            Case($"GSX microphone golden {before} -> {after} changes only gate", () => {
                var bytes = Fixture(before);
                var state = ApoMicrophoneCodec.Read(bytes);
                var patches = ApoMicrophoneCodec.Prepare(bytes, state with { GatePercent = gate });
                TestSuite.Assert(patches.Count == 1 && patches[0].Offset == ApoMicrophoneCodec.GateOffset && patches[0].After.Length == 4);
                foreach (var patch in patches) patch.After.CopyTo(bytes, patch.Offset);
                TestSuite.Assert(bytes.SequenceEqual(Fixture(after)), "GSX wire bytes differ from the independent Suite capture");
            });
        Case("GSX microphone gate capture preserves the entire connected B20 buffer", () => {
            var original = Fixture("b20-during-gsx-gate-start");
            TestSuite.Assert(original.SequenceEqual(Fixture("b20-during-gsx-gate-max")) && original.SequenceEqual(Fixture("b20-during-gsx-gate-restored")));
        });
        Case("GSX microphone page initialization is separate from its gate transition", () => {
            var before = Fixture("gsx-microphone-routing-start");
            var after = Fixture("gsx-microphone-gate-zero");
            var changed = Enumerable.Range(0, before.Length).Where(i => before[i] != after[i]).ToArray();
            TestSuite.Assert(changed.SequenceEqual(new[] { 66, 68, 70 }), "Unexpected Suite page initialization fields");
            TestSuite.Assert(changed.All(i => before[i] == 0 && after[i] == 1));
            TestSuite.Assert(ApoMicrophoneCodec.Read(before).GatePercent == 0 && ApoMicrophoneCodec.Read(after).GatePercent == 0);
        });
        Case("GSX microphone routing experiment restores its complete starting buffer", () =>
            TestSuite.Assert(Fixture("gsx-microphone-routing-start").SequenceEqual(Fixture("gsx-microphone-routing-restored"))));
        foreach (var (name, gate, filter) in new[] { ("apo-memory-009f", 0f, 2), ("apo-gate50-009f", 49f, 2), ("apo-gate100-009f", 100f, 2), ("apo-filter1-009f", 100f, 1), ("apo-memory-0098", 0f, 0) })
            Case($"Captured {name} decodes to gate {gate}, filter {filter}", () => TestSuite.Assert(ApoMicrophoneCodec.Matches(ApoMicrophoneCodec.Read(Fixture(name)), new(gate, filter))));
        foreach (var (before, after, desired) in new[] { ("apo-memory-009f", "apo-gate50-009f", new MicrophoneEffects(49, 2)), ("apo-gate50-009f", "apo-gate100-009f", new MicrophoneEffects(100, 2)), ("apo-gate100-009f", "apo-filter1-009f", new MicrophoneEffects(100, 1)) })
            Case($"Golden transition {before} -> {after} changes only verified fields", () => {
                var data = Fixture(before); var original = data.ToArray(); var patches = ApoMicrophoneCodec.Prepare(data, desired);
                TestSuite.Assert(data.SequenceEqual(original), "Preparation mutated its input");
                foreach (var patch in patches) patch.After.CopyTo(data, patch.Offset);
                TestSuite.Assert(data.SequenceEqual(Fixture(after)), "Prepared wire bytes differ from live capture");
            });
        foreach (var size in new[] { 0, 164, 4095, 4097 }) Case($"Reject APO buffer size {size}", () => TestSuite.Reject(() => ApoMicrophoneCodec.Read(new byte[size])));
        Case("Reject unknown APO header", () => { var bytes = Fixture(); bytes[0] = 3; TestSuite.Reject(() => ApoMicrophoneCodec.Read(bytes)); });
        foreach (var gate in new[] { float.NaN, float.PositiveInfinity, -1, 101 }) Case($"Reject gate {gate}", () => TestSuite.Reject(() => new MicrophoneEffects(gate, 1).Validate()));
        foreach (var level in new[] { -1, 3, int.MaxValue }) Case($"Reject noise filter level {level}", () => TestSuite.Reject(() => new MicrophoneEffects(0, level).Validate()));
        Case("Reject corrupt live gate float", () => { var bytes = Fixture(); BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(152), float.NaN); TestSuite.Reject(() => ApoMicrophoneCodec.Read(bytes)); });
        Case("Reject corrupt live filter preset", () => { var bytes = Fixture(); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(160), 8); TestSuite.Reject(() => ApoMicrophoneCodec.Read(bytes)); });
        Case("All 303 legal gate/filter combinations preserve unrelated bytes", () => {
            for (var gate = 0; gate <= 100; gate++) for (var filter = 0; filter <= 2; filter++) {
                var original = Fixture(); var data = original.ToArray(); var target = new MicrophoneEffects(gate, filter);
                foreach (var p in ApoMicrophoneCodec.Prepare(data, target)) p.After.CopyTo(data, p.Offset);
                TestSuite.Assert(ApoMicrophoneCodec.Matches(ApoMicrophoneCodec.Read(data), target));
                for (var i = 0; i < data.Length; i++) if (i is < 152 or >= 164 || i is >= 156 and < 160) TestSuite.Assert(data[i] == original[i]);
            }
        });
        Case("Effects apply reads back and releases its session", () => { var fake = new FakeMemory(Fixture()); var result = Backend(fake).Apply(Mic, new(0, 2), new(37, 1)); TestSuite.Assert(ApoMicrophoneCodec.Matches(result, new(37, 1)) && fake.Disposed && fake.Notifications == 1); });
        Case("Unchanged effects send no writes or notification", () => { var fake = new FakeMemory(Fixture()); Backend(fake).Apply(Mic, new(0, 2), new(0, 2)); TestSuite.Assert(fake.Writes == 0 && fake.Notifications == 0); });
        Case("Stale effects reject before writes", () => { var fake = new FakeMemory(Fixture()); TestSuite.Reject(() => Backend(fake).Apply(Mic, new(20, 2), new(37, 1))); TestSuite.Assert(fake.Writes == 0 && fake.Disposed); });
        Case("Invalid target rejects before opening transport", () => { var opened = false; var backend = new ApoMicrophoneEffectsBackend(_ => { opened = true; throw new Exception(); }); TestSuite.Reject(() => backend.Apply(Mic, new(0, 2), new(101, 1))); TestSuite.Assert(!opened); });
        Case("Playback effects reject before opening transport", () => { var opened = false; var backend = new ApoMicrophoneEffectsBackend(_ => { opened = true; throw new Exception(); }); TestSuite.Reject(() => backend.Read(Mic with { Direction = AudioDirection.Playback })); TestSuite.Assert(!opened); });
        Case("Unknown model rejects before transport", () => TestSuite.Reject(() => Backend(new(Fixture())).Read(Mic with { Usb = Mic.Usb! with { ProductId = "FFFF" } })));
        Case("Foreign USB vendor rejects before transport", () => TestSuite.Reject(() => Backend(new(Fixture())).Read(Mic with { Usb = Mic.Usb! with { VendorId = "1234" } })));
        Case("Missing USB identity rejects before transport", () => TestSuite.Reject(() => Backend(new(Fixture())).Read(Mic with { Usb = null })));
        Case("Second write failure restores first field", () => { var fake = new FakeMemory(Fixture()) { FailWrite = 2 }; TestSuite.Throws<IOException>(() => Backend(fake).Apply(Mic, new(0, 2), new(37, 1))); TestSuite.Assert(fake.Bytes.SequenceEqual(Fixture()) && fake.Disposed); });
        Case("Notification failure restores exact prior bytes", () => { var fake = new FakeMemory(Fixture()) { FailNotify = 1 }; TestSuite.Throws<IOException>(() => Backend(fake).Apply(Mic, new(0, 2), new(37, 1))); TestSuite.Assert(fake.Bytes.SequenceEqual(Fixture())); });
        Case("Readback mismatch restores effects and preserves concurrent EQ", () => { var fake = new FakeMemory(Fixture()) { OnNotify = (f, n) => { if (n == 1) { f.Bytes[100] = 33; Fixture().AsSpan(152, 4).CopyTo(f.Bytes.AsSpan(152)); } } }; TestSuite.Throws<IOException>(() => Backend(fake).Apply(Mic, new(0, 2), new(37, 1))); TestSuite.Assert(fake.Bytes[100] == 33 && ApoMicrophoneCodec.Matches(ApoMicrophoneCodec.Read(fake.Bytes), new(0, 2))); });
        Case("Concurrent effect edit during failure is preserved", () => { var fake = new FakeMemory(Fixture()) { OnNotify = (f, n) => { if (n == 1) BinaryPrimitives.WriteSingleLittleEndian(f.Bytes.AsSpan(152), .83f); } }; TestSuite.Throws<AggregateException>(() => Backend(fake).Apply(Mic, new(0, 2), new(37, 1))); TestSuite.Assert(ApoMicrophoneCodec.Read(fake.Bytes).GatePercent == 83); });
        Case("Restoration failure reports both errors", () => { var fake = new FakeMemory(Fixture()) { FailNotify = 1, FailWrite = 3 }; var ex = TestSuite.Throws<AggregateException>(() => Backend(fake).Apply(Mic, new(0, 2), new(37, 1))); TestSuite.Assert(ex.InnerExceptions.Count == 2 && fake.Disposed); });
        Case("Disconnected endpoint fails identity guard", () => TestSuite.Reject(() => WindowsApoMemory.ValidateIdentity(Mic, [])));
        Case("Two same-model serials fail shared-object isolation", () => TestSuite.Reject(() => WindowsApoMemory.ValidateIdentity(Mic, [Mic, Mic with { Id = "other", Usb = Mic.Usb! with { InstanceId = "SECOND" } }])));
        Case("Capture/render endpoints of one unit are allowed", () => WindowsApoMemory.ValidateIdentity(Mic, [Mic, Mic with { Id = "render", Direction = AudioDirection.Playback }]));
        Case("Demo effects obey stale-state contract", () => { var backend = new DemoMicrophoneEffectsBackend(); var before = backend.Read(Mic); backend.Apply(Mic, before, new(23, 0)); TestSuite.Reject(() => backend.Apply(Mic, before, new(24, 1))); });
        Case("Demo effects isolate physical devices", () => { var backend = new DemoMicrophoneEffectsBackend(); backend.Apply(Mic, backend.Read(Mic), new(23, 0)); var other = Mic with { Usb = Mic.Usb! with { InstanceId = "SECOND" } }; TestSuite.Assert(ApoMicrophoneCodec.Matches(backend.Read(other), new(0, 2))); });
        Case("Transport timeout reaches caller without a write", () => TestSuite.Throws<TimeoutException>(() => new ApoMicrophoneEffectsBackend(_ => throw new TimeoutException()).Apply(Mic, new(0, 2), new(37, 1))));
        Case("Corrupt snapshot closes session before writes", () => { var bytes = Fixture(); bytes[4] = 5; var fake = new FakeMemory(bytes); TestSuite.Reject(() => Backend(fake).Apply(Mic, new(0, 2), new(37, 1))); TestSuite.Assert(fake.Writes == 0 && fake.Disposed); });
        Case("Read failure releases session", () => { var fake = new FakeMemory(Fixture()) { FailRead = 1 }; TestSuite.Throws<IOException>(() => Backend(fake).Read(Mic)); TestSuite.Assert(fake.Disposed && fake.Writes == 0); });
        Case("Lost readback attempts restoration and reports unreadable state", () => { var fake = new FakeMemory(Fixture()) { FailReadsFrom = 2 }; TestSuite.Throws<AggregateException>(() => Backend(fake).Apply(Mic, new(0, 2), new(37, 1))); TestSuite.Assert(fake.Disposed); });
        Case("Partial field write fails closed instead of guessing restoration", () => { var fake = new FakeMemory(Fixture()) { PartialWrite = true }; TestSuite.Throws<AggregateException>(() => Backend(fake).Apply(Mic, new(0, 2), new(37, 1))); TestSuite.Assert(fake.Disposed && fake.Writes == 1); });
        foreach (var adapter in new[] { "Demo", "APO" }) Case($"{adapter} adapter contract: round-trip, stale guard, invalid target", () => {
            IMicrophoneEffectsBackend backend = adapter == "Demo" ? new DemoMicrophoneEffectsBackend() : Backend(new FakeMemory(Fixture()));
            var before = backend.Read(Mic); var desired = new MicrophoneEffects(44, 0);
            TestSuite.Assert(ApoMicrophoneCodec.Matches(backend.Apply(Mic, before, desired), desired));
            TestSuite.Reject(() => backend.Apply(Mic, before, new(45, 2)));
            TestSuite.Reject(() => backend.Apply(Mic, desired, new(45, 3)));
            TestSuite.Assert(ApoMicrophoneCodec.Matches(backend.Read(Mic), desired));
        });
    }
    private static ApoMicrophoneEffectsBackend Backend(FakeMemory fake) => new(_ => fake);
    private sealed record Manifest(string File, string Hash);
    private sealed class FakeMemory(byte[] bytes) : IEffectMemorySession
    {
        public byte[] Bytes { get; } = bytes;
        public int Writes, Notifications, Reads, FailWrite, FailNotify, FailRead, FailReadsFrom;
        public bool Disposed, PartialWrite;
        public Action<FakeMemory, int>? OnNotify;
        public byte[] Read() { ++Reads; if (Reads == FailRead || FailReadsFrom > 0 && Reads >= FailReadsFrom) throw new IOException("Injected read failure"); return Bytes.ToArray(); }
        public void Write(int offset, byte[] data) { if (++Writes == FailWrite) throw new IOException("Injected write failure"); if (PartialWrite) { data.AsSpan(0, 2).CopyTo(Bytes.AsSpan(offset)); throw new IOException("Injected partial write"); } data.CopyTo(Bytes, offset); }
        public void Notify() { if (++Notifications == FailNotify) throw new IOException("Injected notify failure"); OnNotify?.Invoke(this, Notifications); }
        public void Dispose() => Disposed = true;
    }
}
