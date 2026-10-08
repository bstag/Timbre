using System.Buffers.Binary;

namespace Timbre.Core;

public sealed record CaptureFormat(int Channels, int SampleRate, int Bits, bool FloatingPoint)
{
    public int BlockAlign => checked(Channels * (Bits / 8));
    public void Validate()
    {
        if (Channels is < 1 or > 32 || SampleRate is < 8000 or > 384000 ||
            (FloatingPoint ? Bits != 32 : Bits is not (16 or 24 or 32)))
            throw new InvalidDataException("Unsupported capture format.");
    }
    public static CaptureFormat Parse(ReadOnlySpan<byte> waveFormat)
    {
        if (waveFormat.Length < 18) throw new InvalidDataException("Truncated WAVEFORMATEX.");
        var tag = BinaryPrimitives.ReadUInt16LittleEndian(waveFormat);
        if (tag == 0xfffe) {
            if (waveFormat.Length < 40 || BinaryPrimitives.ReadUInt16LittleEndian(waveFormat[16..]) < 22)
                throw new InvalidDataException("Truncated WAVEFORMATEXTENSIBLE.");
            var sub = new Guid(waveFormat.Slice(24, 16));
            tag = sub == new Guid("00000001-0000-0010-8000-00AA00389B71") ? (ushort)1 :
                sub == new Guid("00000003-0000-0010-8000-00AA00389B71") ? (ushort)3 : (ushort)0;
        }
        if (tag is not (1 or 3)) throw new InvalidDataException("Only PCM and IEEE float capture are supported.");
        var result = new CaptureFormat(BinaryPrimitives.ReadUInt16LittleEndian(waveFormat[2..]),
            BinaryPrimitives.ReadInt32LittleEndian(waveFormat[4..]), BinaryPrimitives.ReadUInt16LittleEndian(waveFormat[14..]), tag == 3);
        result.Validate();
        if (BinaryPrimitives.ReadUInt16LittleEndian(waveFormat[12..]) != result.BlockAlign)
            throw new InvalidDataException("Capture block alignment does not match its format.");
        return result;
    }
}

public sealed record AudioMeasurement(long Frames, double Seconds, double RmsDbFs, double PeakDbFs,
    long ClippedSamples, long InvalidSamples, int Discontinuities, int TimestampErrors);

// Samples are reduced to numbers immediately; no audio is stored in the report.
public sealed class AudioMeter
{
    private readonly CaptureFormat format;
    private long frames, clipped, invalid;
    private double squares, peak;
    private int discontinuities, timestampErrors;
    public AudioMeter(CaptureFormat format) { format.Validate(); this.format = format; }
    public void AddPacket(ReadOnlySpan<byte> data, int frameCount, uint flags)
    {
        if (frameCount < 0 || ((flags & 2) == 0 && data.Length != checked(frameCount * format.BlockAlign)))
            throw new InvalidDataException("Malformed capture packet.");
        if ((flags & 1) != 0) discontinuities++;
        if ((flags & 4) != 0) timestampErrors++;
        frames += frameCount;
        if ((flags & 2) != 0) return; // WASAPI's SILENT flag takes precedence over its data pointer.
        var bytes = format.Bits / 8;
        for (var offset = 0; offset < data.Length; offset += bytes) {
            var sample = data.Slice(offset, bytes);
            double value = format.FloatingPoint ? BinaryPrimitives.ReadSingleLittleEndian(sample) : format.Bits switch {
                16 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768d,
                24 => ((sample[0] | sample[1] << 8 | sample[2] << 16) << 8 >> 8) / 8388608d,
                32 => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648d,
                _ => throw new InvalidDataException()
            };
            if (!double.IsFinite(value)) { invalid++; continue; }
            var magnitude = Math.Abs(value);
            if (magnitude >= .999) clipped++;
            squares += value * value;
            peak = Math.Max(peak, magnitude);
        }
    }
    public AudioMeasurement Result() => new(frames, frames / (double)format.SampleRate,
        Db(frames == 0 ? 0 : Math.Sqrt(squares / (frames * format.Channels))), Db(peak), clipped, invalid, discontinuities, timestampErrors);
    private static double Db(double level) => 20 * Math.Log10(Math.Max(level, 1e-8));
}

public interface IAudioMeasurementSource
{
    AudioMeasurement Measure(TimeSpan duration);
}

public enum AudioValidationOutcome { Passed, Failed, Inconclusive }
public sealed record GateAudioComparison(AudioValidationOutcome Outcome, string Reason, double AttenuationDb);
public static class GateAudioEvaluator
{
    public static GateAudioComparison Compare(AudioMeasurement before, AudioMeasurement gated, AudioMeasurement after)
    {
        var attenuation = Math.Min(before.RmsDbFs, after.RmsDbFs) - gated.RmsDbFs;
        GateAudioComparison Result(AudioValidationOutcome outcome, string reason) => new(outcome, reason, attenuation);
        if (new[] { before, gated, after }.Any(m => m.Seconds < 1 || m.Frames <= 0 || !double.IsFinite(m.RmsDbFs) ||
            !double.IsFinite(m.PeakDbFs) || m.InvalidSamples > 0 || m.ClippedSamples > 0 || m.Discontinuities > 0 || m.TimestampErrors > 0))
            return Result(AudioValidationOutcome.Inconclusive, "Capture was short, clipped, invalid, or interrupted. Repeat with steady quiet input.");
        if (Math.Min(before.RmsDbFs, after.RmsDbFs) < -70)
            return Result(AudioValidationOutcome.Inconclusive, "Ungated input is too quiet to distinguish gating from silence.");
        if (Math.Abs(before.RmsDbFs - after.RmsDbFs) > 6)
            return Result(AudioValidationOutcome.Inconclusive, "Input changed between the two ungated windows. Repeat with steady input.");
        return attenuation >= 12 ? Result(AudioValidationOutcome.Passed, "Maximum gate reduced captured RMS by at least 12 dB and audio returned afterward.") :
            Result(AudioValidationOutcome.Failed, "Maximum gate did not reduce captured RMS by 12 dB. Check that EPOS processing is active in this Windows audio path.");
    }
}
