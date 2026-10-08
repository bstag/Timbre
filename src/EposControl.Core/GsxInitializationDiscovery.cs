namespace EposControl.Core;

// Validate live endpoint metadata before the audio service becomes unavailable.
// During the pause only current PnP presence can authorize use of that in-process pair.
internal sealed class GsxInitializationDiscovery(GsxApoStartupState startup,
    Func<IReadOnlyList<AudioEndpoint>> discoverAudio, Func<bool> audioSuspended,
    Func<IReadOnlyList<string>> presentUsb)
{
    private AudioEndpoint[]? prepared;
    public IReadOnlyList<string> LastPhysicalInstances { get; private set; } = [];
    public IReadOnlyList<AudioEndpoint> LastEndpoints { get; private set; } = [];
    public string Method { get; private set; } = "NotPrepared";

    public IReadOnlyList<AudioEndpoint> Prepare()
    {
        if (audioSuspended()) throw new InvalidOperationException("Prepare the GSX endpoint pair before stopping Windows Audio.");
        ValidatePhysicalDevice();
        LastEndpoints = discoverAudio();
        var pair = startup.SelectCapturedEndpoints(LastEndpoints);
        Method = "LiveValidatedPair";
        return prepared = [pair.Microphone with { State = null, Format = null }, pair.Playback with { State = null, Format = null }];
    }

    public IReadOnlyList<AudioEndpoint> Discover()
    {
        if (prepared is null) throw new InvalidOperationException("GSX endpoint discovery was not prepared while audio was running.");
        ValidatePhysicalDevice();
        if (audioSuspended()) {
            Method = "PreparedPairAndCurrentPnp";
            return LastEndpoints = prepared;
        }
        LastEndpoints = discoverAudio();
        var pair = startup.SelectCapturedEndpoints(LastEndpoints);
        Method = "ActivePairAndCurrentPnp";
        return [pair.Microphone, pair.Playback];
    }

    internal static string[] PhysicalGsxInstances(IEnumerable<string> instances) => instances.Where(id => {
        var parts = id.Split('\\');
        return parts.Length == 3 && parts[0].Equals("USB", StringComparison.OrdinalIgnoreCase) &&
            (parts[1].Equals("VID_1395&PID_0098", StringComparison.OrdinalIgnoreCase) || parts[1].StartsWith("VID_1395&PID_0098&", StringComparison.OrdinalIgnoreCase)) &&
            !parts[1].Contains("&MI_", StringComparison.OrdinalIgnoreCase);
    }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private void ValidatePhysicalDevice()
    {
        LastPhysicalInstances = PhysicalGsxInstances(presentUsb());
        if (LastPhysicalInstances.Count != 1 || !LastPhysicalInstances[0].Equals(startup.DeviceInstance, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Exactly the prepared physical GSX must remain present; missing, replaced or multiple units are refused.");
    }
}
