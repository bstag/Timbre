using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;

namespace Timbre.Core;

internal static class ApoSharedObjects
{
    internal static string B20Name(UsbIdentity usb)
    {
        if (!usb.VendorId.Equals("1395", StringComparison.OrdinalIgnoreCase) ||
            !usb.ProductId.Equals("009f", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Shared-object retention is currently validated for B20 only.");
        return "Global\\CF4B411F-BE2B-4D84-8106-EC27CA0F8F05_1395_009f";
    }

    // An abandoned lock is acquired by WaitOne, but its protected state is not trustworthy.
    internal static void Enter(Mutex mutex)
    {
        try {
            if (!mutex.WaitOne(1000)) throw new TimeoutException("EPOS effects are busy. Try again.");
        } catch (AbandonedMutexException) {
            mutex.ReleaseMutex();
            throw new IOException("EPOS effects mutex was abandoned. Restart the Suite before applying effects.");
        }
    }
}

// Keeps existing objects alive, without creating, resetting, notifying or changing them.
// This is a warm-session experiment, not a cold-start initializer or installed service.
[SupportedOSPlatform("windows")]
public sealed class WindowsApoObjectLease : IDisposable
{
    private Mutex? mutex;
    private MemoryMappedFile? map;
    private MemoryMappedViewAccessor? view;
    private EventWaitHandle? changed;
    private bool disposed;

    public static WindowsApoObjectLease RetainB20(AudioEndpoint endpoint, IAudioBackend audio)
    {
        WindowsApoMemory.ValidateIdentity(endpoint, audio.Discover());
        return OpenExisting(ApoSharedObjects.B20Name(endpoint.Usb!));
    }

    internal static WindowsApoObjectLease OpenExisting(string name)
    {
        var lease = new WindowsApoObjectLease();
        try {
            lease.mutex = Mutex.OpenExisting(name + "_mutex");
            lease.map = MemoryMappedFile.OpenExisting(name + "_memory", MemoryMappedFileRights.Read);
            lease.view = lease.map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (lease.view.Capacity != ApoMicrophoneCodec.MemorySize)
                throw new InvalidDataException("Unknown EPOS effects memory size.");
            lease.changed = EventWaitHandle.OpenExisting(name + "_event");
            lease.Read(); // Validate the full mapped settings before reporting readiness.
            return lease;
        } catch { lease.Dispose(); throw; }
    }

    private WindowsApoObjectLease() { }

    public MicrophoneEffects Read()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ApoSharedObjects.Enter(mutex!);
        try {
            var bytes = new byte[ApoMicrophoneCodec.MemorySize];
            if (view!.ReadArray(0, bytes, 0, bytes.Length) != bytes.Length)
                throw new IOException("Incomplete EPOS effects snapshot.");
            return ApoMicrophoneCodec.Read(bytes);
        } finally { mutex!.ReleaseMutex(); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        changed?.Dispose(); view?.Dispose(); map?.Dispose(); mutex?.Dispose();
    }
}
