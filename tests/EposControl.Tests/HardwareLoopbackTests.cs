using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using EposControl.Core;
using Native = EposControl.Core.CoreAudioBackend.Native;

[SupportedOSPlatform("windows")]
internal static class HardwareLoopbackTests
{
    internal static int Run(string[] args)
    {
        var reportIndex = Array.IndexOf(args, "--report");
        if (reportIndex < 0 || reportIndex + 1 >= args.Length) { Console.Error.WriteLine("Missing --report path"); return 1; }
        var reportPath = Path.GetFullPath(args[reportIndex + 1]);
        var result = 1;
        var thread = new Thread(() => {
            var report = reportPath;
            AudioEndpoint? endpoint = null; MicrophoneMonitorFrame? frame = null; long frames = 0; string? error = null;
            var preserved = false;
            try {
                var audio = new CoreAudioBackend();
                endpoint = audio.Discover().Single(e => e.Direction == AudioDirection.Playback && e.Usb is { VendorId: "1395", ProductId: "0098" });
                var audioBefore = audio.Discover().ToDictionary(e => e.Id, e => audio.Read(e.Id));
                using (var source = WasapiMeasurementSource.OpenPlaybackLoopback(endpoint)) {
                    var spectrum = new MicrophoneSpectrum(source.Format);
                    using var tone = new QuietTone(endpoint);
                    var clock = Stopwatch.StartNew();
                    while (clock.Elapsed < TimeSpan.FromSeconds(2)) {
                        tone.Fill();
                        source.ReadPending((data, count, flags) => { frames += count; spectrum.AddPacket(data.Span, count, flags); });
                        Thread.Sleep(5);
                    }
                    frame = spectrum.Snapshot();
                }
                preserved = audioBefore.All(p => audio.Read(p.Key) == p.Value);
                TestSuite.Assert(preserved, "Loopback test changed Windows level/mute");
                TestSuite.Assert(frames >= 24000 && frame is not null && !frame.Clipping && frame.InvalidSamples == 0, "No usable loopback frame");
                var dominant = frame!.BandRmsDbFs.Select((v, i) => (Value: v ?? -120, Index: i)).MaxBy(v => v.Value);
                TestSuite.Assert(dominant.Index == 4 && frame.RmsDbFs > -90, "Quiet 1 kHz test signal not detected; competing audio or a muted output may make this check inconclusive");
                result = 0;
            } catch (Exception ex) { error = ex.ToString(); }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = result == 0, Endpoint = endpoint?.Name, Frames = frames, Frame = frame,
                WindowsControlsPreserved = preserved, ToneHz = 1000, TonePeakAmplitude = .005, DurationSeconds = 2,
                RecordingSaved = false, DspPositionVerified = false, Error = error }, new JsonSerializerOptions { WriteIndented = true }));
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        Console.WriteLine($"GSX loopback {(result == 0 ? "PASS" : "FAIL")}; report: {reportPath}"); return result;
    }
    // Explicit diagnostic only: quiet generated tone on the selected endpoint, with no default-device changes.
    private sealed class QuietTone : IDisposable
    {
        private Native.AudioClient? client;
        private RenderClient? render;
        private CaptureFormat format = null!;
        private uint capacity;
        private long position;
        private bool started;
        internal QuietTone(AudioEndpoint endpoint)
        {
            var enumerator = Native.CreateEnumerator();
            try {
                Native.Check(enumerator.GetDevice(endpoint.Id, out var device));
                try {
                    var iid = typeof(Native.AudioClient).GUID; Native.Check(device.Activate(ref iid, 23, IntPtr.Zero, out var obj)); client = (Native.AudioClient)obj;
                    Native.Check(client.GetMixFormat(out var pointer));
                    try {
                        var size = checked(18 + (ushort)Marshal.ReadInt16(pointer, 16));
                        if (size > 4096) throw new InvalidDataException("Unknown output format");
                        var bytes = new byte[size]; Marshal.Copy(pointer, bytes, 0, size); format = CaptureFormat.Parse(bytes);
                        if (!format.FloatingPoint || format.Bits != 32 || format.Channels != 2) throw new InvalidDataException("Quiet GSX probe requires stereo float32 output");
                        Native.Check(client.Initialize(0, 0, 1_000_000, 0, pointer, IntPtr.Zero));
                    } finally { Marshal.FreeCoTaskMem(pointer); }
                    Native.Check(client.GetBufferSize(out capacity));
                    iid = typeof(RenderClient).GUID; Native.Check(client.GetService(ref iid, out obj)); render = (RenderClient)obj;
                    Fill(); Native.Check(client.Start()); started = true;
                } finally { Native.Release(device); }
            } catch { Dispose(); throw; }
            finally { Native.Release(enumerator); }
        }
        internal void Fill()
        {
            Native.Check(client!.GetCurrentPadding(out var padding));
            if (padding > capacity) throw new InvalidDataException("Output padding exceeds buffer capacity");
            var available = capacity - padding;
            if (available == 0) return;
            Native.Check(render!.GetBuffer(available, out var pointer)); var released = false;
            try {
                var samples = new float[checked((int)available * format.Channels)];
                for (var i = 0; i < available; i++, position++) {
                    var sample = (float)(.005 * Math.Sin(2 * Math.PI * 1000 * position / format.SampleRate));
                    for (var channel = 0; channel < format.Channels; channel++) samples[i * format.Channels + channel] = sample;
                }
                Marshal.Copy(samples, 0, pointer, samples.Length);
                released = true; Native.Check(render.ReleaseBuffer(available, 0));
            } finally { if (!released) Native.Check(render.ReleaseBuffer(available, 2)); }
        }
        public void Dispose()
        {
            try { if (started) { Native.Check(client!.Stop()); started = false; } }
            finally { Native.Release(render); render = null; Native.Release(client); client = null; }
        }
    }
    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface RenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }
}
