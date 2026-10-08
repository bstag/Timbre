using System.Buffers.Binary;
using System.Text.Json;
using Timbre.Core;

internal static class B20ProcessingTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".bin"));
    private static byte[] Patched(byte[] before, MicrophoneEffects desired)
    {
        var bytes = before.ToArray(); foreach (var patch in ApoMicrophoneCodec.Prepare(before, desired)) patch.After.CopyTo(bytes, patch.Offset); return bytes;
    }
    public static void Run(TestSuite suite)
    {
        void Case(string name, Action action) => suite.Case(name, action);
        Case("B20 frequency labels and EQ field order match the Suite screenshot golden", () => {
            using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "b20-eq-frequency-map.json")));
            var bands = golden.RootElement.GetProperty("Bands").EnumerateArray().ToArray();
            TestSuite.Assert(bands.Length == MicrophoneEqPresets.B20BandLabels.Count);
            var before = Fixture("b20-controls-start"); var state = ApoMicrophoneCodec.Read(before);
            for (var i = 0; i < bands.Length; i++) {
                TestSuite.Assert(bands[i].GetProperty("Label").GetString() == MicrophoneEqPresets.B20BandLabels[i]);
                var levels = new float[9]; levels[i] = 3;
                var patches = ApoMicrophoneCodec.Prepare(before, state with { Processing = state.Processing! with { Equalizer = MicrophoneEqualizer.FromLevels(levels) } });
                TestSuite.Assert(patches.Count == 1 && patches[0].Offset == bands[i].GetProperty("ApoOffset").GetInt32());
                TestSuite.Assert(BinaryPrimitives.ReadSingleLittleEndian(patches[0].After) == 3);
            }
        });
        foreach (var (name, highPass) in new[] { ("b20-hpf-off", false), ("b20-hpf-on", true) })
            Case($"Captured {name} reads correct high-pass state", () => TestSuite.Assert(ApoMicrophoneCodec.Read(Fixture(name)).Processing!.HighPassEnabled == highPass));
        Case("Golden high-pass toggle reverses exactly one byte", () => {
            var before = Fixture("b20-hpf-off"); var state = ApoMicrophoneCodec.Read(before);
            TestSuite.Assert(Patched(before, state with { Processing = state.Processing! with { HighPassEnabled = true } }).SequenceEqual(Fixture("b20-hpf-on")));
        });
        Case("Golden custom EQ band +6 matches full captured buffer", () => {
            var before = Fixture("b20-hpf-on"); var state = ApoMicrophoneCodec.Read(before);
            var eq = state.Processing!.Equalizer with { Band1 = 6 };
            TestSuite.Assert(Patched(before, state with { Processing = state.Processing with { Equalizer = eq } }).SequenceEqual(Fixture("b20-eq-band1-plus6")));
        });
        foreach (var (beforeName, afterName, preset) in new[] { ("b20-eq-band1-plus6", "b20-eq-warm", MicrophoneEqPresets.Warm), ("b20-eq-warm", "b20-eq-clear", MicrophoneEqPresets.Clear) })
            Case($"Golden EQ preset {afterName} matches all captured bytes", () => {
                var before = Fixture(beforeName); var state = ApoMicrophoneCodec.Read(before);
                TestSuite.Assert(ApoMicrophoneCodec.Read(Fixture(afterName)).Processing!.Equalizer == preset);
                TestSuite.Assert(Patched(before, state with { Processing = state.Processing! with { Equalizer = preset } }).SequenceEqual(Fixture(afterName)));
            });
        Case("Read-then-prepare is a byte-exact no-op for every APO capture", () => {
            // These named fixtures contain HID status reports, not APO memory.
            foreach (var path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "*.bin").Where(path =>
                !Path.GetFileName(path).StartsWith("b20-pattern-", StringComparison.Ordinal) &&
                !Path.GetFileName(path).StartsWith("gsx-sidetone-", StringComparison.Ordinal))) {
                var bytes = File.ReadAllBytes(path); TestSuite.Assert(ApoMicrophoneCodec.Prepare(bytes, ApoMicrophoneCodec.Read(bytes)).Count == 0, Path.GetFileName(path));
            }
        });
        Case("All 16 switch combinations round-trip and preserve unrelated bytes", () => {
            var before = Fixture("b20-controls-start"); var state = ApoMicrophoneCodec.Read(before);
            for (var mask = 0; mask < 16; mask++) {
                var processing = new MicrophoneProcessing((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, (mask & 8) != 0, state.Processing!.Equalizer);
                var bytes = Patched(before, state with { Processing = processing });
                TestSuite.Assert(ApoMicrophoneCodec.Read(bytes).Processing == processing);
                for (var i = 0; i < bytes.Length; i++) if (i is not (66 or 67 or 68 or 71)) TestSuite.Assert(bytes[i] == before[i]);
            }
        });
        foreach (var offset in new[] { 66, 67, 68, 71 }) Case($"Corrupt switch byte {offset} rejects the snapshot", () => { var bytes = Fixture("b20-controls-start"); bytes[offset] = 2; TestSuite.Reject(() => ApoMicrophoneCodec.Read(bytes)); });
        Case("Every EQ band supports negative, fractional and boundary values", () => {
            var before = Fixture("b20-controls-start"); var state = ApoMicrophoneCodec.Read(before);
            for (var band = 0; band < 9; band++) foreach (var value in new[] { -6f, -3.02f, 0f, .01f, 6f }) {
                var levels = new float[9]; levels[band] = value; var eq = MicrophoneEqualizer.FromLevels(levels);
                var bytes = Patched(before, state with { Processing = state.Processing! with { Equalizer = eq } });
                TestSuite.Assert(ApoMicrophoneCodec.Read(bytes).Processing!.Equalizer == eq);
                for (var i = 0; i < bytes.Length; i++) if (i < 112 || i >= 148) TestSuite.Assert(bytes[i] == before[i]);
            }
        });
        foreach (var value in new[] { float.NaN, float.PositiveInfinity, -6.01f, 6.01f }) Case($"Reject invalid EQ value {value}", () => TestSuite.Reject(() => (MicrophoneEqualizer.Flat with { Band9 = value }).Validate()));
        foreach (var count in new[] { 0, 8, 10 }) Case($"Reject EQ vector length {count}", () => TestSuite.Throws<ArgumentException>(() => MicrophoneEqualizer.FromLevels(new float[count])));
        Case("EQ representation does not retain mutable caller arrays", () => { var levels = new float[9]; levels[0] = 3; var eq = MicrophoneEqualizer.FromLevels(levels); levels[0] = -3; var copy = eq.Levels(); copy[0] = 6; TestSuite.Assert(eq.Band1 == 3); });
        Case("Old effects JSON preserves switches and EQ when applied", () => {
            var old = JsonSerializer.Deserialize<MicrophoneEffects>("{\"GatePercent\":25,\"FilterLevel\":1}")!;
            var bytes = Fixture("b20-eq-warm"); var before = ApoMicrophoneCodec.Read(bytes); var changed = ApoMicrophoneCodec.Read(Patched(bytes, old));
            TestSuite.Assert(changed.Processing == before.Processing && ApoMicrophoneCodec.Matches(changed, old));
        });
        Case("Complete processing profile JSON round-trips", () => { var state = ApoMicrophoneCodec.Read(Fixture("b20-eq-clear")); TestSuite.Assert(JsonSerializer.Deserialize<MicrophoneEffects>(JsonSerializer.Serialize(state)) == state); });
        Case("Unknown starting processing state rejects advanced updates", () => TestSuite.Reject(() => MicrophoneEffects.ValidateUpdate(new(0, 2), ApoMicrophoneCodec.Read(Fixture("b20-controls-start")))));
        Case("Stale high-pass state is included in comparisons", () => { var state = ApoMicrophoneCodec.Read(Fixture("b20-controls-start")); TestSuite.Assert(!ApoMicrophoneCodec.Matches(state, state with { Processing = state.Processing! with { HighPassEnabled = !state.Processing.HighPassEnabled } })); });
        Case("Stale EQ state is included in comparisons", () => { var state = ApoMicrophoneCodec.Read(Fixture("b20-controls-start")); TestSuite.Assert(!ApoMicrophoneCodec.Matches(state, state with { Processing = state.Processing! with { Equalizer = MicrophoneEqPresets.Warm } })); });
        Case("Write whitelist permits only full validated fields", () => {
            foreach (var offset in new[] { 66, 67, 68, 71 }) ApoMicrophoneCodec.ValidatePatch(offset, 1);
            foreach (var offset in Enumerable.Range(0, 9).Select(i => 112 + 4 * i).Append(152).Append(160)) ApoMicrophoneCodec.ValidatePatch(offset, 4);
            foreach (var (offset, length) in new[] { (65, 1), (72, 1), (177, 1), (112, 36), (113, 4), (148, 4), (152, 2), (66, 4), (0, 4096), (-1, 1) }) TestSuite.Reject(() => ApoMicrophoneCodec.ValidatePatch(offset, length));
        });
        Case("Null nested EQ state rejects before preparation", () => { var state = ApoMicrophoneCodec.Read(Fixture("b20-controls-start")); TestSuite.Reject(() => (state with { Processing = state.Processing! with { Equalizer = null! } }).Validate()); });
        Case("Corrupt EQ live float cannot reach controls", () => { var bytes = Fixture("b20-controls-start"); BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(144), float.NaN); TestSuite.Reject(() => ApoMicrophoneCodec.Read(bytes)); });
        Case("Warm and Clear preset names remain distinguishable from Custom", () => { TestSuite.Assert(MicrophoneEqPresets.Name(MicrophoneEqPresets.Warm) == "Warm" && MicrophoneEqPresets.Name(MicrophoneEqPresets.Clear) == "Clear" && MicrophoneEqPresets.Name(MicrophoneEqPresets.Flat with { Band9 = 1 }) == "Custom"); });
        Case("Nonfinite gate state never compares equal", () => TestSuite.Assert(!ApoMicrophoneCodec.Matches(new(float.NaN, 1), new(float.NaN, 1))));
        Case("Incomplete processing JSON cannot guess enable switches", () => TestSuite.Throws<JsonException>(() => JsonSerializer.Deserialize<MicrophoneProcessing>("{\"Equalizer\":" + JsonSerializer.Serialize(MicrophoneEqualizer.Flat) + "}")));
        Case("Incomplete EQ JSON cannot guess omitted bands", () => TestSuite.Throws<JsonException>(() => JsonSerializer.Deserialize<MicrophoneEqualizer>("{\"Band1\":2}")));
        Case("Incomplete legacy effect JSON cannot guess gate/filter", () => TestSuite.Throws<JsonException>(() => JsonSerializer.Deserialize<MicrophoneEffects>("{\"GatePercent\":2}")));
        Case("Mixed one-byte/float transaction restores all attempted fields", () => {
            var original = Fixture("b20-controls-start"); var memory = new FaultMemory(original.ToArray()) { FailWrite = 4 }; var endpoint = new DemoAudioBackend().Discover().First();
            var before = ApoMicrophoneCodec.Read(original); var desired = before with { GatePercent = 20, FilterLevel = 1, Processing = before.Processing! with { FilterEnabled = false, HighPassEnabled = false, Equalizer = MicrophoneEqPresets.Warm } };
            TestSuite.Throws<IOException>(() => new ApoMicrophoneEffectsBackend(_ => memory).Apply(endpoint, before, desired));
            TestSuite.Assert(memory.Bytes.SequenceEqual(original) && memory.Disposed);
        });
        Case("Advanced rollback preserves an untouched concurrent mic EQ band", () => {
            var original = Fixture("b20-controls-start"); var memory = new FaultMemory(original.ToArray()) { ChangeBand9OnNotify = true }; var endpoint = new DemoAudioBackend().Discover().First();
            var before = ApoMicrophoneCodec.Read(original); var desired = before with { Processing = before.Processing! with { HighPassEnabled = false, Equalizer = MicrophoneEqPresets.Warm } };
            TestSuite.Throws<IOException>(() => new ApoMicrophoneEffectsBackend(_ => memory).Apply(endpoint, before, desired));
            var expected = original.ToArray(); BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(144), .25f);
            TestSuite.Assert(memory.Bytes.SequenceEqual(expected) && memory.Disposed);
        });
        Case("Full effects profile applies switches and all EQ bands", () => {
            var audio = new DemoAudioBackend(); var effects = new DemoMicrophoneEffectsBackend(); var endpoint = audio.Discover().First();
            var state = effects.Read(endpoint) with { Processing = new(false, false, false, true, MicrophoneEqPresets.Clear with { Band9 = 2.5f }) };
            var profile = new AudioProfile("Full", endpoint.Id, endpoint.ProfileIdentity, endpoint.Direction, .5f, false, state);
            ProfileStore.Apply(audio, endpoint, profile, effects); TestSuite.Assert(effects.Read(endpoint) == state);
        });
    }
    private sealed class FaultMemory(byte[] bytes) : IEffectMemorySession
    {
        public byte[] Bytes { get; } = bytes;
        public int FailWrite, Writes, Notifications;
        public bool Disposed, ChangeBand9OnNotify;
        public byte[] Read() => Bytes.ToArray();
        public void Write(int offset, byte[] data) { if (++Writes == FailWrite) throw new IOException("Injected write failure"); data.CopyTo(Bytes, offset); }
        public void Notify() { if (++Notifications == 1 && ChangeBand9OnNotify) BinaryPrimitives.WriteSingleLittleEndian(Bytes.AsSpan(144), .25f); }
        public void Dispose() => Disposed = true;
    }
}
