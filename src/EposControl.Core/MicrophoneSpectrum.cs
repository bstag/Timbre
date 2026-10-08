using System.Buffers.Binary;
using System.Numerics;

namespace EposControl.Core;

public sealed record MicrophoneMonitorFrame(long Sequence, DateTimeOffset CapturedAt, double RmsDbFs, double PeakDbFs,
    IReadOnlyList<double?> BandRmsDbFs, bool Clipping, long InvalidSamples, int Discontinuities,
    int InitialDiscontinuities = 0, int TimestampErrors = 0);

// Octave-band activity from captured samples; no audio is saved or transformed for playback.
public sealed class MicrophoneSpectrum
{
    public static IReadOnlyList<double> Centers { get; } = Array.AsReadOnly(new double[] { 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 });
    public const double FloorDbFs = -120;
    private readonly CaptureFormat format;
    private readonly double[][] samples;
    private readonly double[] window;
    private readonly double windowEnergy;
    private int position, count, discontinuities, initialDiscontinuities, timestampErrors;
    private bool receivedPacket;
    private long sequence, invalid;
    public int WindowSize { get; }
    public MicrophoneSpectrum(CaptureFormat format)
    {
        format.Validate(); this.format = format;
        var size = 1024;
        while (size < format.SampleRate / 16) size *= 2;
        WindowSize = size;
        samples = Enumerable.Range(0, format.Channels).Select(_ => new double[size]).ToArray();
        window = Enumerable.Range(0, size).Select(i => .5 - .5 * Math.Cos(2 * Math.PI * i / size)).ToArray();
        windowEnergy = window.Sum(w => w * w);
    }
    public void AddPacket(ReadOnlySpan<byte> data, int frames, uint flags)
    {
        if (frames < 0 || frames > format.SampleRate || ((flags & 2) == 0 && data.Length != checked(frames * format.BlockAlign)))
            throw new InvalidDataException("Malformed microphone analysis packet.");
        if (frames == 0) return;
        if ((flags & 1) != 0) {
            count = position = 0;
            if (receivedPacket) discontinuities++; else initialDiscontinuities++;
        }
        // Timestamp uncertainty does not mean the samples themselves are discontinuous.
        if ((flags & 4) != 0) timestampErrors++;
        receivedPacket = true;
        var bytes = format.Bits / 8;
        for (var frame = 0; frame < frames; frame++) {
            for (var channel = 0; channel < format.Channels; channel++) {
                double value = 0;
                if ((flags & 2) == 0) {
                    var sample = data.Slice(frame * format.BlockAlign + channel * bytes, bytes);
                    value = format.FloatingPoint ? BinaryPrimitives.ReadSingleLittleEndian(sample) : format.Bits switch {
                        16 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768d,
                        24 => ((sample[0] | sample[1] << 8 | sample[2] << 16) << 8 >> 8) / 8388608d,
                        32 => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648d,
                        _ => throw new InvalidDataException()
                    };
                }
                if (!double.IsFinite(value)) { value = 0; invalid++; }
                samples[channel][position] = value;
            }
            position = (position + 1) % WindowSize; count = Math.Min(WindowSize, count + 1);
        }
    }
    public MicrophoneMonitorFrame? Snapshot()
    {
        if (count < WindowSize) return null;
        var power = new double[WindowSize / 2 + 1];
        double squares = 0, peak = 0;
        var fft = new Complex[WindowSize];
        foreach (var channel in samples) {
            for (var i = 0; i < WindowSize; i++) {
                var value = channel[(position + i) % WindowSize];
                squares += value * value; peak = Math.Max(peak, Math.Abs(value));
                fft[i] = new Complex(value * window[i], 0);
            }
            Transform(fft);
            for (var bin = 0; bin < power.Length; bin++) {
                var magnitudeSquared = fft[bin].Real * fft[bin].Real + fft[bin].Imaginary * fft[bin].Imaginary;
                power[bin] += magnitudeSquared * (bin == 0 || bin == WindowSize / 2 ? 1 : 2) / (WindowSize * windowEnergy * format.Channels);
            }
        }
        var bands = new double?[9];
        for (var band = 0; band < bands.Length; band++) {
            var center = Centers[band];
            if (center >= format.SampleRate / 2d) { bands[band] = null; continue; }
            var lower = band == 0 ? center / Math.Sqrt(2) : Math.Sqrt(Centers[band - 1] * center);
            var upper = band == 8 ? center * Math.Sqrt(2) : Math.Sqrt(center * Centers[band + 1]);
            double energy = 0;
            for (var bin = 1; bin < power.Length; bin++) {
                var hz = bin * (double)format.SampleRate / WindowSize;
                if (hz >= lower && hz < upper) energy += power[bin];
            }
            bands[band] = Db(Math.Sqrt(energy));
        }
        return new(++sequence, DateTimeOffset.UtcNow, Db(Math.Sqrt(squares / (WindowSize * format.Channels))), Db(peak),
            Array.AsReadOnly(bands), peak >= .999, invalid, discontinuities, initialDiscontinuities, timestampErrors);
    }
    public void Clear()
    {
        foreach (var channel in samples) Array.Clear(channel);
        count = position = discontinuities = initialDiscontinuities = timestampErrors = 0; invalid = 0; receivedPacket = false;
    }
    private static double Db(double amplitude) => Math.Max(FloorDbFs, 20 * Math.Log10(Math.Max(amplitude, 1e-6)));
    private static void Transform(Complex[] data)
    {
        for (int i = 1, j = 0; i < data.Length; i++) {
            var bit = data.Length >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }
        for (var length = 2; length <= data.Length; length <<= 1) {
            var step = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
            for (var start = 0; start < data.Length; start += length) {
                var twiddle = Complex.One;
                for (var i = 0; i < length / 2; i++) {
                    var a = data[start + i]; var b = data[start + i + length / 2] * twiddle;
                    data[start + i] = a + b; data[start + i + length / 2] = a - b; twiddle *= step;
                }
            }
        }
    }
}

public static class MicrophoneGateGuide
{
    // Recovered Suite geometry: knob/255 * graph height, on an FFT graph mapped from -60..0.
    // This is a UI guide only; it is not a measured DSP threshold or a gate-open decision.
    public static double SuiteScaleDb(float gatePercent)
    {
        if (!float.IsFinite(gatePercent) || gatePercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(gatePercent));
        return -60 + .6 * gatePercent;
    }
}
