namespace EposControl.Core;

public enum AudioDirection { Playback, Microphone }
public enum FeatureState { Ready, PendingValidation, Unknown }
public sealed record UsbIdentity(string VendorId, string ProductId, string InstanceId);
public sealed record AudioFormat(int Channels, int SampleRate, int BitsPerSample);
public sealed record AudioState(float Level, bool Muted, float Decibels, uint HardwareSupport);
public sealed record AudioEndpoint(string Id, string Name, string AdapterName, AudioDirection Direction,
    UsbIdentity? Usb, AudioState? State, AudioFormat? Format, string? Error)
{
    public string Label => $"{Name} · {(Direction == AudioDirection.Microphone ? "Microphone" : "Playback")}";
    public string ProfileIdentity => Usb is null ? Id : $"{Usb.InstanceId}|{Direction}|{AdapterName}|{Name}";
}
public sealed record DeviceFeature(string Name, FeatureState State, string Description);
public sealed record DeviceDefinition(string VendorId, string ProductId, string Model, string AdapterFamily,
    bool MicrophoneProcessing, bool Sidetone, bool Surround);

// Real and demo adapters share this interface. The UI never accesses native handles.
public interface IAudioBackend
{
    IReadOnlyList<AudioEndpoint> Discover();
    AudioState Read(string endpointId);
    AudioState Apply(string endpointId, float level, bool muted);
}

public sealed class DeviceCatalog
{
    private readonly IReadOnlyList<DeviceDefinition> definitions;
    public DeviceCatalog(IReadOnlyList<DeviceDefinition> definitions) => this.definitions = definitions;
    public DeviceDefinition? Find(UsbIdentity? usb) => usb is null ? null : definitions.FirstOrDefault(d =>
        d.VendorId.Equals(usb.VendorId, StringComparison.OrdinalIgnoreCase) && d.ProductId.Equals(usb.ProductId, StringComparison.OrdinalIgnoreCase));
    public IReadOnlyList<DeviceFeature> Features(AudioEndpoint endpoint, bool microphoneEffectsReady = false, bool sidetoneReady = false, bool playbackEffectsReady = false)
    {
        var definition = Find(endpoint.Usb);
        var input = endpoint.Direction == AudioDirection.Microphone;
        var result = new List<DeviceFeature> {
            new(input ? "Microphone level" : "Playback volume", endpoint.State is null ? FeatureState.Unknown : FeatureState.Ready,
                "Windows endpoint level; changes are applied to this endpoint only."),
            new("Mute", endpoint.State is null ? FeatureState.Unknown : FeatureState.Ready, "Windows endpoint mute.")
        };
        if (input) {
            result.Add(new("Sidetone", sidetoneReady && definition?.Sidetone == true ? FeatureState.Ready : definition?.Sidetone == true ? FeatureState.PendingValidation : FeatureState.Unknown,
                sidetoneReady && definition?.Sidetone == true ? (definition.ProductId.Equals("0098", StringComparison.OrdinalIgnoreCase) ? "Hardware headphone monitoring level through the GSX USB control." : "Hardware headphone monitoring level and mute through Windows audio topology.") : definition?.Sidetone == true ? "The sidetone adapter is unavailable or awaiting a hardware read." : "Device-specific support has not been mapped."));
            result.Add(new("Microphone processing", microphoneEffectsReady && definition?.MicrophoneProcessing == true ? FeatureState.Ready : definition?.MicrophoneProcessing == true ? FeatureState.PendingValidation : FeatureState.Unknown,
                microphoneEffectsReady && definition?.MicrophoneProcessing == true ? "Gate, noise filter, " + (definition.ProductId.Equals("009f", StringComparison.OrdinalIgnoreCase) ? "high-pass and " : "and ") + "nine-band EQ through the installed EPOS audio processor." : definition?.MicrophoneProcessing == true ? "Effect adapter unavailable or still awaiting validation." : "Processing support has not been mapped for this model."));
        } else {
            result.Add(new("7.1 surround", playbackEffectsReady && definition?.Surround == true ? FeatureState.Ready : definition?.Surround == true ? FeatureState.PendingValidation : FeatureState.Unknown,
                playbackEffectsReady && definition?.Surround == true ? "Stereo / virtual 7.1 through the installed EPOS audio processor." : definition?.Surround == true ? "Playback adapter unavailable or awaiting validation." : "Surround support has not been verified for this model."));
            result.Add(new("Equalizer", playbackEffectsReady ? FeatureState.Ready : FeatureState.PendingValidation,
                playbackEffectsReady ? "Nine playback bands from 64 Hz to 16 kHz, -6 to +6 dB." : "Playback EQ adapter unavailable or awaiting validation."));
            if (definition?.ProductId.Equals("0098", StringComparison.OrdinalIgnoreCase) == true)
                result.Add(new("Reverb", playbackEffectsReady ? FeatureState.Ready : FeatureState.PendingValidation,
                    playbackEffectsReady ? "Independent reverb switch and retained amount for virtual 7.1." : "Reverb adapter unavailable."));
        }
        return result;
    }
    public static bool IsEpos(AudioEndpoint e) => e.Usb is not null
        ? e.Usb.VendorId.Equals("1395", StringComparison.OrdinalIgnoreCase) || e.Usb.VendorId.Equals("1394", StringComparison.OrdinalIgnoreCase)
        : !System.Text.RegularExpressions.Regex.IsMatch(e.Name + " " + e.AdapterName, "Virtual|Elgato|Voicemeeter|Wave Link", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            && System.Text.RegularExpressions.Regex.IsMatch(e.Name + " " + e.AdapterName, "EPOS|Sennheiser", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
