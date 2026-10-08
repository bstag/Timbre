using System.Text.Json.Serialization;

namespace EposControl.Core;

public sealed record MicrophoneEqualizer(
    [property: JsonRequired] float Band1, [property: JsonRequired] float Band2, [property: JsonRequired] float Band3,
    [property: JsonRequired] float Band4, [property: JsonRequired] float Band5, [property: JsonRequired] float Band6,
    [property: JsonRequired] float Band7, [property: JsonRequired] float Band8, [property: JsonRequired] float Band9)
{
    public static MicrophoneEqualizer Flat { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    public float[] Levels() => [Band1, Band2, Band3, Band4, Band5, Band6, Band7, Band8, Band9];
    public static MicrophoneEqualizer FromLevels(IReadOnlyList<float> levels)
    {
        if (levels.Count != 9) throw new ArgumentException("Microphone EQ requires exactly nine bands.", nameof(levels));
        var result = new MicrophoneEqualizer(levels[0], levels[1], levels[2], levels[3], levels[4], levels[5], levels[6], levels[7], levels[8]);
        result.Validate(); return result;
    }
    public void Validate()
    {
        if (Levels().Any(v => !float.IsFinite(v) || v is < -6 or > 6))
            throw new ArgumentOutOfRangeException(nameof(MicrophoneEqualizer), "Microphone EQ bands must be -6 to +6 dB.");
    }
}

public sealed record MicrophoneProcessing(
    [property: JsonRequired] bool FilterEnabled, [property: JsonRequired] bool GateEnabled,
    [property: JsonRequired] bool HighPassEnabled, [property: JsonRequired] bool EqualizerEnabled,
    [property: JsonRequired] MicrophoneEqualizer Equalizer)
{
    public void Validate()
    {
        if (Equalizer is null) throw new InvalidDataException("Microphone EQ state is missing.");
        Equalizer.Validate();
    }
}

public sealed record MicrophoneEffects([property: JsonRequired] float GatePercent, [property: JsonRequired] int FilterLevel, MicrophoneProcessing? Processing = null)
{
    public void Validate()
    {
        if (!float.IsFinite(GatePercent) || GatePercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(GatePercent), "Noise gate must be 0–100%.");
        if (FilterLevel is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(FilterLevel), "Noise filter preset must be 0, 1, or 2.");
        Processing?.Validate();
    }
    public static void ValidateUpdate(MicrophoneEffects expected, MicrophoneEffects desired)
    {
        expected.Validate(); desired.Validate();
        if (desired.Processing is not null && expected.Processing is null)
            throw new InvalidOperationException("Reload complete microphone processing state before applying switches or EQ.");
    }
    public static void ValidateUpdate(AudioEndpoint endpoint, MicrophoneEffects expected, MicrophoneEffects desired)
    {
        DemoMicrophoneEffectsBackend.ValidateEndpoint(endpoint);
        ValidateUpdate(expected, desired);
        if (endpoint.Usb!.ProductId.Equals("0098", StringComparison.OrdinalIgnoreCase) &&
            desired.Processing is { } requested && expected.Processing is { } observed && requested.HighPassEnabled != observed.HighPassEnabled)
            throw new InvalidOperationException("GSX 300 high-pass control has not been validated; preserve its current state.");
    }
}

// Every future driver adapter must provide the same read / compare / apply contract.
// Expected state prevents a stale UI or profile from overwriting a newer edit.
public interface IMicrophoneEffectsBackend
{
    MicrophoneEffects Read(AudioEndpoint endpoint);
    MicrophoneEffects Apply(AudioEndpoint endpoint, MicrophoneEffects expected, MicrophoneEffects desired);
}

public sealed class DemoMicrophoneEffectsBackend : IMicrophoneEffectsBackend
{
    private readonly Dictionary<string, MicrophoneEffects> states = [];
    public MicrophoneEffects Read(AudioEndpoint endpoint)
    {
        ValidateEndpoint(endpoint);
        return states.GetValueOrDefault(endpoint.ProfileIdentity, new(0, 2, new(true, true,
            endpoint.Usb!.ProductId.Equals("009f", StringComparison.OrdinalIgnoreCase), true, MicrophoneEqualizer.Flat)));
    }
    public MicrophoneEffects Apply(AudioEndpoint endpoint, MicrophoneEffects expected, MicrophoneEffects desired)
    {
        MicrophoneEffects.ValidateUpdate(endpoint, expected, desired);
        var current = Read(endpoint);
        if (!ApoMicrophoneCodec.Matches(current, expected)) throw new InvalidOperationException("Microphone effects changed. Reload before applying.");
        states[endpoint.ProfileIdentity] = desired with { Processing = desired.Processing ?? current.Processing };
        return Read(endpoint);
    }
    internal static void ValidateEndpoint(AudioEndpoint endpoint)
    {
        if (endpoint.Direction != AudioDirection.Microphone || endpoint.Usb is not { } usb ||
            !usb.VendorId.Equals("1395", StringComparison.OrdinalIgnoreCase) ||
            !(usb.ProductId.Equals("009F", StringComparison.OrdinalIgnoreCase) || usb.ProductId.Equals("0098", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Microphone effects are supported only for mapped B20 / GSX 300 microphone endpoints.");
    }
}
