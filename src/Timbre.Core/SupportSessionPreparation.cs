namespace Timbre.Core;

public sealed record SupportSessionPreparation(GsxApoStartupState GsxSeed, ApoStartupState? B20Seed,
    SavedGsxProcessingState? GsxSaved, SavedProcessingState? B20Saved);

public static class SupportSessionPreparer
{
    public static SupportSessionPreparation Prepare(IReadOnlyList<AudioEndpoint> endpoints, string expectedGsx, string? expectedB20,
        string dataDirectory, Func<AudioEndpoint, byte[]> capture)
    {
        var pair = GsxProcessingRestoreSession.SelectPair(endpoints);
        if (!pair.Microphone.Usb!.InstanceId.Equals(expectedGsx, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Selected GSX changed before startup.");
        var b20s = endpoints.Where(e => e.Direction == AudioDirection.Microphone && e.Usb is { } usb &&
            usb.VendorId.Equals("1395", StringComparison.OrdinalIgnoreCase) && usb.ProductId.Equals("009f", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (b20s.Length > 1 || (expectedB20 is null ? b20s.Length != 0 : b20s.Length != 1 || !b20s[0].Usb!.InstanceId.Equals(expectedB20, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("B20 presence/identity changed before startup.");
        var gsxBytes = capture(pair.Microphone); var gsxEffects = GsxProcessingState.Read(gsxBytes);
        var gsx = new GsxApoStartupState(1, expectedGsx, gsxEffects.Microphone, gsxEffects.Playback, gsxBytes);
        gsx.Validate(pair.Microphone, pair.Playback);
        var gsxSaved = new GsxProcessingStateStore(Path.Combine(dataDirectory, "gsx-processing-state")).Load(pair.Microphone, pair.Playback);
        if (gsxSaved?.RestoreOnConnect == true) MicrophoneEffects.ValidateUpdate(pair.Microphone, gsx.Microphone, gsxSaved.Effects.Microphone);
        ApoStartupState? b20 = null; SavedProcessingState? b20Saved = null;
        if (b20s.Length == 1) {
            var endpoint = b20s[0]; var bytes = capture(endpoint);
            b20 = new(1, endpoint.ProfileIdentity, ApoMicrophoneCodec.Read(bytes), bytes, ProcessingStateStore.DeviceIdentity(endpoint));
            b20.ValidateForB20(endpoint);
            b20Saved = new ProcessingStateStore(Path.Combine(dataDirectory, "processing-state")).Load(endpoint);
        }
        return new(gsx, b20, gsxSaved, b20Saved);
    }
}
