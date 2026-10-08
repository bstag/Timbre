using System.Runtime.Versioning;

namespace Timbre.Core;

[SupportedOSPlatform("windows")]
public sealed class GsxHostOwner : IDisposable
{
    private readonly Mutex mutex;
    private bool disposed;
    // Keep the lock identity shared with pre-Timbre helpers to exclude competing initializers.
    public static GsxHostOwner Acquire() => new("Global\\EposControl.GsxHost.v1");
    internal static GsxHostOwner AcquirePrivate(string name)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"\ALocal\\Timbre\.Tests\.[a-f0-9]{32}\z"))
            throw new ArgumentException("Private owner lock name required.");
        return new(name);
    }
    private GsxHostOwner(string name)
    {
        mutex = new Mutex(false, name);
        try {
            bool acquired;
            try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new InvalidOperationException("Another GSX initializer/managed helper is already running.");
        } catch { mutex.Dispose(); throw; }
    }
    public void Dispose()
    {
        if (disposed) return;
        mutex.ReleaseMutex(); mutex.Dispose(); disposed = true;
    }
}
