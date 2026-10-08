using System.Buffers.Binary;
using Timbre.Core;

internal static class AudioValidationTests
{
    public static void Run(TestSuite suite)
    {
        suite.Case("Float capture meter measures interleaved RMS, peak and duration", () => {
            var meter = new AudioMeter(new(2, 48000, 32, true));
            var bytes = Floats(.5f, -.5f, .5f, -.5f); meter.AddPacket(bytes, 2, 0);
            var result = meter.Result(); TestSuite.Assert(result.Frames == 2 && Math.Abs(result.RmsDbFs + 6.0206) < .001 && result.PeakDbFs == result.RmsDbFs);
            TestSuite.Assert(Math.Abs(result.Seconds - 2d / 48000) < 1e-10);
        });
        foreach (var bits in new[] { 16, 24, 32 }) suite.Case($"PCM {bits}-bit signed minimum is normalized correctly", () => {
            var data = new byte[bits / 8]; data[^1] = 0x80;
            var meter = new AudioMeter(new(1, 48000, bits, false)); meter.AddPacket(data, 1, 0);
            TestSuite.Assert(meter.Result().PeakDbFs == 0 && meter.Result().ClippedSamples == 1);
        });
        suite.Case("WASAPI silent packets count frames without reading data", () => {
            var meter = new AudioMeter(new(2, 48000, 32, true)); meter.AddPacket([], 48000, 2);
            TestSuite.Assert(meter.Result().Seconds == 1 && meter.Result().RmsDbFs == -160);
        });
        suite.Case("Capture meter rejects malformed packets", () => {
            var meter = new AudioMeter(new(2, 48000, 32, true));
            TestSuite.Reject(() => meter.AddPacket([0], 1, 0)); TestSuite.Reject(() => meter.AddPacket([], -1, 2));
        });
        suite.Case("Capture invalid samples and glitch flags cannot masquerade as a pass", () => {
            var meter = new AudioMeter(new(1, 48000, 32, true)); meter.AddPacket(Floats(float.NaN, float.PositiveInfinity, 1), 3, 5);
            var r = meter.Result(); TestSuite.Assert(r.InvalidSamples == 2 && r.ClippedSamples == 1 && r.Discontinuities == 1 && r.TimestampErrors == 1);
            TestSuite.Assert(GateAudioEvaluator.Compare(Good(-40), r, Good(-40)).Outcome == AudioValidationOutcome.Inconclusive);
        });
        suite.Case("Capture parses IEEE float extensible format", () => {
            var data = Format(0xfffe, 32, 40); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(16), 22);
            new Guid("00000003-0000-0010-8000-00AA00389B71").TryWriteBytes(data.AsSpan(24));
            TestSuite.Assert(CaptureFormat.Parse(data) == new CaptureFormat(2, 48000, 32, true));
        });
        suite.Case("Capture rejects unknown subformats and invalid alignment", () => {
            TestSuite.Reject(() => CaptureFormat.Parse(Format(0xfffe, 32, 18)));
            var data = Format(3, 32, 18); data[12] = 1; TestSuite.Reject(() => CaptureFormat.Parse(data));
            TestSuite.Reject(() => CaptureFormat.Parse(Format(6, 16, 18)));
        });
        suite.Case("Gate audio comparison requires attenuation and returning signal", () => {
            TestSuite.Assert(GateAudioEvaluator.Compare(Good(-40), Good(-65), Good(-41)).Outcome == AudioValidationOutcome.Passed);
            TestSuite.Assert(GateAudioEvaluator.Compare(Good(-40), Good(-41), Good(-40)).Outcome == AudioValidationOutcome.Failed);
        });
        suite.Case("Gate audio comparison refuses silence and unstable input", () => {
            TestSuite.Assert(GateAudioEvaluator.Compare(Good(-100), Good(-160), Good(-100)).Outcome == AudioValidationOutcome.Inconclusive);
            TestSuite.Assert(GateAudioEvaluator.Compare(Good(-40), Good(-100), Good(-60)).Outcome == AudioValidationOutcome.Inconclusive);
        });
        suite.Case("Gate audio comparison rejects clipped or interrupted windows", () => {
            foreach (var m in new[] { Good(-40) with { ClippedSamples = 1 }, Good(-40) with { Discontinuities = 1 }, Good(-40) with { TimestampErrors = 1 }, Good(-40) with { Seconds = .5 } })
                TestSuite.Assert(GateAudioEvaluator.Compare(m, Good(-100), Good(-40)).Outcome == AudioValidationOutcome.Inconclusive);
        });
        suite.Case("Live audio transaction restores full settings after passing", () => {
            var f = new Fixture(); var result = f.Run();
            TestSuite.Assert(result.Outcome == AudioValidationOutcome.Passed && result.SettingsRestored && result.Phases.Count == 3);
            TestSuite.Assert(f.Effects.Read(f.Endpoint) == f.Before && f.Audio.Applies == 0);
        });
        suite.Case("GSX audio diagnostic compares off maximum off and restores complete microphone settings", () => {
            var f = new Fixture("0098"); var observed = new List<MicrophoneEffects>();
            f.Source.During = () => observed.Add(f.Effects.Read(f.Endpoint));
            var result = f.Run();
            TestSuite.Assert(result.Outcome == AudioValidationOutcome.Passed && result.SettingsRestored && f.Audio.Applies == 0);
            TestSuite.Assert(observed.Count == 3 && !observed[0].Processing!.GateEnabled && observed[1].GatePercent == 100 && observed[1].Processing!.GateEnabled && !observed[2].Processing!.GateEnabled);
            TestSuite.Assert(f.Effects.Read(f.Endpoint) == f.Before);
        });
        suite.Case("GSX audio diagnostic reports unchanged audio as a failed measurement while restoring settings", () => {
            var f = new Fixture("0098"); f.Source.GatedDb = -40;
            var result = f.Run();
            TestSuite.Assert(result.Outcome == AudioValidationOutcome.Failed && result.Comparison!.AttenuationDb == 0 && result.SettingsRestored);
            TestSuite.Assert(f.Effects.Read(f.Endpoint) == f.Before && f.Audio.Applies == 0);
        });
        suite.Case("Live audio transaction restores full settings after capture failure", () => {
            var f = new Fixture(); f.Source.FailOn = 2; var result = f.Run();
            TestSuite.Assert(result.Outcome == AudioValidationOutcome.Failed && result.SettingsRestored && f.Effects.Read(f.Endpoint) == f.Before);
        });
        suite.Case("Live audio transaction preserves concurrent processing edits", () => {
            var f = new Fixture(); var external = f.Before with { FilterLevel = 1 };
            f.Source.During = () => f.Effects.Apply(f.Endpoint, f.Effects.Read(f.Endpoint), external);
            var result = f.Run(); TestSuite.Assert(!result.SettingsRestored && result.Error is not null && f.Effects.Read(f.Endpoint) == external);
        });
        suite.Case("Live audio transaction preserves newer level and restores owned processing", () => {
            var f = new Fixture(); f.Source.During = () => f.Audio.State = f.Audio.State with { Level = .6f };
            var result = f.Run(); TestSuite.Assert(result.Outcome == AudioValidationOutcome.Failed && result.SettingsRestored && f.Audio.State.Level == .6f && f.Audio.Applies == 0);
        });
        suite.Case("Live audio transaction refuses muted input before writing", () => {
            var f = new Fixture(); f.Audio.State = f.Audio.State with { Muted = true };
            TestSuite.Reject(() => f.Run()); TestSuite.Assert(f.Effects.Read(f.Endpoint) == f.Before && f.Source.Calls == 0);
        });
    }
    private static AudioMeasurement Good(double db) => new(96000, 2, db, db + 5, 0, 0, 0, 0);
    private static byte[] Floats(params float[] values)
    {
        var data = new byte[values.Length * 4]; for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4), values[i]); return data;
    }
    private static byte[] Format(ushort tag, ushort bits, int length)
    {
        var data = new byte[length]; BinaryPrimitives.WriteUInt16LittleEndian(data, tag); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 2);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 48000); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), (ushort)(2 * bits / 8)); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), bits); return data;
    }
    private sealed class Fixture
    {
        internal AudioEndpoint Endpoint = new("test", "B20", "EPOS", AudioDirection.Microphone, new("1395", "009F", "USB-test"), null, null, null);
        internal DemoMicrophoneEffectsBackend Effects = new(); internal FakeAudio Audio = new(); internal Source Source = new();
        internal MicrophoneEffects Before;
        internal Fixture(string productId = "009F") {
            Endpoint = Endpoint with { Usb = Endpoint.Usb! with { ProductId = productId } };
            Before = Effects.Read(Endpoint);
        }
        internal GateAudioReport Run() => GateAudioValidation.Run(Endpoint, Audio, Effects, Source, TimeSpan.FromSeconds(2));
    }
    private sealed class FakeAudio : IAudioBackend
    {
        internal AudioState State = new(.4f, false, -10, 0); internal int Applies;
        public IReadOnlyList<AudioEndpoint> Discover() => [];
        public AudioState Read(string id) => State;
        public AudioState Apply(string id, float level, bool mute) { Applies++; return State = State with { Level = level, Muted = mute }; }
    }
    private sealed class Source : IAudioMeasurementSource
    {
        internal int Calls, FailOn; internal Action? During; internal double GatedDb = -100;
        public AudioMeasurement Measure(TimeSpan duration) { Calls++; if (Calls == FailOn) throw new IOException("Capture lost."); During?.Invoke(); return Good(Calls == 2 ? GatedDb : -40); }
    }
}
