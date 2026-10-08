using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Native = EposControl.Core.CoreAudioBackend.Native;

namespace EposControl.Core;

// Diagnostic shared-mode capture, on a dedicated STA thread. This is not a routing or DSP host.
[SupportedOSPlatform("windows")]
public sealed class WasapiMeasurementSource : IAudioMeasurementSource, IMicrophonePacketSource
{
    private Native.AudioClient? client;
    private CaptureClient? capture;
    private bool started;
    private readonly int threadId = Environment.CurrentManagedThreadId;
    public CaptureFormat Format { get; private set; } = null!;
    public WasapiMeasurementSource(AudioEndpoint endpoint) : this(endpoint, false) { }
    public static WasapiMeasurementSource OpenPlaybackLoopback(AudioEndpoint endpoint) => new(endpoint, true);
    private WasapiMeasurementSource(AudioEndpoint endpoint, bool loopback)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Create and use microphone capture on an STA thread.");
        if (endpoint.Direction != (loopback ? AudioDirection.Playback : AudioDirection.Microphone))
            throw new InvalidOperationException(loopback ? "Select a playback endpoint for loopback analysis." : "Select a microphone endpoint.");
        var enumerator = Native.CreateEnumerator();
        try {
            Native.Check(enumerator.GetDevice(endpoint.Id, out var device));
            try {
                var iid = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
                Native.Check(device.Activate(ref iid, 23, IntPtr.Zero, out var obj));
                client = (Native.AudioClient)obj;
                Native.Check(client.GetMixFormat(out var pointer));
                try {
                    var length = checked(18 + (ushort)Marshal.ReadInt16(pointer, 16));
                    if (length > 4096) throw new InvalidDataException("Unexpected capture format size.");
                    var data = new byte[length]; Marshal.Copy(pointer, data, 0, length); Format = CaptureFormat.Parse(data);
                    Native.Check(client.Initialize(0, loopback ? 0x00020000u : 0u, 1_000_000, 0, pointer, IntPtr.Zero));
                } finally { Marshal.FreeCoTaskMem(pointer); }
                iid = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
                Native.Check(client.GetService(ref iid, out obj)); capture = (CaptureClient)obj;
                Native.Check(client.Start()); started = true;
            } finally { Native.Release(device); }
        } catch { Dispose(); throw; }
        finally { Native.Release(enumerator); }
    }
    public AudioMeasurement Measure(TimeSpan duration)
    {
        CheckThread(); ObjectDisposedException.ThrowIf(capture is null, this);
        if (duration.TotalSeconds is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(duration));
        // Drain queued audio and allow the changed processing to settle before each measurement.
        Collect(TimeSpan.FromMilliseconds(400), null);
        var meter = new AudioMeter(Format); Collect(duration, meter); return meter.Result();
    }
    private void Collect(TimeSpan duration, AudioMeter? meter)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < duration) {
            var packets = ReadPending((data, frames, flags) => meter?.AddPacket(data.Span, frames, flags));
            if (packets == 0) Thread.Sleep(5);
        }
    }
    public int ReadPending(Action<ReadOnlyMemory<byte>, int, uint> consume)
    {
        CheckThread(); ObjectDisposedException.ThrowIf(capture is null, this);
        var packets = 0;
        // Bound work per poll so cancellation can run even if a producer continually fills packets.
        while (packets < 64) {
            Native.Check(capture.GetNextPacketSize(out var available));
            if (available == 0) break;
            Native.Check(capture.GetBuffer(out var pointer, out var frames, out var flags, out _, out _));
            try {
                if (frames != 0) {
                    var length = checked((int)frames * Format.BlockAlign);
                    if (length > Format.SampleRate * Format.BlockAlign) throw new InvalidDataException("Capture packet exceeds one second.");
                    var data = (flags & 2) != 0 ? [] : new byte[length];
                    if (data.Length > 0) { if (pointer == IntPtr.Zero) throw new InvalidDataException("Missing capture samples."); Marshal.Copy(pointer, data, 0, data.Length); }
                    consume(data, (int)frames, flags);
                }
            } finally { Native.Check(capture.ReleaseBuffer(frames)); }
            packets++;
        }
        return packets;
    }
    private void CheckThread() { if (Environment.CurrentManagedThreadId != threadId) throw new InvalidOperationException("Use capture on its owning thread."); }
    public void Dispose()
    {
        CheckThread();
        try { if (started && client is not null) Native.Check(client.Stop()); }
        finally { Native.Release(capture); capture = null; Native.Release(client); client = null; }
    }
    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface CaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong position, out ulong timestamp);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
