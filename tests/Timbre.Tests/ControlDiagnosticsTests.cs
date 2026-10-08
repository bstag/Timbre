using Timbre.Core;

internal static class ControlDiagnosticsTests
{
    private sealed class Rig : IAudioBackend, IMicrophoneEffectsBackend, IPlaybackEffectsBackend, ISidetoneBackend
    {
        public IReadOnlyList<AudioEndpoint> Endpoints = new DemoAudioBackend().Discover();
        public Exception? MicrophoneFailure, PlaybackFailure, AudioFailure;
        public int Writes;
        public List<string> MicReads = [], SoundReads = [], SideReads = [];
        private readonly DemoMicrophoneEffectsBackend mic = new();
        private readonly DemoPlaybackEffectsBackend sound = new();
        private readonly DemoSidetoneBackend side = new();
        public IReadOnlyList<AudioEndpoint> Discover() => Endpoints;
        public AudioState Read(string id) => AudioFailure is { } ex ? throw ex : Endpoints.Single(e => e.Id == id).State!;
        public AudioState Apply(string id, float level, bool muted) { Writes++; throw new Exception("Diagnostics must not write"); }
        MicrophoneEffects IMicrophoneEffectsBackend.Read(AudioEndpoint e) { MicReads.Add(e.Id); if (MicrophoneFailure is { } ex) throw ex; return mic.Read(e); }
        PlaybackEffects IPlaybackEffectsBackend.Read(AudioEndpoint e) { SoundReads.Add(e.Id); if (PlaybackFailure is { } ex) throw ex; return sound.Read(e); }
        SidetoneState ISidetoneBackend.Read(AudioEndpoint e) { SideReads.Add(e.Id); return side.Read(e); }
        MicrophoneEffects IMicrophoneEffectsBackend.Apply(AudioEndpoint e, MicrophoneEffects before, MicrophoneEffects after) { Writes++; throw new Exception("Diagnostics must not write"); }
        PlaybackEffects IPlaybackEffectsBackend.Apply(AudioEndpoint e, PlaybackEffects before, PlaybackEffects after) { Writes++; throw new Exception("Diagnostics must not write"); }
        SidetoneState ISidetoneBackend.Apply(AudioEndpoint e, SidetoneState before, SidetoneSettings after) { Writes++; throw new Exception("Diagnostics must not write"); }
        public ControlDiagnosticReport Collect() => new ControlDiagnostics(this, new DeviceCatalog([
            new("1395", "009f", "B20", "Frost", true, true, false), new("1395", "0098", "GSX", "Boston", true, true, true)]), this, this, this).Collect();
    }
    public static void Run(TestSuite suite)
    {
        suite.Case("Diagnostics probes mapped controls without writing, HID queries or capture", () => {
            var rig = new Rig(); var report = rig.Collect();
            TestSuite.Assert(report.SchemaVersion == 1 && !report.SettingsWrites && !report.HardwareStatusQuerySent && !report.AudioCaptureStarted && !report.Demo);
            TestSuite.Assert(report.Endpoints.Count == 3 && report.Controls.Count == 3 && rig.Writes == 0);
            TestSuite.Assert(rig.MicReads.SequenceEqual(new[] { "demo-b20-mic", "demo-gsx-mic" }) && rig.SoundReads.SequenceEqual(new[] { "demo-gsx-speakers" }) && rig.SideReads.SequenceEqual(new[] { "demo-b20-mic" }));
            var gsx = report.Controls.Single(c => c.EndpointId == "demo-gsx-mic");
            TestSuite.Assert(gsx.Microphone.Status == "Available" && gsx.Sidetone.Status == "NotSupported" && gsx.Sidetone.Value is null && gsx.Sidetone.Reason!.Contains("do not send USB reports"));
        });
        suite.Case("Missing processor diagnostics distinguish supported controls from missing startup interface", () => {
            var rig = new Rig { MicrophoneFailure = new WaitHandleCannotBeOpenedException("missing mutex") }; var report = rig.Collect();
            TestSuite.Assert(report.Controls.Where(c => c.Microphone.Status == "Unavailable").Count() == 2);
            TestSuite.Assert(report.Controls[0].Microphone.Value is null && report.Controls[0].Microphone.Reason!.Contains("finish starting") && report.Controls[0].Microphone.ErrorType == "WaitHandleCannotBeOpenedException");
            TestSuite.Assert(report.Controls.Single(c => c.EndpointId == "demo-gsx-speakers").Playback.Status == "Available" && rig.Writes == 0);
        });
        suite.Case("A failed playback probe preserves independent microphone diagnostics", () => {
            var report = new Rig { PlaybackFailure = new InvalidDataException("header changed") }.Collect();
            TestSuite.Assert(report.Controls[1].Playback.Status == "Unavailable" && report.Controls[1].Playback.Reason!.Contains("incompatible"));
            TestSuite.Assert(report.Controls[0].Microphone.Status == "Available" && report.Controls[2].Microphone.Status == "Available");
        });
        suite.Case("Audio read errors remove available level flags without dropping the endpoint", () => {
            var report = new Rig { AudioFailure = new IOException("disconnected during read") }.Collect();
            TestSuite.Assert(report.Controls.All(c => c.Audio.Status == "Unavailable" && c.Audio.Value is null && c.Features.First().State != FeatureState.Ready));
            TestSuite.Assert(report.Endpoints.Count == 3);
        });
        suite.Case("Unknown models never inherit another device's processor or sidetone probes", () => {
            var rig = new Rig(); rig.Endpoints = rig.Endpoints.Select(e => e with { Usb = e.Usb! with { ProductId = "FFFF" } }).ToArray();
            var report = rig.Collect();
            TestSuite.Assert(rig.MicReads.Count == 0 && rig.SoundReads.Count == 0 && rig.SideReads.Count == 0 && rig.Writes == 0);
            TestSuite.Assert(report.Controls.All(c => c.Microphone.Status == "NotSupported" && c.Playback.Status == "NotSupported" && c.Sidetone.Status == "NotSupported"));
        });
        suite.Case("Empty discovery is a valid read-only diagnostic snapshot", () => {
            var report = new Rig { Endpoints = [] }.Collect(); TestSuite.Assert(report.Endpoints.Count == 0 && report.Controls.Count == 0);
        });
        suite.Case("Diagnostic errors retain useful permission and busy-interface reasons", () => {
            var denied = new Rig { MicrophoneFailure = new UnauthorizedAccessException("denied") }.Collect();
            var busy = new Rig { MicrophoneFailure = new TimeoutException("mutex timeout") }.Collect();
            TestSuite.Assert(denied.Controls[0].Microphone.Reason!.Contains("denied access") && busy.Controls[0].Microphone.Reason!.Contains("busy"));
        });
    }
}
