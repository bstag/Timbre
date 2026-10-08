using System.Text.Json;
using System.Text.Json.Serialization;

namespace Timbre.Core;

// Explicit diagnostic input for one physical GSX; not a profile or automatic restore policy.
public sealed record GsxApoStartupState([property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string DeviceInstance, [property: JsonRequired] MicrophoneEffects Microphone,
    [property: JsonRequired] PlaybackEffects Playback, [property: JsonRequired] byte[] DiagnosticSeed)
{
    public string? MicrophoneEndpointId { get; init; }
    public string? PlaybackEndpointId { get; init; }

    public (AudioEndpoint Microphone, AudioEndpoint Playback) SelectCapturedEndpoints(IReadOnlyList<AudioEndpoint> endpoints)
    {
        if (string.IsNullOrWhiteSpace(MicrophoneEndpointId) || string.IsNullOrWhiteSpace(PlaybackEndpointId) || MicrophoneEndpointId == PlaybackEndpointId)
            throw new InvalidDataException("Paused audio initialization requires the exact captured GSX endpoint IDs.");
        var gsx = endpoints.Where(e => e.Usb is { } usb && usb.VendorId.Equals("1395", StringComparison.OrdinalIgnoreCase) &&
            usb.ProductId.Equals("0098", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (gsx.Any(e => !e.Usb!.InstanceId.Equals(DeviceInstance, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Multiple physical GSX devices are unsupported by the model-wide interface.");
        var microphones = gsx.Where(e => e.Id == MicrophoneEndpointId && e.Direction == AudioDirection.Microphone).ToArray();
        var outputs = gsx.Where(e => e.Id == PlaybackEndpointId && e.Direction == AudioDirection.Playback).ToArray();
        if (microphones.Length != 1 || outputs.Length != 1)
            throw new InvalidDataException("Captured GSX microphone/sound endpoint pair is unavailable or ambiguous.");
        var microphone = microphones[0]; var playback = outputs[0];
        Validate(microphone, playback);
        return (microphone, playback);
    }
    public static void ValidateEndpoints(AudioEndpoint microphone, AudioEndpoint playback)
    {
        DemoMicrophoneEffectsBackend.ValidateEndpoint(microphone);
        ApoPlaybackCodec.ValidateEndpoint(playback);
        if (!microphone.Usb!.ProductId.Equals("0098", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(microphone.Usb.InstanceId) ||
            !microphone.Usb.InstanceId.Equals(playback.Usb!.InstanceId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GSX microphone and sound endpoints must belong to the same physical GSX 300.");
    }

    public static GsxApoStartupState Load(string path, AudioEndpoint microphone, AudioEndpoint playback)
    {
        ValidateEndpoints(microphone, playback);
        var state = Load(path);
        state.Validate(microphone, playback);
        return state;
    }

    public static GsxApoStartupState Load(string path)
    {
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("GSX startup state is too large.");
        var text = File.ReadAllText(path);
        if (text.Length > 16384) throw new InvalidDataException("GSX startup state is too large.");
        var state = JsonSerializer.Deserialize<GsxApoStartupState>(text,
            new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("GSX startup state is missing.");
        return state;
    }

    public void Validate(AudioEndpoint microphone, AudioEndpoint playback)
    {
        ValidateEndpoints(microphone, playback);
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(DeviceInstance) ||
            !DeviceInstance.Equals(microphone.Usb!.InstanceId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GSX startup version or physical USB identity does not match.");
        if (Microphone?.Processing is null || Playback?.Equalizer is null || Playback.Reverb is null || DiagnosticSeed is null)
            throw new InvalidDataException("Complete GSX microphone, playback and diagnostic state is required.");
        Microphone.Validate(); Playback.Validate();
        if (!ApoMicrophoneCodec.Matches(ApoMicrophoneCodec.Read(DiagnosticSeed), Microphone) ||
            !ApoPlaybackCodec.Matches(ApoPlaybackCodec.Read(DiagnosticSeed), Playback))
            throw new InvalidDataException("GSX diagnostic snapshot differs from its typed microphone or playback settings.");
        // The native structure occupies 2112 bytes. Unknown nonzero page padding cannot be replayed.
        if (DiagnosticSeed.AsSpan(ApoInitialState.StructureSize).ContainsAnyExcept((byte)0))
            throw new InvalidDataException("GSX snapshot has unknown data beyond the recovered native structure.");
    }
}
