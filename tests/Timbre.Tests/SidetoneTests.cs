using System.Text.Json;
using Timbre.Core;

internal static class SidetoneTests
{
    private static readonly AudioEndpoint Mic = new DemoAudioBackend().Discover().First();
    private static readonly SidetoneState Initial = new("b20-control", -34.5f, 9, 1.5f, new(2.9765625f, 2.9765625f, false));
    private static AudioTopologySnapshot Fixture(string name) => JsonSerializer.Deserialize<AudioTopologySnapshot>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "b20-topology-" + name + ".json")))!;
    public static void Run(TestSuite suite)
    {
        foreach (var (name, db) in new[] { ("before-sidetone", 9.0859375f), ("sidetone50", 2.9765625f), ("sidetone0", -31f) })
            suite.Case("Captured B20 sidetone layout " + name, () => {
                var node = B20SidetoneLayout.ValidateLayout(Fixture(name));
                TestSuite.Assert(node.Levels!.All(l => l.Decibels == db));
            });
        suite.Case("Suite sidetone captures change only the identified stereo volume", () => {
            var before = Fixture("before-sidetone");
            foreach (var name in new[] { "sidetone50", "sidetone0" }) {
                var after = Fixture(name);
                TestSuite.Assert(before.Parts.Count == after.Parts.Count);
                foreach (var part in before.Parts) if (part.LocalId != 0x20007)
                    TestSuite.Assert(JsonSerializer.Serialize(part) == JsonSerializer.Serialize(after.Parts.Single(p => p.LocalId == part.LocalId)));
            }
        });
        suite.Case("Unknown sidetone topology does not inherit B20 volume commands", () => {
            var snapshot = Fixture("sidetone50"); var parts = snapshot.Parts.ToArray();
            TestSuite.Reject(() => B20SidetoneLayout.ValidateLayout(snapshot with { DeviceId = "GSX300" }));
            var index = Array.FindIndex(parts, p => p.LocalId == 0x20007);
            parts[index] = parts[index] with { Outgoing = [0x20001] };
            TestSuite.Reject(() => B20SidetoneLayout.ValidateLayout(snapshot with { Parts = parts }));
        });
        suite.Case("Unknown sidetone ranges and channel counts reject before writes", () => {
            var snapshot = Fixture("sidetone50"); var parts = snapshot.Parts.ToArray(); var index = Array.FindIndex(parts, p => p.LocalId == 0x20007);
            parts[index] = parts[index] with { Levels = [new(0, -50, 0, 1)] };
            TestSuite.Reject(() => B20SidetoneLayout.ValidateLayout(snapshot with { Parts = parts }));
        });
        suite.Case("Sidetone uses actual native levels with an independent mute switch", () => {
            var fake = new Session(); var backend = new SidetoneBackend(_ => fake);
            var desired = new SidetoneSettings(-10, -11, true); var result = backend.Apply(Mic, Initial, desired);
            TestSuite.Assert(result.Settings == desired && fake.Order.SequenceEqual(new[] { "mute", "levels" }) && fake.Disposed);
        });
        suite.Case("Sidetone unmutes after changing its levels", () => {
            var fake = new Session { State = Initial with { Settings = Initial.Settings with { Muted = true } } };
            new SidetoneBackend(_ => fake).Apply(Mic, fake.State, new(-10, -11, false));
            TestSuite.Assert(fake.Order.SequenceEqual(new[] { "levels", "mute" }));
        });
        suite.Case("Unchanged sidetone sends no writes", () => { var f = new Session(); new SidetoneBackend(_ => f).Apply(Mic, Initial, Initial.Settings); TestSuite.Assert(f.Writes == 0 && f.Disposed); });
        suite.Case("Stale sidetone rejects before writes", () => {
            var f = new Session { State = Initial with { Settings = new(0, 0, false) } };
            TestSuite.Reject(() => new SidetoneBackend(_ => f).Apply(Mic, Initial, new(-10, -10, true))); TestSuite.Assert(f.Writes == 0 && f.Disposed);
        });
        suite.Case("Sidetone control identity is part of stale state checks", () => {
            var f = new Session { State = Initial with { ControlIdentity = "other-device" } };
            TestSuite.Reject(() => new SidetoneBackend(_ => f).Apply(Mic, Initial, new(0, 0, false))); TestSuite.Assert(f.Writes == 0);
        });
        foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, -34.51f, 9.1f })
            suite.Case("Invalid sidetone level rejects before opening transport " + invalid, () => {
                var opens = 0; TestSuite.Reject(() => new SidetoneBackend(_ => { opens++; return new Session(); }).Apply(Mic, Initial, new(invalid, 0, false))); TestSuite.Assert(opens == 0);
            });
        suite.Case("Sidetone playback, GSX, unknown vendor and missing identity reject before transport", () => {
            var opens = 0; var backend = new SidetoneBackend(_ => { opens++; return new Session(); });
            foreach (var e in new[] { Mic with { Direction = AudioDirection.Playback }, Mic with { Usb = Mic.Usb! with { ProductId = "0098" } }, Mic with { Usb = Mic.Usb! with { VendorId = "1234" } }, Mic with { Usb = null } })
                TestSuite.Reject(() => backend.Apply(e, Initial, new(0, 0, false)));
            TestSuite.Assert(opens == 0);
        });
        suite.Case("Sidetone partial stereo write restores exact starting settings", () => {
            var f = new Session { PartialFailOn = 1 }; TestSuite.Throws<IOException>(() => new SidetoneBackend(_ => f).Apply(Mic, Initial, new(-10, -11, false)));
            TestSuite.Assert(f.State == Initial && f.Disposed);
        });
        suite.Case("Sidetone second write failure restores mute and levels", () => {
            var f = new Session { FailOn = 2 }; TestSuite.Throws<IOException>(() => new SidetoneBackend(_ => f).Apply(Mic, Initial, new(-10, -10, true)));
            TestSuite.Assert(f.State == Initial && f.Disposed);
        });
        suite.Case("Sidetone rollback preserves an untouched newer mute", () => {
            var f = new Session { PartialFailOn = 1 };
            f.AfterWrite = () => { if (f.Writes == 1) f.State = f.State with { Settings = f.State.Settings with { Muted = true } }; };
            TestSuite.Throws<IOException>(() => new SidetoneBackend(_ => f).Apply(Mic, Initial, new(-10, -10, false)));
            TestSuite.Assert(f.State.Settings == Initial.Settings with { Muted = true });
        });
        suite.Case("Sidetone rollback preserves ambiguous newer levels", () => {
            var f = new Session { PartialFailOn = 1 };
            f.AfterWrite = () => { if (f.Writes == 1) f.State = f.State with { Settings = new(-15, -15, false) }; };
            TestSuite.Throws<AggregateException>(() => new SidetoneBackend(_ => f).Apply(Mic, Initial, new(-10, -10, false)));
            TestSuite.Assert(f.State.Settings.LeftDb == -15 && f.Disposed);
        });
        suite.Case("Sidetone restoration failure reports both errors", () => {
            var f = new Session { PartialFailOn = 1, FailOn = 2 };
            var ex = TestSuite.Throws<AggregateException>(() => new SidetoneBackend(_ => f).Apply(Mic, Initial, new(-10, -10, false)));
            TestSuite.Assert(ex.InnerExceptions.Count == 2 && f.Disposed);
        });
        suite.Case("Sidetone fixed-point readback accepts driver rounding", () => {
            var f = new Session { Quantize = true }; var result = new SidetoneBackend(_ => f).Apply(Mic, Initial, new(-10.123f, -10.321f, false));
            TestSuite.Assert(SidetoneBackend.Matches(result.Settings, new(-10.123f, -10.321f, false)));
        });
        suite.Case("Sidetone profiles round-trip and restore with complete processing", () => {
            var audio = new DemoAudioBackend(); var effects = new DemoMicrophoneEffectsBackend(); var sidetone = new DemoSidetoneBackend();
            var p = new AudioProfile("B20", Mic.Id, Mic.ProfileIdentity, Mic.Direction, .5f, false, new(10, 1), new(-9, -8, true));
            TestSuite.Assert(JsonSerializer.Deserialize<AudioProfile>(JsonSerializer.Serialize(p)) == p);
            ProfileStore.Apply(audio, Mic, p, effects, sidetone); TestSuite.Assert(sidetone.Read(Mic).Settings == p.Sidetone && effects.Read(Mic).GatePercent == 10);
        });
        suite.Case("Legacy profiles preserve current sidetone", () => {
            var audio = new DemoAudioBackend(); var side = new DemoSidetoneBackend(); var before = side.Read(Mic);
            var p = new AudioProfile("Old", Mic.Id, Mic.ProfileIdentity, Mic.Direction, .5f, false, new(10, 1));
            ProfileStore.Apply(audio, Mic, p, new DemoMicrophoneEffectsBackend(), side); TestSuite.Assert(side.Read(Mic) == before);
        });
        suite.Case("Missing sidetone adapter rejects profile before level writes", () => {
            var audio = new DemoAudioBackend(); var before = audio.Read(Mic.Id);
            TestSuite.Reject(() => ProfileStore.Apply(audio, Mic, new("Side", Mic.Id, Mic.ProfileIdentity, Mic.Direction, .9f, true, null, new(0, 0, false))));
            TestSuite.Assert(audio.Read(Mic.Id) == before);
        });
        suite.Case("Sidetone profile failure restores already-applied processing and level", () => {
            var audio = new DemoAudioBackend(); var effects = new DemoMicrophoneEffectsBackend(); var before = audio.Read(Mic.Id); var beforeEffects = effects.Read(Mic);
            var p = new AudioProfile("All", Mic.Id, Mic.ProfileIdentity, Mic.Direction, .7f, true, new(35, 0), new(0, 0, false));
            TestSuite.Throws<IOException>(() => ProfileStore.Apply(audio, Mic, p, effects, new FailedSidetone()));
            TestSuite.Assert(audio.Read(Mic.Id).Level == before.Level && audio.Read(Mic.Id).Muted == before.Muted && effects.Read(Mic) == beforeEffects);
        });
        suite.Case("Incomplete sidetone profile JSON cannot guess omitted values", () => {
            TestSuite.Throws<JsonException>(() => JsonSerializer.Deserialize<SidetoneSettings>("{\"LeftDb\":0,\"RightDb\":0}"));
        });
        suite.Case("Sidetone profile failure preserves newer processing edits", () => {
            var audio = new DemoAudioBackend(); var effects = new DemoMicrophoneEffectsBackend(); var before = audio.Read(Mic.Id);
            var external = new MicrophoneEffects(51, 2);
            var side = new FailedSidetone { BeforeFailure = () => effects.Apply(Mic, effects.Read(Mic), external) };
            var p = new AudioProfile("All", Mic.Id, Mic.ProfileIdentity, Mic.Direction, .7f, true, new(35, 0), new(0, 0, false));
            TestSuite.Throws<AggregateException>(() => ProfileStore.Apply(audio, Mic, p, effects, side));
            TestSuite.Assert(ApoMicrophoneCodec.Matches(effects.Read(Mic), external) && audio.Read(Mic.Id).Level == before.Level && audio.Read(Mic.Id).Muted == before.Muted);
        });
        suite.Case("Sidetone profile read failure rejects before any device writes", () => {
            var audio = new DemoAudioBackend(); var before = audio.Read(Mic.Id); var effects = new DemoMicrophoneEffectsBackend(); var beforeEffects = effects.Read(Mic);
            var p = new AudioProfile("All", Mic.Id, Mic.ProfileIdentity, Mic.Direction, .7f, true, new(35, 0), new(0, 0, false));
            TestSuite.Throws<IOException>(() => ProfileStore.Apply(audio, Mic, p, effects, new FailedSidetone { FailRead = true }));
            TestSuite.Assert(audio.Read(Mic.Id) == before && effects.Read(Mic) == beforeEffects);
        });
        suite.Case("Playback and foreign sidetone profiles reject before writes", () => {
            var audio = new DemoAudioBackend(); var before = audio.Read(Mic.Id);
            var p = new AudioProfile("Side", Mic.Id, Mic.ProfileIdentity, Mic.Direction, .7f, true, null, new(0, 0, false));
            TestSuite.Reject(() => ProfileStore.Apply(audio, Mic, p with { DeviceIdentity = "other" }, null, new DemoSidetoneBackend()));
            var output = audio.Discover().First(e => e.Direction == AudioDirection.Playback);
            TestSuite.Reject(() => ProfileStore.Apply(audio, output, p with { Direction = AudioDirection.Playback, DeviceIdentity = output.ProfileIdentity }, null, new DemoSidetoneBackend()));
            TestSuite.Assert(audio.Read(Mic.Id) == before);
        });
        suite.Case("Missing sidetone state fails closed before native writes", () => {
            var f = new Session { State = Initial with { Settings = null! } };
            TestSuite.Reject(() => new SidetoneBackend(_ => f).Apply(Mic, Initial, new(0, 0, false))); TestSuite.Assert(f.Writes == 0 && f.Disposed);
        });
    }
    private sealed class FailedSidetone : ISidetoneBackend
    {
        internal Action? BeforeFailure; internal bool FailRead;
        public SidetoneState Read(AudioEndpoint e) => FailRead ? throw new IOException("Sidetone read lost connection.") : Initial;
        public SidetoneState Apply(AudioEndpoint e, SidetoneState expected, SidetoneSettings desired) { BeforeFailure?.Invoke(); throw new IOException("Sidetone write lost connection."); }
    }
    private sealed class Session : ISidetoneSession
    {
        internal SidetoneState State = Initial; internal int Writes, FailOn, PartialFailOn; internal bool Disposed, Quantize;
        internal Action? AfterWrite; internal List<string> Order = [];
        public SidetoneState Read() => State;
        public void WriteLevels(float left, float right)
        {
            Writes++; Order.Add("levels"); if (Writes == FailOn) throw new IOException("Write failed.");
            float Value(float v) => Quantize ? (float)(Math.Truncate(v * 256) / 256) : v;
            State = State with { Settings = State.Settings with { LeftDb = Value(left), RightDb = Writes == PartialFailOn ? State.Settings.RightDb : Value(right) } };
            AfterWrite?.Invoke(); if (Writes == PartialFailOn) throw new IOException("Partial write failed.");
        }
        public void WriteMute(bool muted) { Writes++; Order.Add("mute"); if (Writes == FailOn) throw new IOException("Write failed."); State = State with { Settings = State.Settings with { Muted = muted } }; AfterWrite?.Invoke(); }
        public void Dispose() => Disposed = true;
    }
}
