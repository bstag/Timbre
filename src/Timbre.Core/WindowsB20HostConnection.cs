using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Timbre.Core;

[SupportedOSPlatform("windows")]
public sealed class WindowsB20HostConnection : IB20HostConnection
{
    private readonly IDisposable lifetime;
    private readonly Func<MicrophoneEffects> read;
    private readonly AudioEndpoint endpoint;
    private readonly IMicrophoneEffectsBackend backend;
    public bool CreatedFresh { get; }
    private WindowsB20HostConnection(IDisposable lifetime, Func<MicrophoneEffects> read, bool fresh,
        AudioEndpoint endpoint, IMicrophoneEffectsBackend backend)
    { this.lifetime = lifetime; this.read = read; CreatedFresh = fresh; this.endpoint = endpoint; this.backend = backend; }
    public static WindowsB20HostConnection Connect(AudioEndpoint endpoint, IAudioBackend audio, byte[] seed)
    {
        WindowsApoMemory.ValidateIdentity(endpoint, audio.Discover());
        WindowsApoObjectHost.RequireSuiteStopped();
        var backend = WindowsApoMemory.Create(audio);
        try {
            var retained = WindowsApoObjectLease.RetainB20(endpoint, audio);
            return new(retained, retained.Read, false, endpoint, backend);
        } catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or FileNotFoundException) {
            // Fresh-only creation refuses partial sets/races; never repair existing memory.
            var fresh = WindowsApoObjectHost.StartFreshB20(endpoint, audio, seed);
            return new(fresh, fresh.Read, fresh.CreatedFresh, endpoint, backend);
        }
    }
    public MicrophoneEffects Read() => read();
    public MicrophoneEffects Apply(MicrophoneEffects expected, MicrophoneEffects desired)
    {
        WindowsApoObjectHost.RequireSuiteStopped();
        return backend.Apply(endpoint, expected, desired);
    }
    public void Dispose() => lifetime.Dispose();
}

[SupportedOSPlatform("windows")]
public sealed class B20HostOwner : IDisposable
{
    private readonly Mutex mutex;
    private bool disposed;
    // Keep the lock identity shared with pre-Timbre helpers to exclude competing initializers.
    public static B20HostOwner Acquire() => new("Global\\EposControl.B20Host.v1");
    internal static B20HostOwner AcquirePrivate(string name)
    {
        if (!Regex.IsMatch(name, @"\ALocal\\Timbre\.Tests\.[a-f0-9]{32}\z")) throw new ArgumentException("Private owner lock name required.");
        return new(name);
    }
    private B20HostOwner(string name)
    {
        mutex = new Mutex(false, name);
        try {
            bool owned;
            try { owned = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; } // Protected state is revalidated by the connection.
            if (!owned) throw new InvalidOperationException("Another B20 initializer/managed helper is already running.");
        } catch { mutex.Dispose(); throw; }
    }
    public void Dispose()
    {
        if (disposed) return;
        mutex.ReleaseMutex(); mutex.Dispose(); disposed = true;
    }
}
