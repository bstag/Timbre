using System.Buffers.Binary;
using System.Text.Json.Serialization;

namespace Timbre.Core;

public sealed record PlaybackReverb([property: JsonRequired] bool Enabled, [property: JsonRequired] float Level)
{
    public void Validate()
    {
        if (!float.IsFinite(Level) || Level is < 0 or > 1) throw new InvalidDataException("Reverb amount must be between 0 and 1.");
    }
}

public sealed record PlaybackEffects([property: JsonRequired] bool SurroundEnabled, PlaybackEqualizer? Equalizer = null, PlaybackReverb? Reverb = null)
{
    public void Validate() { Equalizer?.Validate(); Reverb?.Validate(); }
    public static void ValidateUpdate(PlaybackEffects expected, PlaybackEffects desired)
    {
        expected.Validate(); desired.Validate();
        if (desired.Equalizer is not null && expected.Equalizer is null)
            throw new InvalidOperationException("Reload the playback EQ before applying a new curve.");
        if (desired.Reverb is not null && expected.Reverb is null)
            throw new InvalidOperationException("Reload reverb before applying its settings.");
    }
}

public interface IPlaybackEffectsBackend
{
    PlaybackEffects Read(AudioEndpoint endpoint);
    PlaybackEffects Apply(AudioEndpoint endpoint, PlaybackEffects expected, PlaybackEffects desired);
}

public static class ApoPlaybackCodec
{
    public const int SurroundOffset = 64, EqualizerOffset = 76, ReverbEnabledOffset = 69, ReverbLevelOffset = 148;
    public static PlaybackEffects Read(ReadOnlySpan<byte> memory)
    {
        if (memory.Length != ApoMicrophoneCodec.MemorySize || BinaryPrimitives.ReadInt32LittleEndian(memory) != 2 ||
            BinaryPrimitives.ReadInt32LittleEndian(memory[4..]) != 4)
            throw new InvalidDataException("Unsupported EPOS playback memory layout; expected 4096 bytes and header 2, 4.");
        var mode = memory[SurroundOffset] switch { 0 => false, 1 => true, _ => throw new InvalidDataException("Invalid EPOS surround switch.") };
        var levels = new float[9];
        for (var i = 0; i < 9; i++) levels[i] = BinaryPrimitives.ReadSingleLittleEndian(memory[(EqualizerOffset + i * 4)..]);
        var reverbEnabled = memory[ReverbEnabledOffset] switch { 0 => false, 1 => true, _ => throw new InvalidDataException("Invalid EPOS reverb switch.") };
        var reverb = new PlaybackReverb(reverbEnabled, BinaryPrimitives.ReadSingleLittleEndian(memory[ReverbLevelOffset..]));
        reverb.Validate();
        return new(mode, PlaybackEqualizer.FromLevels(levels), reverb);
    }
    public static IReadOnlyList<EffectMemoryPatch> Prepare(ReadOnlySpan<byte> memory, PlaybackEffects desired)
    {
        var current = Read(memory); desired.Validate();
        var patches = new List<EffectMemoryPatch>();
        if (current.SurroundEnabled != desired.SurroundEnabled) patches.Add(new(SurroundOffset, [memory[SurroundOffset]], [(byte)(desired.SurroundEnabled ? 1 : 0)]));
        if (desired.Equalizer is not null) {
            var levels = desired.Equalizer.Levels();
            for (var i = 0; i < 9; i++) {
                var offset = EqualizerOffset + i * 4; var after = new byte[4];
                BinaryPrimitives.WriteSingleLittleEndian(after, levels[i]);
                var before = memory.Slice(offset, 4).ToArray();
                if (!before.AsSpan().SequenceEqual(after)) patches.Add(new(offset, before, after));
            }
        }
        if (desired.Reverb is { } reverb) {
            if (current.Reverb!.Enabled != reverb.Enabled)
                patches.Add(new(ReverbEnabledOffset, [memory[ReverbEnabledOffset]], [(byte)(reverb.Enabled ? 1 : 0)]));
            var after = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(after, reverb.Level);
            var before = memory.Slice(ReverbLevelOffset, 4).ToArray();
            if (!before.AsSpan().SequenceEqual(after)) patches.Add(new(ReverbLevelOffset, before, after));
        }
        return patches;
    }
    public static bool Matches(PlaybackEffects actual, PlaybackEffects expected)
    {
        if (actual.SurroundEnabled != expected.SurroundEnabled) return false;
        if (expected.Reverb is { } reverb && (actual.Reverb is not { } current || current.Enabled != reverb.Enabled ||
            !float.IsFinite(current.Level) || !float.IsFinite(reverb.Level) || Math.Abs(current.Level - reverb.Level) >= .0001f)) return false;
        return expected.Equalizer is null || actual.Equalizer is { } eq &&
            eq.Levels().Zip(expected.Equalizer.Levels()).All(p => float.IsFinite(p.First) && float.IsFinite(p.Second) && Math.Abs(p.First - p.Second) < .0001f);
    }
    public static void ValidatePatch(int offset, int length)
    {
        if (!(offset == SurroundOffset && length == 1 || offset == ReverbEnabledOffset && length == 1 || offset == ReverbLevelOffset && length == 4 ||
            length == 4 && offset >= EqualizerOffset && offset < EqualizerOffset + 36 && (offset - EqualizerOffset) % 4 == 0))
            throw new InvalidOperationException("Only validated GSX 300 surround, playback EQ and reverb fields may be written.");
    }
    public static void ValidateEndpoint(AudioEndpoint endpoint)
    {
        if (endpoint.Direction != AudioDirection.Playback || endpoint.Usb is not { } usb ||
            !usb.VendorId.Equals("1395", StringComparison.OrdinalIgnoreCase) ||
            !usb.ProductId.Equals("0098", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(usb.InstanceId))
            throw new InvalidOperationException("Playback processing is currently validated for GSX 300 sound output only.");
    }
}

public sealed class ApoPlaybackEffectsBackend(Func<AudioEndpoint, IEffectMemorySession> open) : IPlaybackEffectsBackend
{
    public PlaybackEffects Read(AudioEndpoint endpoint)
    {
        ApoPlaybackCodec.ValidateEndpoint(endpoint);
        using var session = open(endpoint);
        return ApoPlaybackCodec.Read(session.Read());
    }
    public PlaybackEffects Apply(AudioEndpoint endpoint, PlaybackEffects expected, PlaybackEffects desired)
    {
        ApoPlaybackCodec.ValidateEndpoint(endpoint);
        PlaybackEffects.ValidateUpdate(expected, desired);
        using var session = open(endpoint);
        var bytes = session.Read();
        if (!ApoPlaybackCodec.Matches(ApoPlaybackCodec.Read(bytes), expected)) throw new InvalidOperationException("Sound settings changed. Reload before applying.");
        var patches = ApoPlaybackCodec.Prepare(bytes, desired);
        if (patches.Count == 0) return ApoPlaybackCodec.Read(bytes);
        var attempted = new List<EffectMemoryPatch>();
        try {
            foreach (var patch in patches) { attempted.Add(patch); session.Write(patch.Offset, patch.After); }
            session.Notify();
            var result = ApoPlaybackCodec.Read(session.Read());
            if (!ApoPlaybackCodec.Matches(result, desired)) throw new IOException("EPOS sound settings readback did not match the requested settings.");
            return result;
        } catch (Exception failure) {
            try {
                var current = session.Read();
                ApoPlaybackCodec.Read(current);
                foreach (var patch in attempted) {
                    var value = current.AsSpan(patch.Offset, patch.Before.Length);
                    if (!value.SequenceEqual(patch.Before) && !value.SequenceEqual(patch.After)) throw new IOException("Sound settings changed during failure; restoration cancelled to preserve the newer state.");
                }
                foreach (var patch in attempted) session.Write(patch.Offset, patch.Before);
                session.Notify();
                var restored = session.Read();
                foreach (var patch in attempted) if (!restored.AsSpan(patch.Offset, patch.Before.Length).SequenceEqual(patch.Before)) throw new IOException("Sound settings restoration could not be verified.");
            } catch (Exception restoration) {
                throw new AggregateException("Sound mode update and restoration failed. Reload current settings.", failure, restoration);
            }
            throw;
        }
    }
}

public sealed class DemoPlaybackEffectsBackend : IPlaybackEffectsBackend
{
    private readonly Dictionary<string, PlaybackEffects> states = new(StringComparer.OrdinalIgnoreCase);
    public PlaybackEffects Read(AudioEndpoint endpoint)
    {
        ApoPlaybackCodec.ValidateEndpoint(endpoint);
        return states.GetValueOrDefault(endpoint.Usb!.InstanceId) ?? new(false, PlaybackEqualizer.Flat, new(false, 0));
    }
    public PlaybackEffects Apply(AudioEndpoint endpoint, PlaybackEffects expected, PlaybackEffects desired)
    {
        PlaybackEffects.ValidateUpdate(expected, desired);
        var current = Read(endpoint);
        if (!ApoPlaybackCodec.Matches(current, expected)) throw new InvalidOperationException("Sound settings changed. Reload before applying.");
        states[endpoint.Usb!.InstanceId] = desired with { Equalizer = desired.Equalizer ?? current.Equalizer, Reverb = desired.Reverb ?? current.Reverb }; return Read(endpoint);
    }
}
