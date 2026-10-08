using System.Buffers.Binary;

namespace EposControl.Core;

public sealed record EffectMemoryPatch(int Offset, byte[] Before, byte[] After);

public static class ApoMicrophoneCodec
{
    public const int MemorySize = 4096, GateOffset = 152, FilterOffset = 160;
    public const int EqualizerEnabledOffset = 66, FilterEnabledOffset = 67, GateEnabledOffset = 68, HighPassEnabledOffset = 71, EqualizerOffset = 112;
    public static MicrophoneEffects Read(ReadOnlySpan<byte> memory)
    {
        if (memory.Length != MemorySize || BinaryPrimitives.ReadInt32LittleEndian(memory) != 2 ||
            BinaryPrimitives.ReadInt32LittleEndian(memory[4..]) != 4)
            throw new InvalidDataException("Unsupported EPOS APO memory layout; expected 4096 bytes and header 2, 4.");
        var levels = new float[9];
        for (var i = 0; i < levels.Length; i++) levels[i] = BinaryPrimitives.ReadSingleLittleEndian(memory[(EqualizerOffset + i * 4)..]);
        var state = new MicrophoneEffects(BinaryPrimitives.ReadSingleLittleEndian(memory[GateOffset..]) * 100,
            BinaryPrimitives.ReadInt32LittleEndian(memory[FilterOffset..]), new(
                ReadBoolean(memory, FilterEnabledOffset), ReadBoolean(memory, GateEnabledOffset),
                ReadBoolean(memory, HighPassEnabledOffset), ReadBoolean(memory, EqualizerEnabledOffset), MicrophoneEqualizer.FromLevels(levels)));
        state.Validate(); return state;
    }
    public static IReadOnlyList<EffectMemoryPatch> Prepare(ReadOnlySpan<byte> memory, MicrophoneEffects desired)
    {
        var current = Read(memory); desired.Validate();
        var gate = new byte[4]; var filter = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(gate, desired.GatePercent / 100);
        BinaryPrimitives.WriteInt32LittleEndian(filter, desired.FilterLevel);
        var patches = new List<EffectMemoryPatch>();
        foreach (var (offset, after) in new[] { (GateOffset, gate), (FilterOffset, filter) }) {
            if (offset == GateOffset && Math.Abs(current.GatePercent - desired.GatePercent) < .001f) continue;
            var before = memory.Slice(offset, 4).ToArray();
            if (!before.AsSpan().SequenceEqual(after)) patches.Add(new(offset, before, after));
        }
        if (desired.Processing is { } processing) {
            foreach (var (offset, enabled) in new[] { (FilterEnabledOffset, processing.FilterEnabled), (GateEnabledOffset, processing.GateEnabled), (HighPassEnabledOffset, processing.HighPassEnabled), (EqualizerEnabledOffset, processing.EqualizerEnabled) }) {
                var after = (byte)(enabled ? 1 : 0);
                if (memory[offset] != after) patches.Add(new(offset, [memory[offset]], [after]));
            }
            var levels = processing.Equalizer.Levels();
            for (var i = 0; i < levels.Length; i++) {
                var offset = EqualizerOffset + i * 4; var after = new byte[4];
                BinaryPrimitives.WriteSingleLittleEndian(after, levels[i]);
                var before = memory.Slice(offset, 4).ToArray();
                if (!before.AsSpan().SequenceEqual(after)) patches.Add(new(offset, before, after));
            }
        }
        return patches;
    }
    private static bool ReadBoolean(ReadOnlySpan<byte> memory, int offset) => memory[offset] switch {
        0 => false, 1 => true, _ => throw new InvalidDataException($"Invalid EPOS boolean at offset {offset}.")
    };
    public static void ValidatePatch(int offset, int length)
    {
        var boolean = length == 1 && offset is EqualizerEnabledOffset or FilterEnabledOffset or GateEnabledOffset or HighPassEnabledOffset;
        var scalar = length == 4 && (offset is GateOffset or FilterOffset || offset >= EqualizerOffset && offset < EqualizerOffset + 36 && (offset - EqualizerOffset) % 4 == 0);
        if (!boolean && !scalar) throw new InvalidOperationException("Only validated microphone effect fields may be written.");
    }
    // A legacy profile without Processing preserves the current flags/EQ.
    public static bool Matches(MicrophoneEffects a, MicrophoneEffects b)
    {
        if (!float.IsFinite(a.GatePercent) || !float.IsFinite(b.GatePercent) || Math.Abs(a.GatePercent - b.GatePercent) >= .001f || a.FilterLevel != b.FilterLevel) return false;
        if (b.Processing is null) return true;
        if (a.Processing is not { } x || b.Processing is not { } y || x.FilterEnabled != y.FilterEnabled || x.GateEnabled != y.GateEnabled || x.HighPassEnabled != y.HighPassEnabled || x.EqualizerEnabled != y.EqualizerEnabled) return false;
        return x.Equalizer.Levels().Zip(y.Equalizer.Levels()).All(pair => Math.Abs(pair.First - pair.Second) < .0001f);
    }
}

// A session owns the device's lock until disposed. Implementations must perform bounded I/O.
// USB adapters can provide a different transport while sharing the effects/backend contract.
public interface IEffectMemorySession : IDisposable
{
    byte[] Read();
    void Write(int offset, byte[] data);
    void Notify();
}

public sealed class ApoMicrophoneEffectsBackend(Func<AudioEndpoint, IEffectMemorySession> open) : IMicrophoneEffectsBackend
{
    public MicrophoneEffects Read(AudioEndpoint endpoint)
    {
        DemoMicrophoneEffectsBackend.ValidateEndpoint(endpoint);
        using var session = open(endpoint);
        return ApoMicrophoneCodec.Read(session.Read());
    }
    public MicrophoneEffects Apply(AudioEndpoint endpoint, MicrophoneEffects expected, MicrophoneEffects desired)
    {
        MicrophoneEffects.ValidateUpdate(endpoint, expected, desired);
        using var session = open(endpoint);
        var memory = session.Read();
        if (!ApoMicrophoneCodec.Matches(ApoMicrophoneCodec.Read(memory), expected))
            throw new InvalidOperationException("Microphone effects changed. Reload before applying.");
        var patches = ApoMicrophoneCodec.Prepare(memory, desired);
        if (patches.Count == 0) return ApoMicrophoneCodec.Read(memory);
        var attempted = new List<EffectMemoryPatch>();
        try {
            foreach (var patch in patches) { attempted.Add(patch); session.Write(patch.Offset, patch.After); }
            session.Notify();
            var result = ApoMicrophoneCodec.Read(session.Read());
            if (!ApoMicrophoneCodec.Matches(result, desired)) throw new IOException("EPOS effects readback did not match the requested settings.");
            return result;
        } catch (Exception failure) {
            try {
                var current = session.Read();
                ApoMicrophoneCodec.Read(current);
                foreach (var patch in attempted) {
                    var value = current.AsSpan(patch.Offset, patch.Before.Length);
                    if (!value.SequenceEqual(patch.Before) && !value.SequenceEqual(patch.After))
                        throw new IOException("Effects changed during failure; restoration cancelled to preserve the newer state.");
                }
                foreach (var patch in attempted) session.Write(patch.Offset, patch.Before);
                session.Notify();
                var restored = session.Read();
                foreach (var patch in attempted)
                    if (!restored.AsSpan(patch.Offset, patch.Before.Length).SequenceEqual(patch.Before))
                        throw new IOException("Effects restoration could not be verified.");
            } catch (Exception rollbackFailure) {
                throw new AggregateException("Effects update failed and restoration failed. Reload current settings.", failure, rollbackFailure);
            }
            throw;
        }
    }
}
