using Timbre.Core;

internal static class PlaybackEqAudioTests
{
    private static AudioMeasurement Good(double db) => new(96000, 2, db, db + 3, 0, 0, 0, 0);
    public static void Run(TestSuite suite)
    {
        suite.Case("Playback EQ measurement requires reversible expected gain", () => {
            var result = PlaybackEqAudioEvaluator.Compare(Good(-50), Good(-44), Good(-50.2));
            TestSuite.Assert(result.Outcome == AudioValidationOutcome.Passed && Math.Abs(result.GainDb - 6.1) < .001);
            TestSuite.Assert(PlaybackEqAudioEvaluator.Compare(Good(-50), Good(-44), Good(-48)).Outcome == AudioValidationOutcome.Inconclusive);
        });
        suite.Case("No EQ response in an unverified loopback path is inconclusive", () => {
            foreach (var boosted in new[] { -50d, -60, -38 })
                TestSuite.Assert(PlaybackEqAudioEvaluator.Compare(Good(-50), Good(boosted), Good(-50)).Outcome == AudioValidationOutcome.Inconclusive);
        });
        suite.Case("Playback EQ evaluator rejects silent invalid and interrupted evidence", () => {
            foreach (var bad in new[] { Good(-100), Good(double.NaN), Good(-50) with { PeakDbFs = double.PositiveInfinity },
                Good(-50) with { Seconds = .5 }, Good(-50) with { Frames = 0 }, Good(-50) with { InvalidSamples = 1 },
                Good(-50) with { ClippedSamples = 1 }, Good(-50) with { Discontinuities = 1 }, Good(-50) with { TimestampErrors = 1 } }) {
                TestSuite.Assert(PlaybackEqAudioEvaluator.Compare(bad, Good(-44), Good(-50)).Outcome == AudioValidationOutcome.Inconclusive);
                TestSuite.Assert(PlaybackEqAudioEvaluator.Compare(Good(-50), bad, Good(-50)).Outcome == AudioValidationOutcome.Inconclusive);
            }
        });
        suite.Case("Playback EQ transaction preserves mode and restores curve and reverb exactly", () => {
            var f = new Fixture(); var observed = new List<PlaybackEffects>();
            f.Source.During = () => observed.Add(f.Playback.Read(f.Endpoint));
            var report = f.Run();
            TestSuite.Assert(report.Outcome == AudioValidationOutcome.Passed && report.SettingsRestored && f.Playback.Read(f.Endpoint) == f.Before);
            TestSuite.Assert(observed.Count == 3 && observed.All(s => s.SurroundEnabled && !s.Reverb!.Enabled && s.Reverb.Level == .37f));
            TestSuite.Assert(observed[0].Equalizer == PlaybackEqualizer.Flat && observed[1].Equalizer == PlaybackEqualizer.Flat with { Band5 = 6 } && observed[2].Equalizer == PlaybackEqualizer.Flat);
            TestSuite.Assert(f.Audio.Applies == 0);
        });
        suite.Case("Inconclusive EQ measurement still restores all starting settings", () => {
            var f = new Fixture(); f.Source.BoostDb = -50; var report = f.Run();
            TestSuite.Assert(report.Outcome == AudioValidationOutcome.Inconclusive && report.SettingsRestored && f.Playback.Read(f.Endpoint) == f.Before);
        });
        suite.Case("Playback capture failure restores the owned settings", () => {
            var f = new Fixture(); f.Source.FailOn = 2; var report = f.Run();
            TestSuite.Assert(report.Outcome == AudioValidationOutcome.Failed && report.SettingsRestored && f.Playback.Read(f.Endpoint) == f.Before);
        });
        suite.Case("Playback EQ measurement preserves concurrent external processing edits", () => {
            var f = new Fixture(); var external = f.Before with { Equalizer = PlaybackEqualizer.Flat with { Band9 = -4 } };
            f.Source.During = () => f.Playback.Apply(f.Endpoint, f.Playback.Read(f.Endpoint), external);
            var report = f.Run();
            TestSuite.Assert(report.Outcome == AudioValidationOutcome.Failed && !report.SettingsRestored && f.Playback.Read(f.Endpoint) == external);
        });
        suite.Case("Playback EQ measurement preserves newer volume while restoring its curve", () => {
            var f = new Fixture(); f.Source.During = () => f.Audio.State = f.Audio.State with { Level = .7f };
            var report = f.Run();
            TestSuite.Assert(report.Outcome == AudioValidationOutcome.Failed && report.SettingsRestored && f.Audio.State.Level == .7f && f.Audio.Applies == 0);
        });
        suite.Case("Muted or missing playback refuses measurement before any writes", () => {
            var muted = new Fixture(); muted.Audio.State = muted.Audio.State with { Muted = true };
            TestSuite.Reject(() => muted.Run()); TestSuite.Assert(muted.Source.Calls == 0 && muted.Playback.Read(muted.Endpoint) == muted.Before);
            var missing = new Fixture(); missing.Audio.Missing = true;
            TestSuite.Reject(() => missing.Run()); TestSuite.Assert(missing.Source.Calls == 0 && missing.Playback.Read(missing.Endpoint) == missing.Before);
        });
        suite.Case("Playback EQ restore cannot overwrite a replacement device", () => {
            var f = new Fixture(); f.Source.During = () => f.Audio.Missing = true;
            var report = f.Run();
            TestSuite.Assert(report.Outcome == AudioValidationOutcome.Failed && !report.SettingsRestored && f.Audio.Applies == 0);
        });
    }
    private sealed class Fixture
    {
        internal AudioEndpoint Endpoint = new("gsx-sound", "GSX", "GSX", AudioDirection.Playback, new("1395", "0098", "USB-test"), null, null, null);
        internal FakeAudio Audio = new(); internal DemoPlaybackEffectsBackend Playback = new(); internal Source Source = new();
        internal PlaybackEffects Before;
        internal Fixture() {
            Audio.Endpoint = Endpoint;
            Before = new(true, new(1, -1, 2, -2, 3, -3, 4, -4, 5), new(true, .37f));
            Playback.Apply(Endpoint, Playback.Read(Endpoint), Before);
        }
        internal PlaybackEqAudioReport Run() => PlaybackEqAudioValidation.Run(Endpoint, Audio, Playback, Source, TimeSpan.FromSeconds(2));
    }
    private sealed class FakeAudio : IAudioBackend
    {
        internal AudioEndpoint Endpoint = null!; internal bool Missing; internal int Applies;
        internal AudioState State = new(.4f, false, -10, 0);
        public IReadOnlyList<AudioEndpoint> Discover() => Missing ? [] : [Endpoint];
        public AudioState Read(string id) => State;
        public AudioState Apply(string id, float level, bool mute) { Applies++; return State = State with { Level = level, Muted = mute }; }
    }
    private sealed class Source : IAudioMeasurementSource
    {
        internal int Calls, FailOn; internal double BoostDb = -44; internal Action? During;
        public AudioMeasurement Measure(TimeSpan duration) {
            Calls++; if (Calls == FailOn) throw new IOException("Capture lost."); During?.Invoke(); return Good(Calls == 2 ? BoostDb : -50);
        }
    }
}
