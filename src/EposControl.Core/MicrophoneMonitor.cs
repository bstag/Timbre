using System.Diagnostics;

namespace EposControl.Core;

public interface IMicrophonePacketSource : IDisposable
{
    CaptureFormat Format { get; }
    int ReadPending(Action<ReadOnlyMemory<byte>, int, uint> consume);
}
public interface IMicrophoneMonitor : IDisposable
{
    MicrophoneMonitorFrame? Latest { get; }
    string? Error { get; }
}

// All capture handles are opened, used and released on their owning thread. UI polls immutable summaries.
public sealed class MicrophoneMonitor : IMicrophoneMonitor
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly Thread worker;
    private MicrophoneMonitorFrame? latest;
    private string? error;
    private int disposed;
    public MicrophoneMonitorFrame? Latest => Volatile.Read(ref latest);
    public string? Error => Volatile.Read(ref error);
    public MicrophoneMonitor(Func<IMicrophonePacketSource> open)
    {
        worker = new Thread(() => Run(open)) { IsBackground = true, Name = "EPOS microphone activity" };
        if (OperatingSystem.IsWindows()) worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }
    private void Run(Func<IMicrophonePacketSource> open)
    {
        MicrophoneSpectrum? spectrum = null;
        try {
            if (cancellation.IsCancellationRequested) return;
            using var source = open();
            spectrum = new(source.Format);
            var clock = Stopwatch.StartNew(); var next = TimeSpan.Zero;
            while (!cancellation.IsCancellationRequested) {
                var packets = source.ReadPending((data, frames, flags) => spectrum.AddPacket(data.Span, frames, flags));
                if (packets > 0 && clock.Elapsed >= next) {
                    var frame = spectrum.Snapshot();
                    if (!cancellation.IsCancellationRequested) Volatile.Write(ref latest, frame);
                    next = clock.Elapsed + TimeSpan.FromMilliseconds(100);
                }
                if (cancellation.Token.WaitHandle.WaitOne(10)) break;
            }
        } catch (Exception ex) { Volatile.Write(ref error, ex.Message); }
        finally { spectrum?.Clear(); cancellation.Dispose(); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        // A slow native call must not block device selection or window shutdown indefinitely.
        worker.Join(250); Volatile.Write(ref latest, null);
    }
}

public sealed class DemoMicrophoneMonitor : IMicrophoneMonitor
{
    private bool disposed;
    private long sequence;
    public string? Error => null;
    public MicrophoneMonitorFrame? Latest => disposed ? null : new(++sequence, DateTimeOffset.UtcNow, -27, -18,
        Array.AsReadOnly<double?>(Enumerable.Range(0, 9).Select(i => (double?)(-32 - i * 4 + Math.Sin(sequence / 3d + i) * 4)).ToArray()), false, 0, 0);
    public void Dispose() => disposed = true;
}
