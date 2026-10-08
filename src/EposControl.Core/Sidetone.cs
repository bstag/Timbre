using System.Text.Json.Serialization;

namespace EposControl.Core;

public sealed record SidetoneSettings([property: JsonRequired] float LeftDb, [property: JsonRequired] float RightDb,
    [property: JsonRequired] bool Muted)
{
    public void Validate()
    {
        // B20 driver reports -34.5..9 dB. Suite's recovered formula can set 9.0864 dB at 100%.
        if (!float.IsFinite(LeftDb) || !float.IsFinite(RightDb) || LeftDb is < -34.5f or > 9.087f || RightDb is < -34.5f or > 9.087f)
            throw new ArgumentOutOfRangeException(nameof(SidetoneSettings), "B20 sidetone level is outside the validated hardware range.");
    }
}
public sealed record SidetoneState(string ControlIdentity, float MinimumDb, float MaximumDb, float StepDb, SidetoneSettings Settings)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ControlIdentity) || MinimumDb != -34.5f || MaximumDb != 9 || StepDb != 1.5f)
            throw new InvalidDataException("Unknown B20 sidetone layout or range.");
        if (Settings is null) throw new InvalidDataException("Sidetone settings are missing.");
        Settings.Validate();
    }
}
public interface ISidetoneBackend
{
    SidetoneState Read(AudioEndpoint endpoint);
    SidetoneState Apply(AudioEndpoint endpoint, SidetoneState expected, SidetoneSettings desired);
}
public interface ISidetoneSession : IDisposable
{
    SidetoneState Read();
    void WriteLevels(float leftDb, float rightDb);
    void WriteMute(bool muted);
}
public sealed class SidetoneBackend(Func<AudioEndpoint, ISidetoneSession> open) : ISidetoneBackend
{
    public static void ValidateEndpoint(AudioEndpoint endpoint)
    {
        if (endpoint.Direction != AudioDirection.Microphone || endpoint.Usb is not { VendorId: "1395", ProductId: "009F" })
            throw new InvalidOperationException("Sidetone is currently mapped only for the B20 microphone.");
    }
    public SidetoneState Read(AudioEndpoint endpoint)
    {
        ValidateEndpoint(endpoint); using var session = open(endpoint); var state = session.Read(); state.Validate(); return state;
    }
    public static bool Matches(SidetoneState actual, SidetoneState expected) => actual.ControlIdentity == expected.ControlIdentity &&
        actual.MinimumDb == expected.MinimumDb && actual.MaximumDb == expected.MaximumDb && actual.StepDb == expected.StepDb &&
        Matches(actual.Settings, expected.Settings, .00001f);
    public static bool Matches(SidetoneSettings actual, SidetoneSettings expected, float tolerance = .004f) =>
        float.IsFinite(actual.LeftDb) && float.IsFinite(actual.RightDb) && float.IsFinite(expected.LeftDb) && float.IsFinite(expected.RightDb) &&
        Math.Abs(actual.LeftDb - expected.LeftDb) <= tolerance && Math.Abs(actual.RightDb - expected.RightDb) <= tolerance && actual.Muted == expected.Muted;
    public SidetoneState Apply(AudioEndpoint endpoint, SidetoneState expected, SidetoneSettings desired)
    {
        ValidateEndpoint(endpoint); expected.Validate(); desired.Validate();
        using var session = open(endpoint);
        var before = session.Read(); before.Validate();
        if (!Matches(before, expected)) throw new InvalidOperationException("Sidetone changed. Reload before applying.");
        var levelsChanged = before.Settings.LeftDb != desired.LeftDb || before.Settings.RightDb != desired.RightDb;
        var muteChanged = before.Settings.Muted != desired.Muted;
        if (!levelsChanged && !muteChanged) return before;
        var attemptedLevels = false; var attemptedMute = false;
        try {
            if (muteChanged && desired.Muted) { attemptedMute = true; session.WriteMute(true); }
            if (levelsChanged) { CheckOwned(); attemptedLevels = true; session.WriteLevels(desired.LeftDb, desired.RightDb); }
            if (muteChanged && !desired.Muted) { CheckOwned(); attemptedMute = true; session.WriteMute(false); }
            var result = session.Read(); result.Validate();
            if (result.ControlIdentity != before.ControlIdentity || !Matches(result.Settings, desired)) throw new IOException("Sidetone readback does not match the requested settings.");
            return result;
        } catch (Exception failure) {
            try {
                var current = CheckOwned(true);
                // Restore only fields we attempted; preserve unrelated newer fields.
                if (attemptedMute && before.Settings.Muted) session.WriteMute(true);
                if (attemptedLevels) session.WriteLevels(before.Settings.LeftDb, before.Settings.RightDb);
                if (attemptedMute && !before.Settings.Muted) session.WriteMute(false);
                var restored = session.Read(); restored.Validate();
                var target = current.Settings with {
                    LeftDb = attemptedLevels ? before.Settings.LeftDb : current.Settings.LeftDb,
                    RightDb = attemptedLevels ? before.Settings.RightDb : current.Settings.RightDb,
                    Muted = attemptedMute ? before.Settings.Muted : current.Settings.Muted
                };
                if (!Matches(restored.Settings, target)) throw new IOException("Sidetone restoration readback failed.");
            } catch (Exception rollback) { throw new AggregateException("Sidetone update failed and restoration could not be completed.", failure, rollback); }
            throw;
        }
        SidetoneState CheckOwned(bool rollback = false)
        {
            var current = session.Read(); current.Validate();
            if (current.ControlIdentity != before.ControlIdentity || current.MinimumDb != before.MinimumDb || current.MaximumDb != before.MaximumDb || current.StepDb != before.StepDb)
                throw new IOException("Sidetone control identity or range changed.");
            bool LevelOwned(float value, float old, float target) => Math.Abs(value - old) < .00001 || Math.Abs(value - target) <= .004;
            if (attemptedLevels && (!LevelOwned(current.Settings.LeftDb, before.Settings.LeftDb, desired.LeftDb) || !LevelOwned(current.Settings.RightDb, before.Settings.RightDb, desired.RightDb)))
                throw new IOException("Sidetone levels changed during update; preserving newer settings.");
            if (!rollback && !attemptedLevels && (current.Settings.LeftDb != before.Settings.LeftDb || current.Settings.RightDb != before.Settings.RightDb))
                throw new IOException("Sidetone levels changed before their update.");
            if (!rollback && !attemptedMute && current.Settings.Muted != before.Settings.Muted) throw new IOException("Sidetone mute changed before its update.");
            return current;
        }
    }
}
public sealed class DemoSidetoneBackend : ISidetoneBackend
{
    private readonly Dictionary<string, SidetoneState> states = [];
    public SidetoneState Read(AudioEndpoint endpoint)
    {
        SidetoneBackend.ValidateEndpoint(endpoint);
        return states.GetValueOrDefault(endpoint.ProfileIdentity, new("demo-" + endpoint.ProfileIdentity, -34.5f, 9, 1.5f, new(0, 0, false)));
    }
    public SidetoneState Apply(AudioEndpoint endpoint, SidetoneState expected, SidetoneSettings desired)
    {
        desired.Validate(); var current = Read(endpoint);
        if (!SidetoneBackend.Matches(current, expected)) throw new InvalidOperationException("Sidetone changed. Reload before applying.");
        return states[endpoint.ProfileIdentity] = current with { Settings = desired };
    }
}
