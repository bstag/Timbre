using System.Text.Json;
using System.Text.Json.Serialization;

namespace EposControl.Core;

// Explicit startup input for the cold-initialization experiment, separate from profiles.
public sealed record ApoStartupState([property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string ProfileIdentity, [property: JsonRequired] MicrophoneEffects Effects, byte[]? DiagnosticSeed = null,
    string? DeviceIdentity = null)
{
    public static ApoStartupState LoadForB20(string path, AudioEndpoint endpoint)
    {
        DemoMicrophoneEffectsBackend.ValidateEndpoint(endpoint);
        ApoSharedObjects.B20Name(endpoint.Usb!);
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("APO startup state is too large.");
        var text = File.ReadAllText(path);
        if (text.Length > 16384) throw new InvalidDataException("APO startup state is too large.");
        var state = JsonSerializer.Deserialize<ApoStartupState>(text, new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("APO startup state is missing.");
        state.ValidateForB20(endpoint);
        return state;
    }
    public void ValidateForB20(AudioEndpoint endpoint)
    {
        DemoMicrophoneEffectsBackend.ValidateEndpoint(endpoint); ApoSharedObjects.B20Name(endpoint.Usb!);
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(ProfileIdentity) ||
            (DeviceIdentity is null ? ProfileIdentity != endpoint.ProfileIdentity : DeviceIdentity != ProcessingStateStore.DeviceIdentity(endpoint)))
            throw new InvalidDataException("APO startup state version or physical device identity does not match.");
        if (Effects?.Processing is null) throw new InvalidDataException("Complete microphone processing state is required for startup.");
        Effects.Validate();
        if (DiagnosticSeed is { } seed && !ApoMicrophoneCodec.Matches(ApoMicrophoneCodec.Read(seed), Effects))
            throw new InvalidDataException("Diagnostic snapshot settings do not match the typed startup state.");
    }
}
