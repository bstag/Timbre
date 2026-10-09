namespace Timbre.Core;

// Volatile replay state for one supervised session. It never writes a named save
// or restore preference; opted-in saved settings retain precedence in the host.
public sealed class SessionSeedCache
{
    private GsxApoStartupState? gsx;
    private ApoStartupState? b20;
    public void ObserveGsx(AudioEndpoint microphone, AudioEndpoint playback, byte[] bytes)
    {
        var state = GsxProcessingState.Read(bytes);
        var seed = new GsxApoStartupState(1, microphone.Usb!.InstanceId, state.Microphone, state.Playback, (byte[])bytes.Clone());
        seed.Validate(microphone, playback); gsx = seed;
    }
    public void ObserveB20(AudioEndpoint microphone, byte[] bytes)
    {
        var seed = new ApoStartupState(1, microphone.ProfileIdentity, ApoMicrophoneCodec.Read(bytes), (byte[])bytes.Clone(), ProcessingStateStore.DeviceIdentity(microphone));
        seed.ValidateForB20(microphone); b20 = seed;
    }
    public GsxApoStartupState Gsx(string path, AudioEndpoint microphone, AudioEndpoint playback)
    {
        var seed = gsx ?? GsxApoStartupState.Load(path, microphone, playback); seed.Validate(microphone, playback);
        return seed with { DiagnosticSeed = (byte[])seed.DiagnosticSeed.Clone() };
    }
    public ApoStartupState B20(string path, AudioEndpoint microphone)
    {
        var seed = b20 ?? ApoStartupState.LoadForB20(path, microphone); seed.ValidateForB20(microphone);
        return seed with { DiagnosticSeed = seed.DiagnosticSeed is null ? null : (byte[])seed.DiagnosticSeed.Clone() };
    }
}
