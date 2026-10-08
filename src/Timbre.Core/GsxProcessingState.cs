using System.Text.Json.Serialization;

namespace Timbre.Core;

// Both pages share one GSX mapping and must be observed/restored under one lock.
public sealed record GsxProcessingState([property: JsonRequired] MicrophoneEffects Microphone,
    [property: JsonRequired] PlaybackEffects Playback)
{
    public void Validate()
    {
        if (Microphone?.Processing is null || Playback?.Equalizer is null || Playback.Reverb is null)
            throw new InvalidDataException("Complete GSX microphone and playback processing is required.");
        Microphone.Validate(); Playback.Validate();
    }
    public static GsxProcessingState Read(ReadOnlySpan<byte> memory) => new(ApoMicrophoneCodec.Read(memory), ApoPlaybackCodec.Read(memory));
    public bool Matches(GsxProcessingState expected) => ApoMicrophoneCodec.Matches(Microphone, expected.Microphone) &&
        ApoPlaybackCodec.Matches(Playback, expected.Playback);
}

public interface IGsxProcessingBackend
{
    GsxProcessingState Read(AudioEndpoint microphone, AudioEndpoint playback);
    GsxProcessingState Apply(AudioEndpoint microphone, AudioEndpoint playback, GsxProcessingState expected, GsxProcessingState desired);
}

public sealed class ApoGsxProcessingBackend(Func<AudioEndpoint, AudioEndpoint, IEffectMemorySession> open) : IGsxProcessingBackend
{
    public GsxProcessingState Read(AudioEndpoint microphone, AudioEndpoint playback)
    {
        GsxApoStartupState.ValidateEndpoints(microphone, playback);
        using var session = open(microphone, playback);
        return GsxProcessingState.Read(session.Read());
    }
    public GsxProcessingState Apply(AudioEndpoint microphone, AudioEndpoint playback, GsxProcessingState expected, GsxProcessingState desired)
    {
        GsxApoStartupState.ValidateEndpoints(microphone, playback);
        expected.Validate(); desired.Validate();
        MicrophoneEffects.ValidateUpdate(microphone, expected.Microphone, desired.Microphone);
        PlaybackEffects.ValidateUpdate(expected.Playback, desired.Playback);
        using var session = open(microphone, playback);
        var memory = session.Read();
        var before = GsxProcessingState.Read(memory);
        if (!before.Matches(expected)) throw new InvalidOperationException("GSX processing changed. Reload both pages before restoring.");
        var micPatches = ApoMicrophoneCodec.Prepare(memory, desired.Microphone);
        var playbackPatches = ApoPlaybackCodec.Prepare(memory, desired.Playback);
        // Validate the entire plan before the first write, including unsupported GSX fields.
        foreach (var patch in micPatches) WindowsApoMemory.ValidateMicrophonePatch(microphone, patch.Offset, patch.After.Length);
        foreach (var patch in playbackPatches) ApoPlaybackCodec.ValidatePatch(patch.Offset, patch.After.Length);
        var patches = micPatches.Concat(playbackPatches).ToArray();
        if (patches.Length == 0) return before;
        var attempted = new List<EffectMemoryPatch>();
        try {
            foreach (var patch in patches) { attempted.Add(patch); session.Write(patch.Offset, patch.After); }
            session.Notify();
            var result = GsxProcessingState.Read(session.Read());
            if (!result.Matches(desired)) throw new IOException("GSX processing readback differs from the requested settings.");
            return result;
        } catch (Exception failure) {
            try {
                var current = session.Read(); GsxProcessingState.Read(current);
                foreach (var patch in attempted) {
                    var value = current.AsSpan(patch.Offset, patch.Before.Length);
                    if (!value.SequenceEqual(patch.Before) && !value.SequenceEqual(patch.After))
                        throw new IOException("GSX processing changed during failure; newer state was preserved.");
                }
                foreach (var patch in attempted) session.Write(patch.Offset, patch.Before);
                session.Notify();
                var restored = session.Read(); GsxProcessingState.Read(restored);
                foreach (var patch in attempted)
                    if (!restored.AsSpan(patch.Offset, patch.Before.Length).SequenceEqual(patch.Before))
                        throw new IOException("GSX processing restoration could not be verified.");
            } catch (Exception rollbackFailure) {
                throw new AggregateException("GSX processing update and restoration failed. Reload both pages.", failure, rollbackFailure);
            }
            throw;
        }
    }
}
