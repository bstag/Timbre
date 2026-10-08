namespace EposControl.Core;

public sealed record GateAudioPhase(string Name, MicrophoneEffects Settings, AudioMeasurement Measurement);
public sealed record GateAudioReport(AudioValidationOutcome Outcome, string Reason, MicrophoneEffects Before,
    IReadOnlyList<GateAudioPhase> Phases, GateAudioComparison? Comparison, MicrophoneEffects? Restored,
    bool SettingsRestored, string? Error);

public static class GateAudioValidation
{
    public static GateAudioReport Run(AudioEndpoint endpoint, IAudioBackend audio, IMicrophoneEffectsBackend effects,
        IAudioMeasurementSource source, TimeSpan duration)
    {
        if (duration.TotalSeconds is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(duration));
        var before = effects.Read(endpoint); before.Validate();
        var processing = before.Processing ?? throw new InvalidOperationException("Complete processing state is required.");
        var volume = audio.Read(endpoint.Id);
        if (volume.Muted || volume.Level == 0) throw new InvalidOperationException("Unmute the microphone and set a usable level before audio validation.");
        var expected = before;
        var phases = new List<GateAudioPhase>();
        MicrophoneEffects? restored = null;
        GateAudioComparison? comparison = null;
        Exception? failure = null;
        try {
            var open = before with { Processing = processing with { GateEnabled = false } };
            var gated = before with { GatePercent = 100, Processing = processing with { GateEnabled = true } };
            foreach (var (name, settings) in new[] { ("Gate off before", open), ("Gate maximum", gated), ("Gate off after", open) }) {
                CheckVolume();
                expected = effects.Apply(endpoint, expected, settings);
                var measurement = source.Measure(duration);
                if (!ApoMicrophoneCodec.Matches(effects.Read(endpoint), expected))
                    throw new InvalidOperationException("Processing changed during capture. The result cannot be attributed to this test.");
                CheckVolume();
                phases.Add(new(name, expected, measurement));
            }
            comparison = GateAudioEvaluator.Compare(phases[0].Measurement, phases[1].Measurement, phases[2].Measurement);
        } catch (Exception ex) { failure = ex; }
        finally {
            try { restored = effects.Apply(endpoint, expected, before); }
            catch (Exception ex) { failure = failure is null ? ex : new AggregateException("Audio validation and restoration failed.", failure, ex); }
        }
        var settingsRestored = restored is not null && ApoMicrophoneCodec.Matches(restored, before);
        return new(failure is null ? comparison!.Outcome : AudioValidationOutcome.Failed,
            failure is null ? comparison!.Reason : "Audio validation failed; inspect the error and restoration status.",
            before, phases, comparison, restored, settingsRestored, failure?.ToString());

        void CheckVolume()
        {
            var current = audio.Read(endpoint.Id);
            if (current.Muted != volume.Muted || Math.Abs(current.Level - volume.Level) > .00001)
                throw new InvalidOperationException("Microphone level or mute changed during validation. Its newer value has been preserved.");
        }
    }
}
