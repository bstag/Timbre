using System.Runtime.Versioning;

namespace Timbre.Core;

[SupportedOSPlatform("windows")]
public sealed class WindowsGsxHostConnection : IGsxHostConnection
{
    private readonly IDisposable lifetime;
    private readonly AudioEndpoint microphone, playback;
    private readonly IGsxProcessingBackend backend;
    private bool disposed;
    public bool CreatedFresh { get; }

    private WindowsGsxHostConnection(IDisposable lifetime, bool fresh, AudioEndpoint microphone, AudioEndpoint playback, IAudioBackend audio)
    {
        this.lifetime = lifetime; CreatedFresh = fresh;
        this.microphone = microphone; this.playback = playback;
        backend = WindowsApoMemory.CreateGsxProcessing(audio);
    }

    public static WindowsGsxHostConnection Connect(AudioEndpoint microphone, AudioEndpoint playback, IAudioBackend audio, GsxApoStartupState startup)
    {
        startup.Validate(microphone, playback);
        WindowsApoObjectHost.RequireSuiteStopped();
        try {
            var lease = WindowsApoObjectLease.RetainGsx(microphone, playback, audio);
            return new(lease, false, microphone, playback, audio);
        } catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or FileNotFoundException) {
            // The fresh initializer refuses partial sets and objects appearing during startup.
            var host = WindowsApoObjectHost.StartFreshGsx(microphone, playback, audio, startup);
            return new(host, true, microphone, playback, audio);
        }
    }
    public GsxProcessingState Read()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return backend.Read(microphone, playback);
    }
    public GsxProcessingState Apply(GsxProcessingState expected, GsxProcessingState desired)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        WindowsApoObjectHost.RequireSuiteStopped();
        return backend.Apply(microphone, playback, expected, desired);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; lifetime.Dispose();
    }
}
