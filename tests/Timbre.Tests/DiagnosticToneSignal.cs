using Timbre.Core;

// Diagnostic-only quiet tone; never a default-device or format-setting operation.
internal sealed class DiagnosticToneSignal
{
    internal const double PeakAmplitude = .005;
    internal const int FrequencyHz = 1000;
    private readonly CaptureFormat format;
    private long position;

    internal DiagnosticToneSignal(CaptureFormat format)
    {
        format.Validate();
        if (!format.FloatingPoint || format.Bits != 32 || format.Channels is not (2 or 8))
            throw new InvalidDataException("Quiet GSX probe requires stereo or eight-channel float32 output");
        this.format = format;
    }

    internal float[] Fill(int frames)
    {
        if (frames is < 1 || frames > format.SampleRate) throw new ArgumentOutOfRangeException(nameof(frames));
        var samples = new float[checked(frames * format.Channels)];
        for (var i = 0; i < frames; i++, position++) {
            var sample = (float)(PeakAmplitude * Math.Sin(2 * Math.PI * FrequencyHz * position / format.SampleRate));
            // Shared-mode 7.1 keeps the original quiet level on front left/right.
            // Other channels, including LFE, remain silent.
            samples[i * format.Channels] = sample;
            samples[i * format.Channels + 1] = sample;
        }
        return samples;
    }
}
