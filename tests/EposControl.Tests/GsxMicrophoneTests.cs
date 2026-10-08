using EposControl.Core;

internal static class GsxMicrophoneTests
{
    private static byte[] Fixture(string stage) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gsx-microphone-" + stage + ".bin"));
    public static void Run(TestSuite suite)
    {
        var audio = new DemoAudioBackend();
        var gsx = audio.Discover().Single(e => e.Usb?.ProductId == "0098" && e.Direction == AudioDirection.Microphone);
        var b20 = audio.Discover().First();
        foreach (var (from, to) in new[] { ("page", "filter2"), ("filter2", "filter1"), ("filter1", "filter0"), ("filter0", "warm"), ("warm", "clear"), ("clear", "eq-restored"), ("page", "gate-mid") })
            suite.Case($"GSX microphone golden {from} -> {to} reproduces complete Suite bytes", () => {
                var bytes = Fixture(from); var target = Fixture(to);
                foreach (var patch in ApoMicrophoneCodec.Prepare(bytes, ApoMicrophoneCodec.Read(target))) {
                    WindowsApoMemory.ValidateMicrophonePatch(gsx, patch.Offset, patch.After.Length);
                    patch.After.CopyTo(bytes, patch.Offset);
                }
                TestSuite.Assert(bytes.SequenceEqual(target));
            });
        suite.Case("GSX microphone presets match all nine Warm and Clear band values", () => {
            TestSuite.Assert(ApoMicrophoneCodec.Read(Fixture("warm")).Processing!.Equalizer == MicrophoneEqPresets.Warm);
            TestSuite.Assert(ApoMicrophoneCodec.Read(Fixture("clear")).Processing!.Equalizer == MicrophoneEqPresets.Clear);
            TestSuite.Assert(Math.Abs(ApoMicrophoneCodec.Read(Fixture("gate-mid")).GatePercent - 51) < .001f);
        });
        suite.Case("GSX and B20 microphone transports have distinct model names", () => {
            TestSuite.Assert(WindowsApoMemory.MicrophoneObjectName(gsx).EndsWith("1395_0098"));
            TestSuite.Assert(WindowsApoMemory.MicrophoneObjectName(b20).EndsWith("1395_009f"));
            TestSuite.Reject(() => WindowsApoMemory.MicrophoneObjectName(gsx with { Direction = AudioDirection.Playback }));
            TestSuite.Reject(() => WindowsApoMemory.MicrophoneObjectName(gsx with { Usb = gsx.Usb! with { ProductId = "FFFF" } }));
        });
        suite.Case("GSX microphone whitelist rejects high-pass, playback and unknown fields", () => {
            foreach (var offset in new[] { 64, 65, 69, 70, 71, 76, 108, 148 })
                TestSuite.Reject(() => WindowsApoMemory.ValidateMicrophonePatch(gsx, offset, 1));
            foreach (var offset in new[] { 66, 67, 68 }) WindowsApoMemory.ValidateMicrophonePatch(gsx, offset, 1);
            foreach (var offset in new[] { 112, 116, 120, 124, 128, 132, 136, 140, 144, 152, 160 }) WindowsApoMemory.ValidateMicrophonePatch(gsx, offset, 4);
            WindowsApoMemory.ValidateMicrophonePatch(b20, 71, 1);
        });
        suite.Case("Unsupported GSX high-pass edit rejects before opening native transport", () => {
            var before = ApoMicrophoneCodec.Read(Fixture("page")); var opened = false;
            var backend = new ApoMicrophoneEffectsBackend(_ => { opened = true; throw new Exception(); });
            TestSuite.Reject(() => backend.Apply(gsx, before, before with { Processing = before.Processing! with { HighPassEnabled = !before.Processing!.HighPassEnabled } }));
            TestSuite.Assert(!opened);
        });
        suite.Case("Unsupported GSX profile rejects before endpoint changes", () => {
            var effects = new DemoMicrophoneEffectsBackend(); var before = effects.Read(gsx); var audioBefore = audio.Read(gsx.Id);
            var profile = new AudioProfile("Invalid HPF", gsx.Id, ProfileStore.DeviceIdentity(gsx), gsx.Direction, .01f, true,
                before with { Processing = before.Processing! with { HighPassEnabled = !before.Processing.HighPassEnabled } });
            TestSuite.Reject(() => ProfileStore.Apply(audio, gsx, profile, effects));
            TestSuite.Assert(audio.Read(gsx.Id) == audioBefore && effects.Read(gsx) == before);
        });
        suite.Case("GSX microphone profile restores processing without changing B20 or playback", () => {
            var effects = new DemoMicrophoneEffectsBackend(); var before = effects.Read(gsx); var b20Before = effects.Read(b20);
            var sound = audio.Discover().Single(e => e.Direction == AudioDirection.Playback); var soundBefore = audio.Read(sound.Id);
            var target = before with { GatePercent = 51, FilterLevel = 1, Processing = before.Processing! with { Equalizer = MicrophoneEqPresets.Clear } };
            var profile = new AudioProfile("GSX mic", gsx.Id, ProfileStore.DeviceIdentity(gsx), gsx.Direction, .61f, false, target);
            ProfileStore.Apply(audio, gsx, profile, effects);
            TestSuite.Assert(effects.Read(gsx) == target && effects.Read(b20) == b20Before && audio.Read(sound.Id) == soundBefore);
        });
    }
}
