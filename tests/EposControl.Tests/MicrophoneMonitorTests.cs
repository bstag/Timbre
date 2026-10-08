using System.Buffers.Binary;
using EposControl.Core;

internal static class MicrophoneMonitorTests
{
    private static byte[] Tone(CaptureFormat format, int frames, double frequency, double amplitude = .5, bool opposite = false)
    {
        var data = new byte[frames * format.BlockAlign];
        for (var frame = 0; frame < frames; frame++) for (var channel = 0; channel < format.Channels; channel++) {
            var value = amplitude * Math.Sin(2 * Math.PI * frequency * frame / format.SampleRate) * (opposite && channel % 2 == 1 ? -1 : 1);
            var span = data.AsSpan(frame * format.BlockAlign + channel * format.Bits / 8, format.Bits / 8);
            if (format.FloatingPoint) BinaryPrimitives.WriteSingleLittleEndian(span, (float)value);
            else if (format.Bits == 16) BinaryPrimitives.WriteInt16LittleEndian(span, (short)(value * 32767));
            else if (format.Bits == 32) BinaryPrimitives.WriteInt32LittleEndian(span, (int)(value * 2147483647));
            else { var integer = (int)(value * 8388607); span[0] = (byte)integer; span[1] = (byte)(integer >> 8); span[2] = (byte)(integer >> 16); }
        }
        return data;
    }
    private static MicrophoneMonitorFrame Analyze(CaptureFormat format, double hz, double amplitude = .5, bool opposite = false)
    {
        var analyzer = new MicrophoneSpectrum(format); analyzer.AddPacket(Tone(format, analyzer.WindowSize, hz, amplitude, opposite), analyzer.WindowSize, 0); return analyzer.Snapshot()!;
    }
    public static void Run(TestSuite suite)
    {
        suite.Case("Microphone spectrum waits for a full window before showing live data", () => {
            var analyzer = new MicrophoneSpectrum(new(1, 48000, 32, true));
            TestSuite.Assert(analyzer.Snapshot() is null);
            analyzer.AddPacket([], analyzer.WindowSize - 1, 2); TestSuite.Assert(analyzer.Snapshot() is null);
            analyzer.AddPacket([], 1, 2); TestSuite.Assert(analyzer.Snapshot() is not null);
        });
        foreach (var (frequency, band) in new[] { (64d, 0), (125d, 1), (1000d, 4), (16000d, 8) })
            suite.Case($"Captured {frequency} Hz tone appears in its matching frequency band", () => {
                var frame = Analyze(new(1, 48000, 32, true), frequency);
                var maximum = frame.BandRmsDbFs.Select((v, i) => (Value: v ?? -120, Index: i)).MaxBy(v => v.Value);
                TestSuite.Assert(maximum.Index == band && Math.Abs(frame.RmsDbFs + 9.0309) < .15 && Math.Abs(frame.BandRmsDbFs[band]!.Value - frame.RmsDbFs) < .4);
                // At 16 kHz / 48 kHz there are three samples per cycle; the sampled peak is A*sqrt(3)/2.
                var expectedPeak = frequency == 16000 ? -7.269987 : -6.0206;
                TestSuite.Assert(Math.Abs(frame.PeakDbFs - expectedPeak) < .1);
            });
        suite.Case("Spectrum combines stereo power without canceling opposite-phase channels", () => {
            var mono = Analyze(new(1, 48000, 32, true), 1000); var stereo = Analyze(new(2, 48000, 32, true), 1000, opposite: true);
            TestSuite.Assert(Math.Abs(mono.RmsDbFs - stereo.RmsDbFs) < 1e-6 && Math.Abs(mono.BandRmsDbFs[4]!.Value - stereo.BandRmsDbFs[4]!.Value) < 1e-6);
        });
        foreach (var bits in new[] { 16, 24, 32 }) suite.Case($"Live spectrum supports PCM {bits}-bit capture", () => {
            var frame = Analyze(new(2, 48000, bits, false), 1000);
            TestSuite.Assert(Math.Abs(frame.RmsDbFs + 9.0309) < .1 && Math.Abs(frame.BandRmsDbFs[4]!.Value + 9.0309) < .1);
        });
        suite.Case("Silent capture packets clear activity and do not read their data", () => {
            var analyzer = new MicrophoneSpectrum(new(2, 48000, 32, true));
            analyzer.AddPacket([255], analyzer.WindowSize, 2); var frame = analyzer.Snapshot()!;
            TestSuite.Assert(frame.RmsDbFs == -120 && frame.PeakDbFs == -120 && frame.BandRmsDbFs.All(db => db == -120) && !frame.Clipping);
        });
        suite.Case("Low-rate capture marks bands beyond Nyquist as unavailable", () => {
            var frame = Analyze(new(1, 8000, 32, true), 1000);
            TestSuite.Assert(frame.BandRmsDbFs[6] is null && frame.BandRmsDbFs[7] is null && frame.BandRmsDbFs[8] is null && frame.BandRmsDbFs[4] > -10);
        });
        suite.Case("High-rate FFT windows preserve low-frequency resolution", () => {
            var frame = Analyze(new(1, 192000, 32, true), 64);
            TestSuite.Assert(frame.BandRmsDbFs[0] > -10 && frame.BandRmsDbFs.Skip(1).All(v => v < frame.BandRmsDbFs[0]));
        });
        suite.Case("Packet boundaries do not alter frequency measurements", () => {
            var format = new CaptureFormat(2, 48000, 32, true); var analyzer = new MicrophoneSpectrum(format);
            var data = Tone(format, analyzer.WindowSize, 1000); var split = 417;
            analyzer.AddPacket(data.AsSpan(0, split * format.BlockAlign), split, 0);
            analyzer.AddPacket(data.AsSpan(split * format.BlockAlign), analyzer.WindowSize - split, 0);
            var single = Analyze(format, 1000); var chunked = analyzer.Snapshot()!;
            TestSuite.Assert(Math.Abs(single.RmsDbFs - chunked.RmsDbFs) < 1e-8 && single.BandRmsDbFs.SequenceEqual(chunked.BandRmsDbFs));
        });
        suite.Case("Capture interruptions reset analysis instead of mixing old and new samples", () => {
            var analyzer = new MicrophoneSpectrum(new(1, 48000, 32, true)); analyzer.AddPacket([], analyzer.WindowSize, 2);
            analyzer.AddPacket([], 20, 3); TestSuite.Assert(analyzer.Snapshot() is null);
            analyzer.AddPacket([], analyzer.WindowSize - 20, 2); TestSuite.Assert(analyzer.Snapshot()!.Discontinuities == 1);
        });
        suite.Case("Initial capture discontinuity is distinguished from later stream interruptions", () => {
            var format = new CaptureFormat(1, 48000, 32, true); var analyzer = new MicrophoneSpectrum(format);
            analyzer.AddPacket(Tone(format, analyzer.WindowSize, 1000), analyzer.WindowSize, 1);
            TestSuite.Assert(analyzer.Snapshot() is { Discontinuities: 0, InitialDiscontinuities: 1, TimestampErrors: 0 });
        });
        suite.Case("Timestamp uncertainty does not discard valid spectrum samples or imply audio interruption", () => {
            var format = new CaptureFormat(1, 48000, 32, true); var analyzer = new MicrophoneSpectrum(format); var data = Tone(format, analyzer.WindowSize, 1000); var split = 500;
            analyzer.AddPacket(data.AsSpan(0, split * format.BlockAlign), split, 0);
            analyzer.AddPacket(data.AsSpan(split * format.BlockAlign), analyzer.WindowSize - split, 4);
            var frame = analyzer.Snapshot(); var reference = Analyze(format, 1000);
            TestSuite.Assert(frame is { Discontinuities: 0, InitialDiscontinuities: 0, TimestampErrors: 1 } && frame.BandRmsDbFs.SequenceEqual(reference.BandRmsDbFs));
        });
        suite.Case("Clearing analysis resets initial and later capture-quality counts independently", () => {
            var analyzer = new MicrophoneSpectrum(new(1, 48000, 32, true));
            analyzer.AddPacket([], 0, 7); analyzer.AddPacket([], analyzer.WindowSize, 7);
            TestSuite.Assert(analyzer.Snapshot() is { Discontinuities: 0, InitialDiscontinuities: 1, TimestampErrors: 1 });
            analyzer.AddPacket([], analyzer.WindowSize, 7);
            TestSuite.Assert(analyzer.Snapshot() is { Discontinuities: 1, InitialDiscontinuities: 1, TimestampErrors: 2 });
            analyzer.Clear(); analyzer.AddPacket([], analyzer.WindowSize, 2);
            TestSuite.Assert(analyzer.Snapshot() is { Discontinuities: 0, InitialDiscontinuities: 0, TimestampErrors: 0 });
        });
        suite.Case("Invalid samples are sanitized and flagged without breaking the analyzer", () => {
            var format = new CaptureFormat(1, 48000, 32, true); var analyzer = new MicrophoneSpectrum(format); var data = Tone(format, analyzer.WindowSize, 1000);
            BinaryPrimitives.WriteSingleLittleEndian(data, float.NaN); BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), float.PositiveInfinity);
            analyzer.AddPacket(data, analyzer.WindowSize, 0); var frame = analyzer.Snapshot()!;
            TestSuite.Assert(frame.InvalidSamples == 2 && double.IsFinite(frame.RmsDbFs) && frame.BandRmsDbFs.All(v => v is null || double.IsFinite(v.Value)));
        });
        suite.Case("Clipping reflects the current analysis window and clears after silence", () => {
            var format = new CaptureFormat(1, 48000, 32, true); var analyzer = new MicrophoneSpectrum(format);
            analyzer.AddPacket(Tone(format, analyzer.WindowSize, 1000, 1), analyzer.WindowSize, 0); TestSuite.Assert(analyzer.Snapshot()!.Clipping);
            analyzer.AddPacket([], analyzer.WindowSize, 2); TestSuite.Assert(!analyzer.Snapshot()!.Clipping);
        });
        suite.Case("Live analyzer rejects malformed oversized and unsupported packets", () => {
            var analyzer = new MicrophoneSpectrum(new(2, 48000, 32, true));
            TestSuite.Reject(() => analyzer.AddPacket([0], 1, 0)); TestSuite.Reject(() => analyzer.AddPacket([], -1, 2)); TestSuite.Reject(() => analyzer.AddPacket([], 48001, 2));
            TestSuite.Reject(() => new MicrophoneSpectrum(new(1, 48000, 8, false)));
        });
        suite.Case("Clearing the spectrum erases buffered samples and requires new audio", () => {
            var analyzer = new MicrophoneSpectrum(new(1, 48000, 32, true)); analyzer.AddPacket([], analyzer.WindowSize, 2); TestSuite.Assert(analyzer.Snapshot() is not null);
            analyzer.Clear(); TestSuite.Assert(analyzer.Snapshot() is null);
        });
        suite.Case("Gate display guide matches the recovered Suite geometry without changing control values", () => {
            TestSuite.Assert(MicrophoneGateGuide.SuiteScaleDb(0) == -60 && MicrophoneGateGuide.SuiteScaleDb(50) == -30 && MicrophoneGateGuide.SuiteScaleDb(100) == 0);
            TestSuite.Reject(() => MicrophoneGateGuide.SuiteScaleDb(float.NaN)); TestSuite.Reject(() => MicrophoneGateGuide.SuiteScaleDb(-1)); TestSuite.Reject(() => MicrophoneGateGuide.SuiteScaleDb(101));
        });
        suite.Case("Monitor worker owns source creation sampling and disposal on one thread", () => {
            PacketSource? source = null; using var monitor = new MicrophoneMonitor(() => source = new());
            Wait(() => monitor.Latest is not null || monitor.Error is not null);
            TestSuite.Assert(monitor.Error is null && monitor.Latest!.BandRmsDbFs[4] > -10);
            monitor.Dispose(); Wait(() => source?.Disposed == true);
            TestSuite.Assert(source!.Owner == source.LastReadThread && source.Owner == source.DisposeThread && source.Owner != Environment.CurrentManagedThreadId && monitor.Latest is null);
            if (OperatingSystem.IsWindows()) TestSuite.Assert(source.Apartment == ApartmentState.STA);
        });
        suite.Case("Monitor capture failure reports an error and releases its source without retry", () => {
            PacketSource? source = null; var opens = 0;
            using var monitor = new MicrophoneMonitor(() => { opens++; return source = new() { Fail = true }; });
            Wait(() => monitor.Error is not null);
            TestSuite.Assert(monitor.Error!.Contains("test capture failure") && source!.Disposed && opens == 1 && monitor.Latest is null);
        });
        suite.Case("A blocked capture poll does not block UI shutdown indefinitely", () => {
            using var entered = new ManualResetEvent(false); using var release = new ManualResetEvent(false);
            PacketSource? source = null; using var monitor = new MicrophoneMonitor(() => source = new() { Entered = entered, Release = release });
            TestSuite.Assert(entered.WaitOne(2000)); var watch = System.Diagnostics.Stopwatch.StartNew();
            try { monitor.Dispose(); TestSuite.Assert(watch.Elapsed < TimeSpan.FromSeconds(1) && monitor.Latest is null); }
            finally { release.Set(); Wait(() => source!.Disposed); }
        });
        suite.Case("Demo microphone analysis stops without exposing cached values", () => {
            using var monitor = new DemoMicrophoneMonitor(); TestSuite.Assert(monitor.Latest is not null);
            monitor.Dispose(); TestSuite.Assert(monitor.Latest is null && monitor.Error is null);
        });
    }
    private static void Wait(Func<bool> ready)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!ready() && started.Elapsed < TimeSpan.FromSeconds(3)) Thread.Sleep(10);
        TestSuite.Assert(ready(), "Timed out waiting for the monitor worker.");
    }
    private sealed class PacketSource : IMicrophonePacketSource
    {
        public CaptureFormat Format { get; } = new(1, 48000, 32, true);
        public int Owner { get; } = Environment.CurrentManagedThreadId;
        public ApartmentState Apartment { get; } = Thread.CurrentThread.GetApartmentState();
        public int LastReadThread, DisposeThread;
        public volatile bool Disposed;
        public bool Fail;
        public ManualResetEvent? Entered, Release;
        public int ReadPending(Action<ReadOnlyMemory<byte>, int, uint> consume)
        {
            LastReadThread = Environment.CurrentManagedThreadId;
            Entered?.Set(); Release?.WaitOne();
            if (Fail) throw new IOException("test capture failure");
            var analyzer = new MicrophoneSpectrum(Format); consume(Tone(Format, analyzer.WindowSize, 1000), analyzer.WindowSize, 0); return 1;
        }
        public void Dispose() { DisposeThread = Environment.CurrentManagedThreadId; Disposed = true; }
    }
}
