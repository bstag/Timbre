namespace EposControl.Core;

public sealed class DemoAudioBackend : IAudioBackend
{
    private readonly Dictionary<string, AudioEndpoint> endpoints = new[] {
        new AudioEndpoint("demo-b20-mic", "Microphone (EPOS B20)", "EPOS B20", AudioDirection.Microphone,
            new UsbIdentity("1395", "009F", "USB\\VID_1395&PID_009F\\DEMO"), new AudioState(.42f, false, -9.5f, 3), new AudioFormat(2, 48000, 32), null),
        new AudioEndpoint("demo-gsx-speakers", "Headphones (EPOS GSX 300)", "EPOS GSX 300", AudioDirection.Playback,
            new UsbIdentity("1395", "0098", "USB\\VID_1395&PID_0098\\DEMO"), new AudioState(.60f, false, -7.4f, 3), new AudioFormat(2, 48000, 32), null),
        new AudioEndpoint("demo-gsx-mic", "Microphone (EPOS GSX 300)", "EPOS GSX 300", AudioDirection.Microphone,
            new UsbIdentity("1395", "0098", "USB\\VID_1395&PID_0098\\DEMO"), new AudioState(.59f, false, -3.2f, 3), new AudioFormat(1, 48000, 32), null)
    }.ToDictionary(e => e.Id);
    public IReadOnlyList<AudioEndpoint> Discover() => endpoints.Values.ToArray();
    public AudioState Read(string endpointId) => endpoints[endpointId].State!;
    public AudioState Apply(string endpointId, float level, bool muted)
    {
        if (!float.IsFinite(level) || level is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(level));
        var state = Read(endpointId) with { Level = level, Muted = muted };
        endpoints[endpointId] = endpoints[endpointId] with { State = state };
        return state;
    }
}
