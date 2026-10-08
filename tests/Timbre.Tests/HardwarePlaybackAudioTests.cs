using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Timbre.Core;

[SupportedOSPlatform("windows")]
internal static class HardwarePlaybackAudioTests
{
    internal static int Run(string[] args)
    {
        var reportIndex = Array.IndexOf(args, "--report");
        if (reportIndex < 0 || reportIndex + 1 >= args.Length) { Console.Error.WriteLine("Missing --report path"); return 1; }
        var path = Path.GetFullPath(args[reportIndex + 1]); var exit = 1;
        var thread = new Thread(() => {
            AudioEndpoint? endpoint = null; CaptureFormat? format = null; PlaybackEqAudioReport? report = null;
            CoreAudioBackend? audio = null; Dictionary<string, AudioState>? windowsBefore = null;
            var preserved = false; var gsxEqual = false; var b20Equal = true; string? error = null;
            byte[]? b20Before = null; var spectra = new List<MicrophoneMonitorFrame?>();
            try {
                audio = new CoreAudioBackend(); var endpoints = audio.Discover();
                endpoint = endpoints.Single(e => e.Direction == AudioDirection.Playback && e.Usb is { VendorId: "1395", ProductId: "0098" });
                WindowsApoMemory.ValidatePlaybackIdentity(endpoint, endpoints);
                windowsBefore = endpoints.ToDictionary(e => e.Id, e => audio.Read(e.Id));
                var volume = audio.Read(endpoint.Id);
                if (volume.Muted || volume.Level == 0) throw new InvalidOperationException("The GSX output must be usable and unmuted before this diagnostic.");
                var playback = WindowsApoMemory.CreatePlayback(audio); var before = playback.Read(endpoint);
                if (endpoints.Any(e => e.Usb is { VendorId: "1395", ProductId: "009F" })) b20Before = HardwarePlaybackTests.Snapshot("009f");
                using (var capture = WasapiMeasurementSource.OpenPlaybackLoopback(endpoint)) {
                    format = capture.Format;
                    using var tone = new HardwareLoopbackTests.QuietTone(endpoint);
                    if (!ApoPlaybackCodec.Matches(playback.Read(endpoint), before)) throw new InvalidOperationException("Opening streams changed playback processing; no diagnostic EQ writes attempted.");
                    var gsxBefore = HardwarePlaybackTests.Snapshot("0098");
                    var source = new ToneSource(capture, tone, spectra);
                    Console.WriteLine("Measuring a quiet 1 kHz tone with flat / +6 dB / flat EQ. Keep other playback/controllers idle for about eight seconds.");
                    report = PlaybackEqAudioValidation.Run(endpoint, audio, playback, source, TimeSpan.FromSeconds(2));
                    gsxEqual = HardwarePlaybackTests.Snapshot("0098").SequenceEqual(gsxBefore);
                }
                if (report.Outcome != AudioValidationOutcome.Failed && !spectra.All(IsDominantTone))
                    report = report with { Outcome = AudioValidationOutcome.Inconclusive, Reason = "The generated 1 kHz tone did not dominate every spectrum; competing playback or missing capture prevents attribution." };
            } catch (Exception ex) { error = ex.ToString(); }
            finally {
                try {
                    preserved = windowsBefore is not null && windowsBefore.All(p => audio!.Read(p.Key) == p.Value);
                    if (b20Before is not null) b20Equal = HardwarePlaybackTests.Snapshot("009f").SequenceEqual(b20Before);
                } catch (Exception ex) { error += "\nRestoration comparison: " + ex; }
            }
            var controlsRestored = error is null && preserved && gsxEqual && b20Equal && report?.SettingsRestored == true;
            exit = !controlsRestored || report!.Outcome == AudioValidationOutcome.Failed ? 1 : report.Outcome == AudioValidationOutcome.Passed ? 0 : 2;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var options = new JsonSerializerOptions { WriteIndented = true }; options.Converters.Add(new JsonStringEnumConverter());
            File.WriteAllText(path, JsonSerializer.Serialize(new { Outcome = exit == 0 ? "Passed" : exit == 2 ? "Inconclusive" : "Failed", Endpoint = endpoint?.Name,
                OutputFormat = format, Report = report, Spectra = spectra, WindowsControlsPreserved = preserved,
                FullGsxBufferRestoredWhileStreamsOpen = gsxEqual, FullB20BufferPreserved = b20Equal, RecordingSaved = false,
                ToneHz = DiagnosticToneSignal.FrequencyHz, TonePeakAmplitude = DiagnosticToneSignal.PeakAmplitude,
                DspResponseObserved = exit == 0, SuiteIndependenceValidated = false, Error = error,
                Scope = "Selected GSX endpoint loopback; quiet generated front-pair tone; reversible 1 kHz playback EQ comparison; no service or default-device changes" }, options));
            Console.WriteLine($"Playback EQ audio: {(exit == 0 ? "Passed" : exit == 2 ? "Inconclusive" : "Failed")}. {report?.Reason} Controls restored: {controlsRestored}");
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return exit;
    }
    private static bool IsDominantTone(MicrophoneMonitorFrame? frame) => frame is not null && frame.BandRmsDbFs[4] is { } tone &&
        frame.BandRmsDbFs.Where((_, i) => i != 4).All(v => v is null || tone - v >= 10);

    private sealed class ToneSource(WasapiMeasurementSource capture, HardwareLoopbackTests.QuietTone tone,
        List<MicrophoneMonitorFrame?> spectra) : IAudioMeasurementSource
    {
        public AudioMeasurement Measure(TimeSpan duration)
        {
            Collect(TimeSpan.FromMilliseconds(400), null, null);
            var meter = new AudioMeter(capture.Format); var spectrum = new MicrophoneSpectrum(capture.Format);
            Collect(duration, meter, spectrum); tone.Fill(); spectra.Add(spectrum.Snapshot()); return meter.Result();
        }
        private void Collect(TimeSpan duration, AudioMeter? meter, MicrophoneSpectrum? spectrum)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < duration) {
                tone.Fill();
                capture.ReadPending((data, frames, flags) => { meter?.AddPacket(data.Span, frames, flags); spectrum?.AddPacket(data.Span, frames, flags); });
                Thread.Sleep(5);
            }
        }
    }
}
