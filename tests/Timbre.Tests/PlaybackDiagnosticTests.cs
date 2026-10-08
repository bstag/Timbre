using Timbre.Core;

internal static class PlaybackDiagnosticTests
{
    public static void Run(TestSuite suite)
    {
        suite.Case("Quiet playback tone retains stereo amplitude and frequency", () => {
            var samples = new DiagnosticToneSignal(new(2, 48000, 32, true)).Fill(48000);
            var left = samples.Where((_, i) => i % 2 == 0).ToArray();
            TestSuite.Assert(Math.Abs(left.Max() - DiagnosticToneSignal.PeakAmplitude) < 1e-8 &&
                Math.Abs(Math.Sqrt(left.Sum(s => (double)s * s) / left.Length) - .005 / Math.Sqrt(2)) < 1e-8);
            TestSuite.Assert(left.Where((s, i) => i > 0 && s >= 0 && left[i - 1] < 0).Count() == 999);
            for (var i = 0; i < samples.Length; i += 2) TestSuite.Assert(samples[i] == samples[i + 1]);
        });
        suite.Case("GSX eight-channel loopback tone uses front pair and keeps other channels silent", () => {
            var samples = new DiagnosticToneSignal(new(8, 48000, 32, true)).Fill(4800);
            TestSuite.Assert(samples.Length == 4800 * 8 && samples.Any(s => s != 0));
            for (var i = 0; i < samples.Length; i += 8) {
                TestSuite.Assert(samples[i] == samples[i + 1]);
                for (var channel = 2; channel < 8; channel++) TestSuite.Assert(samples[i + channel] == 0);
            }
        });
        suite.Case("Playback tone stays phase-continuous across native render buffers", () => {
            foreach (var channels in new[] { 2, 8 }) {
                var format = new CaptureFormat(channels, 48000, 32, true);
                var split = new DiagnosticToneSignal(format);
                TestSuite.Assert(split.Fill(137).Concat(split.Fill(863)).SequenceEqual(new DiagnosticToneSignal(format).Fill(1000)));
            }
        });
        suite.Case("Playback diagnostic refuses unsupported formats and oversized buffers", () => {
            foreach (var format in new[] { new CaptureFormat(1, 48000, 32, true), new(6, 48000, 32, true), new(2, 48000, 16, false) })
                TestSuite.Throws<InvalidDataException>(() => new DiagnosticToneSignal(format));
            var signal = new DiagnosticToneSignal(new(2, 48000, 32, true));
            TestSuite.Throws<ArgumentOutOfRangeException>(() => signal.Fill(0));
            TestSuite.Throws<ArgumentOutOfRangeException>(() => signal.Fill(48001));
        });
    }
}
