using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using EposControl.Core;

// Explicit diagnostic: samples are reduced to metrics immediately, never saved or played.
internal static class MonitorPacketDiagnostics
{
    [SupportedOSPlatform("windows")]
    public static int Run(string[] args)
    {
        string Value(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : throw new ArgumentException("Missing " + name); }
        var product = Value("--monitor-packets").ToUpperInvariant();
        var report = Path.GetFullPath(Value("--report"));
        if (product is not ("0098" or "009F")) throw new ArgumentException("Select B20 009f or GSX 0098 microphone.");
        var result = 1;
        var thread = new Thread(() => {
            var events = new List<object>(); var intervals = new List<double>();
            long packets = 0, frames = 0; var flagged = 0; var laterFlags = 0; var timestampFlags = 0;
            MicrophoneMonitorFrame? latest = null; CaptureFormat? format = null; string? error = null;
            try {
                var audio = new CoreAudioBackend();
                var endpoint = audio.Discover().Single(e => e.Direction == AudioDirection.Microphone && e.Usb is { } usb && usb.ProductId.Equals(product, StringComparison.OrdinalIgnoreCase) && usb.VendorId == "1395");
                var before = audio.Discover().ToDictionary(e => e.Id, e => audio.Read(e.Id));
                using var source = new WasapiMeasurementSource(endpoint); format = source.Format; var spectrum = new MicrophoneSpectrum(format);
                var clock = Stopwatch.StartNew(); double lastPacket = 0; double nextAnalysis = 0;
                while (clock.Elapsed < TimeSpan.FromSeconds(3)) {
                    source.ReadPending((data, count, flags) => {
                        packets++; frames += count;
                        var now = clock.Elapsed.TotalMilliseconds;
                        if (packets > 1) intervals.Add(now - lastPacket); lastPacket = now;
                        if ((flags & 5) != 0) {
                            flagged++; if (packets > 1) laterFlags++; if ((flags & 4) != 0) timestampFlags++;
                            if (events.Count < 32) events.Add(new { Packet = packets, AtMilliseconds = now, Frames = count, Flags = flags });
                        }
                        spectrum.AddPacket(data.Span, count, flags);
                    });
                    if (clock.Elapsed.TotalMilliseconds >= nextAnalysis) { latest = spectrum.Snapshot(); nextAnalysis = clock.Elapsed.TotalMilliseconds + 100; }
                    Thread.Sleep(10);
                }
                foreach (var entry in before) if (audio.Read(entry.Key) != entry.Value) throw new IOException("Windows control changed during packet diagnostics.");
                if (packets == 0 || latest is null) throw new IOException("No complete live analysis window received.");
                if (args.Contains("--assert-initial-flag-classified") && laterFlags == 0 && timestampFlags == 0 && latest.Discontinuities != 0)
                    throw new InvalidDataException("Initial-only capture flag is reported as an ongoing stream interruption.");
                result = 0;
            } catch (Exception ex) { error = ex.ToString(); }
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = result == 0, Product = product, Format = format, Packets = packets, Frames = frames,
                FlaggedPackets = flagged, FlaggedAfterFirstPacket = laterFlags, TimestampFlaggedPackets = timestampFlags,
                FlagEvents = events, MaximumObservedPacketGapMilliseconds = intervals.Count == 0 ? 0 : intervals.Max(), Latest = latest,
                AudioSaved = false, AudioPlayed = false, SettingsWrites = false, ListeningValidated = false, Error = error }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Monitor packets {product}: {packets} packets; {flagged} flagged ({laterFlags} after first); timestamp flags {timestampFlags}; analyzer interruptions {latest?.Discontinuities}. Report: {report}");
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return result;
    }
}
