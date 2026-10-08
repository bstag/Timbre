namespace Timbre.Core;

public sealed record ControlProbe<T>(string Status, T? Value, string? ErrorType, string? Reason);
public sealed record EndpointControlDiagnostic(string EndpointId, string DeviceIdentity,
    ControlProbe<AudioState> Audio, ControlProbe<MicrophoneEffects> Microphone,
    ControlProbe<PlaybackEffects> Playback, ControlProbe<SidetoneState> Sidetone,
    IReadOnlyList<DeviceFeature> Features);
public sealed record ControlDiagnosticReport(int SchemaVersion, DateTimeOffset CollectedAt, bool Demo,
    bool SettingsWrites, bool HardwareStatusQuerySent, bool AudioCaptureStarted,
    IReadOnlyList<AudioEndpoint> Endpoints, IReadOnlyList<EndpointControlDiagnostic> Controls);

// Explicit read-only inspection. Never restores state, applies a profile, queries HID or starts capture.
public sealed class ControlDiagnostics(IAudioBackend audio, DeviceCatalog catalog,
    IMicrophoneEffectsBackend microphone, IPlaybackEffectsBackend playback, ISidetoneBackend sidetone)
{
    public ControlDiagnosticReport Collect(bool demo = false)
    {
        var endpoints = audio.Discover();
        var rows = new List<EndpointControlDiagnostic>();
        foreach (var endpoint in endpoints) {
            var input = endpoint.Direction == AudioDirection.Microphone;
            var epos = endpoint.Usb?.VendorId.Equals("1395", StringComparison.OrdinalIgnoreCase) == true;
            var b20 = epos && endpoint.Usb!.ProductId.Equals("009f", StringComparison.OrdinalIgnoreCase);
            var gsx = epos && endpoint.Usb!.ProductId.Equals("0098", StringComparison.OrdinalIgnoreCase);
            var level = Probe(() => audio.Read(endpoint.Id));
            var mic = input && (b20 || gsx) ? Probe(() => microphone.Read(endpoint))
                : Unsupported<MicrophoneEffects>(input ? "Microphone processing is not mapped for this model." : "This is a sound output.");
            var sound = !input && gsx ? Probe(() => playback.Read(endpoint))
                : Unsupported<PlaybackEffects>(input ? "This is a microphone input." : "Playback processing is currently mapped only for GSX 300.");
            var side = input && b20 ? Probe(() => sidetone.Read(endpoint))
                : Unsupported<SidetoneState>(input && gsx ? "GSX sidetone requires a USB status query. Open its microphone page in the app; read-only diagnostics do not send USB reports."
                    : input ? "Hardware sidetone is not mapped for this model." : "Sidetone is a microphone control.");
            rows.Add(new(endpoint.Id, ProfileStore.DeviceIdentity(endpoint), level, mic, sound, side,
                catalog.Features(endpoint with { State = level.Value }, mic.Value is not null, side.Value is not null, sound.Value is not null)));
        }
        return new(1, DateTimeOffset.UtcNow, demo, false, false, false, endpoints, rows);
    }
    private static ControlProbe<T> Unsupported<T>(string reason) => new("NotSupported", default, null, reason);
    private static ControlProbe<T> Probe<T>(Func<T> read)
    {
        try { return new("Available", read(), null, null); }
        catch (Exception ex) {
            var hint = ex switch {
                WaitHandleCannotBeOpenedException or FileNotFoundException => "The EPOS processing interface is absent. Let Gaming Suite finish starting and refresh. The compatible processor/service is still required.",
                UnauthorizedAccessException => "Windows denied access to this control. Check the installed driver/interface permissions.",
                TimeoutException => "The control is busy. Leave other controllers idle and refresh.",
                InvalidDataException => "The control returned an incompatible state. Its editor remains unavailable.",
                _ => "The control could not be read. Refresh after startup or reconnect; no settings were changed."
            };
            return new("Unavailable", default, ex.GetType().Name, hint + " " + ex.Message);
        }
    }
}
