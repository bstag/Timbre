namespace Timbre.Core;

public sealed record PlaybackEqAudioComparison(AudioValidationOutcome Outcome, string Reason, double GainDb, double ReturnDifferenceDb);
public sealed record PlaybackEqAudioPhase(string Name, PlaybackEffects Settings, AudioMeasurement Measurement);
public sealed record PlaybackEqAudioReport(AudioValidationOutcome Outcome, string Reason, PlaybackEffects Before,
    IReadOnlyList<PlaybackEqAudioPhase> Phases, PlaybackEqAudioComparison? Comparison,
    PlaybackEffects? Restored, bool SettingsRestored, string? Error);

public static class PlaybackEqAudioEvaluator
{
    public static PlaybackEqAudioComparison Compare(AudioMeasurement flatBefore, AudioMeasurement boosted, AudioMeasurement flatAfter)
    {
        var gain = boosted.RmsDbFs - (flatBefore.RmsDbFs + flatAfter.RmsDbFs) / 2;
        var returned = Math.Abs(flatBefore.RmsDbFs - flatAfter.RmsDbFs);
        PlaybackEqAudioComparison Result(AudioValidationOutcome outcome, string reason) => new(outcome, reason, gain, returned);
        if (new[] { flatBefore, boosted, flatAfter }.Any(m => m.Frames <= 0 || m.Seconds < 1 || !double.IsFinite(m.RmsDbFs) ||
            !double.IsFinite(m.PeakDbFs) || m.ClippedSamples != 0 || m.InvalidSamples != 0 || m.Discontinuities != 0 || m.TimestampErrors != 0))
            return Result(AudioValidationOutcome.Inconclusive, "Capture was short, interrupted, clipped or invalid.");
        if (Math.Min(flatBefore.RmsDbFs, flatAfter.RmsDbFs) < -90 || returned > 1)
            return Result(AudioValidationOutcome.Inconclusive, "The flat reference was too quiet or changed by more than 1 dB.");
        return gain is >= 4 and <= 8
            ? Result(AudioValidationOutcome.Passed, "The +6 dB 1 kHz EQ edit produced a reversible 4–8 dB increase in captured tone RMS.")
            : Result(AudioValidationOutcome.Inconclusive, "The expected EQ response was not observed. Loopback may be before processing, or this audio path may not apply the effect; settings readback alone cannot distinguish them.");
    }
}

// Explicit diagnostic transaction. Does not create APO objects, change sound mode,
// alter Windows levels/defaults or persist profiles. Source supplies a steady 1 kHz tone.
public static class PlaybackEqAudioValidation
{
    public static PlaybackEqAudioReport Run(AudioEndpoint endpoint, IAudioBackend audio, IPlaybackEffectsBackend playback,
        IAudioMeasurementSource source, TimeSpan duration)
    {
        if (duration.TotalSeconds is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(duration));
        WindowsApoMemory.ValidatePlaybackIdentity(endpoint, audio.Discover());
        var before = playback.Read(endpoint); before.Validate();
        if (before.Equalizer is null || before.Reverb is null) throw new InvalidOperationException("Reload complete playback processing before measuring EQ.");
        var volume = audio.Read(endpoint.Id);
        if (volume.Muted || volume.Level == 0) throw new InvalidOperationException("A usable, unmuted GSX output is required.");
        var flat = before with { Equalizer = PlaybackEqualizer.Flat, Reverb = before.Reverb with { Enabled = false } };
        var boost = flat with { Equalizer = PlaybackEqualizer.Flat with { Band5 = 6 } };
        var expected = before;
        var phases = new List<PlaybackEqAudioPhase>();
        PlaybackEqAudioComparison? comparison = null;
        PlaybackEffects? restored = null;
        Exception? failure = null;
        try {
            foreach (var (name, settings) in new[] { ("Flat before", flat), ("1 kHz +6 dB", boost), ("Flat after", flat) }) {
                CheckVolume();
                expected = playback.Apply(endpoint, expected, settings);
                if (!ApoPlaybackCodec.Matches(expected, settings)) throw new InvalidOperationException("Playback EQ readback differs from the requested diagnostic curve.");
                var measurement = source.Measure(duration);
                if (!ApoPlaybackCodec.Matches(playback.Read(endpoint), expected))
                    throw new InvalidOperationException("Playback processing changed during measurement; preserving the newer settings.");
                CheckVolume(); phases.Add(new(name, expected, measurement));
            }
            comparison = PlaybackEqAudioEvaluator.Compare(phases[0].Measurement, phases[1].Measurement, phases[2].Measurement);
        } catch (Exception ex) { failure = ex; }
        finally {
            try {
                WindowsApoMemory.ValidatePlaybackIdentity(endpoint, audio.Discover());
                restored = playback.Apply(endpoint, expected, before);
            }
            catch (Exception ex) { failure = failure is null ? ex : new AggregateException("EQ measurement and restoration failed.", failure, ex); }
        }
        var settingsRestored = restored is not null && ApoPlaybackCodec.Matches(restored, before);
        if (!settingsRestored && failure is null) failure = new InvalidOperationException("Starting playback settings were not restored.");
        return new(failure is null ? comparison!.Outcome : AudioValidationOutcome.Failed,
            failure is null ? comparison!.Reason : "Playback EQ measurement failed; inspect restoration and errors.",
            before, phases, comparison, restored, settingsRestored, failure?.ToString());

        void CheckVolume()
        {
            WindowsApoMemory.ValidatePlaybackIdentity(endpoint, audio.Discover());
            var current = audio.Read(endpoint.Id);
            if (current.Muted != volume.Muted || Math.Abs(current.Level - volume.Level) > .00001)
                throw new InvalidOperationException("Windows level or mute changed during measurement; the newer value is preserved.");
        }
    }
}
