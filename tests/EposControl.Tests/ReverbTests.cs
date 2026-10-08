using System.Buffers.Binary;
using System.Text.Json;
using EposControl.Core;

internal static class ReverbTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".bin"));
    private static readonly AudioEndpoint Output = new DemoAudioBackend().Discover().First(e => e.Direction == AudioDirection.Playback);
    public static void Run(TestSuite suite)
    {
        suite.Case("GSX reverb live captures decode zero midpoint maximum and stereo retention", () => {
            TestSuite.Assert(ApoPlaybackCodec.Read(Fixture("gsx-reverb-zero")).Reverb == new PlaybackReverb(true, 0));
            TestSuite.Assert(ApoPlaybackCodec.Read(Fixture("gsx-reverb-mid")).Reverb == new PlaybackReverb(true, 129f / 255));
            TestSuite.Assert(ApoPlaybackCodec.Read(Fixture("gsx-reverb-max")).Reverb == new PlaybackReverb(true, 1));
            TestSuite.Assert(ApoPlaybackCodec.Read(Fixture("gsx-reverb-stereo-max")) is { SurroundEnabled: false, Reverb: { Enabled: false, Level: 1 } });
        });
        foreach (var (from, to) in new[] { ("gsx-reverb-zero", "gsx-reverb-mid"), ("gsx-reverb-mid", "gsx-reverb-max"), ("gsx-reverb-max", "gsx-reverb-zero-return") })
            suite.Case($"Reverb amount exactly reproduces Suite golden {from} -> {to}", () => {
                var bytes = Fixture(from); var desired = ApoPlaybackCodec.Read(Fixture(to));
                var patches = ApoPlaybackCodec.Prepare(bytes, desired);
                TestSuite.Assert(patches.Count == 1 && patches[0].Offset == 148 && patches[0].After.Length == 4);
                foreach (var patch in patches) patch.After.CopyTo(bytes, patch.Offset);
                TestSuite.Assert(bytes.SequenceEqual(Fixture(to)));
            });
        suite.Case("Suite surround and reverb flags reproduce stereo transition without losing amount", () => {
            var bytes = Fixture("gsx-reverb-max");
            var desired = ApoPlaybackCodec.Read(Fixture("gsx-reverb-stereo-max"));
            var patches = ApoPlaybackCodec.Prepare(bytes, desired);
            TestSuite.Assert(patches.Select(p => p.Offset).SequenceEqual(new[] { 64, 69 }));
            foreach (var patch in patches) patch.After.CopyTo(bytes, patch.Offset);
            TestSuite.Assert(bytes.SequenceEqual(Fixture("gsx-reverb-stereo-max")));
        });
        suite.Case("GSX reverb capture restores both complete device buffers", () => {
            TestSuite.Assert(Fixture("gsx-reverb-start").SequenceEqual(Fixture("gsx-reverb-restored")));
            var b20 = Fixture("b20-during-gsx-reverb-start");
            TestSuite.Assert(b20.SequenceEqual(Fixture("b20-during-gsx-reverb-max")) && b20.SequenceEqual(Fixture("b20-during-gsx-reverb-restored")));
        });
        suite.Case("Legacy mode EQ and profiles preserve exact reverb fields", () => {
            var memory = new Memory(Fixture("gsx-reverb-mid")); var before = ApoPlaybackCodec.Read(memory.Bytes);
            var backend = new ApoPlaybackEffectsBackend(_ => memory);
            var legacy = JsonSerializer.Deserialize<PlaybackEffects>("{\"SurroundEnabled\":false}")!;
            backend.Apply(Output, before, legacy);
            TestSuite.Assert(ApoPlaybackCodec.Read(memory.Bytes).Reverb == before.Reverb);
            TestSuite.Assert(memory.Bytes[69] == 1 && memory.Bytes.AsSpan(148, 4).SequenceEqual(Fixture("gsx-reverb-mid").AsSpan(148, 4)));
        });
        suite.Case("Reverb toggle retains amount and every other processor byte", () => {
            var original = Fixture("gsx-reverb-mid"); var bytes = original.ToArray(); var state = ApoPlaybackCodec.Read(bytes);
            var patches = ApoPlaybackCodec.Prepare(bytes, state with { Reverb = state.Reverb! with { Enabled = false } });
            TestSuite.Assert(patches.Count == 1 && patches[0].Offset == 69);
            patches[0].After.CopyTo(bytes, 69);
            for (var i = 0; i < bytes.Length; i++) if (i != 69) TestSuite.Assert(bytes[i] == original[i]);
        });
        foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, -.001f, 1.001f })
            suite.Case($"Invalid reverb amount {invalid} fails before transport", () => {
                var opened = false; var backend = new ApoPlaybackEffectsBackend(_ => { opened = true; throw new Exception(); });
                TestSuite.Reject(() => backend.Apply(Output, new(true, Reverb: new(true, 0)), new(true, Reverb: new(true, invalid))));
                TestSuite.Assert(!opened);
            });
        suite.Case("Corrupt reverb read cannot reach writes", () => {
            foreach (var corruptFlag in new[] { true, false }) {
                var bytes = Fixture("gsx-reverb-zero");
                if (corruptFlag) bytes[69] = 2; else BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(148), float.NaN);
                var memory = new Memory(bytes);
                TestSuite.Reject(() => new ApoPlaybackEffectsBackend(_ => memory).Apply(Output, new(true), new(false)));
                TestSuite.Assert(memory.Writes == 0);
            }
        });
        suite.Case("Stale or incomplete reverb snapshot fails before writes", () => {
            var memory = new Memory(Fixture("gsx-reverb-mid")); var backend = new ApoPlaybackEffectsBackend(_ => memory);
            TestSuite.Reject(() => backend.Apply(Output, new(true, Reverb: new(true, 0)), new(true, Reverb: new(true, 1))));
            TestSuite.Reject(() => backend.Apply(Output, new(true), new(true, Reverb: new(true, 1))));
            TestSuite.Assert(memory.Writes == 0);
        });
        suite.Case("Reverb failure restores attempted fields and preserves concurrent microphone edit", () => {
            var memory = new Memory(Fixture("gsx-reverb-mid")); var before = ApoPlaybackCodec.Read(memory.Bytes);
            memory.NotifyAction = m => { m.Bytes[68] = 1; throw new IOException("Injected failure"); };
            TestSuite.Throws<IOException>(() => new ApoPlaybackEffectsBackend(_ => memory).Apply(Output, before, before with { Reverb = new(false, 1) }));
            TestSuite.Assert(ApoPlaybackCodec.Read(memory.Bytes).Reverb == before.Reverb && memory.Bytes[68] == 1);
        });
        suite.Case("Newer reverb edit during readback failure is preserved", () => {
            var memory = new Memory(Fixture("gsx-reverb-mid")); var before = ApoPlaybackCodec.Read(memory.Bytes);
            memory.NotifyAction = m => BinaryPrimitives.WriteSingleLittleEndian(m.Bytes.AsSpan(148), .25f);
            TestSuite.Throws<AggregateException>(() => new ApoPlaybackEffectsBackend(_ => memory).Apply(Output, before, before with { Reverb = new(true, 1) }));
            TestSuite.Assert(ApoPlaybackCodec.Read(memory.Bytes).Reverb!.Level == .25f);
        });
        suite.Case("Reverb profile round-trips applies and preserves omitted legacy settings", () => {
            var audio = new DemoAudioBackend(); var sound = new DemoPlaybackEffectsBackend();
            var profile = new AudioProfile("Reverb", Output.Id, Output.ProfileIdentity, AudioDirection.Playback, .5f, false,
                Playback: new(true, PlaybackEqualizer.Flat, new(true, .376f)));
            var saved = JsonSerializer.Deserialize<AudioProfile>(JsonSerializer.Serialize(profile))!;
            TestSuite.Assert(saved == profile); ProfileStore.Apply(audio, Output, saved, playback: sound);
            TestSuite.Assert(sound.Read(Output) == saved.Playback);
            ProfileStore.Apply(audio, Output, saved with { Playback = new(false) }, playback: sound);
            TestSuite.Assert(sound.Read(Output).Reverb == saved.Playback!.Reverb);
            TestSuite.Throws<JsonException>(() => JsonSerializer.Deserialize<PlaybackReverb>("{\"Enabled\":true}"));
            TestSuite.Throws<JsonException>(() => JsonSerializer.Deserialize<PlaybackReverb>("{\"Level\":0.5}"));
        });
    }
    private sealed class Memory(byte[] bytes) : IEffectMemorySession
    {
        public byte[] Bytes = bytes; public int Writes, Notifications;
        public Action<Memory>? NotifyAction;
        public byte[] Read() => Bytes.ToArray();
        public void Write(int offset, byte[] data) { Writes++; data.CopyTo(Bytes, offset); }
        public void Notify() { if (++Notifications == 1) NotifyAction?.Invoke(this); }
        public void Dispose() { }
    }
}
