namespace Timbre.Core;

// App restoration uses existing objects only. Either endpoint incarnation changing
// starts a new connection; failed attempts are not repeated by polling.
public sealed class GsxProcessingRestoreSession(GsxProcessingStateStore store)
{
    private readonly HashSet<string> attempted = [];

    public static (AudioEndpoint Microphone, AudioEndpoint Playback) SelectPair(IReadOnlyList<AudioEndpoint> endpoints)
    {
        var gsx = endpoints.Where(IsGsx).ToArray();
        if (gsx.Length != 2 || gsx.Count(e => e.Direction == AudioDirection.Microphone) != 1 ||
            gsx.Count(e => e.Direction == AudioDirection.Playback) != 1)
            throw new InvalidOperationException("Exactly one physical GSX microphone and sound pair is required.");
        var microphone = gsx.Single(e => e.Direction == AudioDirection.Microphone);
        var playback = gsx.Single(e => e.Direction == AudioDirection.Playback);
        GsxApoStartupState.ValidateEndpoints(microphone, playback);
        return (microphone, playback);
    }
    public static bool IsGsx(AudioEndpoint endpoint) => endpoint.Usb is { } usb &&
        usb.VendorId.Equals("1395", StringComparison.OrdinalIgnoreCase) && usb.ProductId.Equals("0098", StringComparison.OrdinalIgnoreCase);

    private static string Connection(AudioEndpoint microphone, AudioEndpoint playback) =>
        GsxProcessingStateStore.DeviceIdentity(microphone, playback) + "|" + microphone.Id + "|" + playback.Id;

    public void Observe(IReadOnlyList<AudioEndpoint> endpoints)
    {
        try {
            var (microphone, playback) = SelectPair(endpoints);
            var connection = Connection(microphone, playback);
            attempted.RemoveWhere(key => key != connection);
        } catch (InvalidOperationException) { attempted.Clear(); }
        catch (InvalidDataException) { attempted.Clear(); }
    }

    public GsxProcessingState? RestoreIfEnabled(AudioEndpoint microphone, AudioEndpoint playback,
        IAudioBackend audio, IGsxProcessingBackend backend)
    {
        if (!attempted.Add(Connection(microphone, playback))) return null;
        var saved = store.Load(microphone, playback);
        return saved?.RestoreOnConnect == true ? store.Restore(microphone, playback, audio, backend) : null;
    }
}

// Shares the existing demo page adapters so paired restoration is visible in the UI.
// The native adapter instead performs both pages in one memory transaction.
public sealed class DemoGsxProcessingBackend(DemoMicrophoneEffectsBackend microphoneEffects,
    DemoPlaybackEffectsBackend playbackEffects) : IGsxProcessingBackend
{
    public GsxProcessingState Read(AudioEndpoint microphone, AudioEndpoint playback)
    {
        GsxApoStartupState.ValidateEndpoints(microphone, playback);
        return new(microphoneEffects.Read(microphone), playbackEffects.Read(playback));
    }
    public GsxProcessingState Apply(AudioEndpoint microphone, AudioEndpoint playback,
        GsxProcessingState expected, GsxProcessingState desired)
    {
        expected.Validate(); desired.Validate();
        var before = Read(microphone, playback);
        if (!before.Matches(expected)) throw new InvalidOperationException("GSX processing changed. Reload both pages before restoring.");
        MicrophoneEffects.ValidateUpdate(microphone, before.Microphone, desired.Microphone);
        PlaybackEffects.ValidateUpdate(before.Playback, desired.Playback);
        microphoneEffects.Apply(microphone, before.Microphone, desired.Microphone);
        playbackEffects.Apply(playback, before.Playback, desired.Playback);
        return Read(microphone, playback);
    }
}
