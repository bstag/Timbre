using Timbre.Core;
using static GsxProcessingStateTests;

internal static class GsxHostLifecycleTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UtcNow;
    public static void Run(TestSuite suite)
    {
        foreach (var devices in new IReadOnlyList<AudioEndpoint>[] { [], [Mic], [Output] }) suite.Case("Managed GSX waits for its complete endpoint pair (" + devices.Count + ":" + devices.FirstOrDefault()?.Direction + ")", () => {
            using var rig = new Rig(); rig.Audio.Devices = devices;
            TestSuite.Assert(rig.Host.Tick(Start).State == "WaitingForDevice" && rig.Opens == 0 && rig.Loads == 0);
        });
        suite.Case("Managed GSX ignores B20 and a different target instance without opening state", () => {
            using var rig = new Rig("ABSENT");
            rig.Audio.Devices = new DemoAudioBackend().Discover();
            TestSuite.Assert(rig.Host.Tick(Start).State == "WaitingForDevice" && rig.Opens == 0 && rig.Loads == 0);
        });
        foreach (var duplicate in new[] { Mic, Output, Mic with { Id = "other", Usb = Mic.Usb! with { InstanceId = "OTHER" } } })
            suite.Case("Managed GSX refuses ambiguous pair " + duplicate.Id, () => {
                using var rig = new Rig(); rig.Audio.Devices = [Mic, Output, duplicate];
                TestSuite.Assert(rig.Host.Tick(Start).State == "Faulted" && rig.Opens == 0);
            });
        suite.Case("Managed GSX fresh start preserves explicit snapshot without saved restore opt-in", () => {
            using var rig = new Rig(); rig.Store.Remember(Mic, Output, Desired(rig.State));
            TestSuite.Assert(rig.Host.Tick(Start) is { State: "Connected", CreatedFresh: true, SettingsWrites: true, RestoreSource: "ExplicitSnapshot", Generation: 1 } status
                && status.Effects!.Matches(rig.State) && status.PlaybackEndpoint == Output);
        });
        suite.Case("Managed GSX fresh start restores both saved pages when opted in", () => {
            using var rig = new Rig(); rig.Enable();
            TestSuite.Assert(rig.Host.Tick(Start) is { State: "Connected", CreatedFresh: true, RestoreSource: "LastSavedProcessing" } status
                && status.Effects!.Matches(Desired(rig.State)) && rig.Last!.Applies == 1);
        });
        suite.Case("Managed GSX retained connection stays unchanged when automatic restore is off", () => {
            using var rig = new Rig(); rig.Store.Remember(Mic, Output, Desired(rig.State)); rig.Fresh = false;
            TestSuite.Assert(rig.Host.Tick(Start) is { State: "Connected", SettingsWrites: false, RestoreSource: "None" } status
                && status.Effects!.Matches(rig.State) && rig.Last!.Applies == 0);
        });
        suite.Case("Managed GSX health polling keeps subsequent external edits instead of replaying saves", () => {
            using var rig = new Rig(); rig.Enable(); rig.Host.Tick(Start);
            var external = rig.State with { Microphone = rig.State.Microphone with { GatePercent = 12 } };
            rig.Last!.State = external;
            TestSuite.Assert(rig.Host.Tick(Start.AddSeconds(1)).Effects == external && rig.Last.Applies == 1 && rig.Opens == 1);
        });
        suite.Case("Managed GSX losing either page releases lease and restores latest saved pair on reconnect", () => {
            using var rig = new Rig(); rig.Enable(); rig.Host.Tick(Start); var prior = rig.Last!;
            rig.Audio.Devices = [Mic]; TestSuite.Assert(rig.Host.Tick(Start.AddSeconds(1)).State == "WaitingForDevice" && prior.Disposals == 1);
            var next = Desired(rig.State) with { Playback = rig.State.Playback with { SurroundEnabled = false } }; rig.Store.Remember(Mic, Output, next);
            rig.Audio.Devices = [Mic with { Id = "new-mic" }, Output with { Id = "new-output" }];
            TestSuite.Assert(rig.Host.Tick(Start.AddSeconds(2)) is { State: "Connected", Generation: 2 } status && status.Effects!.Matches(next));
        });
        suite.Case("Managed GSX playback endpoint replacement reconnects even when microphone ID stays fixed", () => {
            using var rig = new Rig(); rig.Host.Tick(Start); var prior = rig.Last!;
            rig.Audio.Devices = [Mic, Output with { Id = "new-output", Name = "renamed" }];
            TestSuite.Assert(rig.Host.Tick(Start.AddSeconds(1)) is { State: "Connected", Generation: 2 } && prior.Disposals == 1 && rig.Opens == 2);
        });
        suite.Case("Managed GSX validates corrupt saved state before creating native objects", () => {
            using var rig = new Rig(); rig.Enable(); File.WriteAllText(rig.Store.StatePath(Mic, Output), "{}");
            TestSuite.Assert(rig.Host.Tick(Start).State == "Faulted" && rig.Opens == 0);
        });
        suite.Case("Managed GSX validates snapshot identity before connecting", () => {
            using var rig = new Rig(); rig.Startup = rig.Startup with { DeviceInstance = "OTHER" };
            TestSuite.Assert(rig.Host.Tick(Start).State == "Faulted" && rig.Opens == 0);
        });
        suite.Case("Managed GSX transient connection failures retry at most three times two seconds apart", () => {
            using var rig = new Rig(); rig.OpenError = new IOException("transport");
            TestSuite.Assert(rig.Host.Tick(Start) is { State: "Retrying", Attempts: 1 });
            rig.Host.Tick(Start.AddSeconds(1)); TestSuite.Assert(rig.Opens == 1);
            TestSuite.Assert(rig.Host.Tick(Start.AddSeconds(2)) is { State: "Retrying", Attempts: 2 });
            TestSuite.Assert(rig.Host.Tick(Start.AddSeconds(4)) is { State: "Faulted", Attempts: 3 });
            rig.OpenError = null; rig.Host.Tick(Start.AddSeconds(8)); TestSuite.Assert(rig.Opens == 3);
        });
        suite.Case("Managed GSX access failure is terminal without retry or persistence changes", () => {
            using var rig = new Rig(); rig.Enable(); var saved = rig.Store.Load(Mic, Output); rig.OpenError = new UnauthorizedAccessException();
            TestSuite.Assert(rig.Host.Tick(Start).State == "Faulted" && rig.Store.Load(Mic, Output) == saved && rig.Opens == 1);
        });
        suite.Case("Managed GSX rejects partial or wrong readback, closes lease and keeps saved state", () => {
            using var rig = new Rig(); rig.Enable(); var saved = rig.Store.Load(Mic, Output); rig.BadReadback = true;
            TestSuite.Assert(rig.Host.Tick(Start).State == "Faulted" && rig.Last!.Disposals == 1 && rig.Store.Load(Mic, Output) == saved);
        });
        suite.Case("Managed GSX health failure releases lease then reconnects after bounded delay", () => {
            using var rig = new Rig(); rig.Host.Tick(Start); var prior = rig.Last!; prior.ReadError = new IOException("lost");
            TestSuite.Assert(rig.Host.Tick(Start.AddSeconds(1)).State == "Retrying" && prior.Disposals == 1);
            rig.Host.Tick(Start.AddSeconds(2)); TestSuite.Assert(rig.Opens == 1);
            TestSuite.Assert(rig.Host.Tick(Start.AddSeconds(3)) is { State: "Connected", Generation: 2 });
        });
        suite.Case("Managed GSX disposal releases connection once and refuses subsequent ticks", () => {
            using var rig = new Rig(); rig.Host.Tick(Start); rig.Host.Dispose(); rig.Host.Dispose();
            TestSuite.Assert(rig.Last!.Disposals == 1 && rig.Host.Status.State == "Stopped");
            TestSuite.Throws<ObjectDisposedException>(() => rig.Host.Tick(Start));
        });
        suite.Case("Managed GSX command requires explicit instance, saved-state directory and diagnostic input", () => {
            string[] args = ["--manage-gsx", "--initial-state", "snapshot.json", "--state-directory", "state", "--device-instance", "TARGET", "--report-directory", "report", "--stop-file", "stop", "--seconds", "30"];
            TestSuite.Assert(ApoHostOptions.Parse(args) is { Mode: "--manage-gsx", DeviceInstance: "TARGET", Seconds: 30 });
            foreach (var key in new[] { "--initial-state", "--state-directory", "--device-instance" }) {
                var index = Array.IndexOf(args, key); var missing = args.Take(index).Concat(args.Skip(index + 2)).ToArray();
                TestSuite.Throws<ArgumentException>(() => ApoHostOptions.Parse(missing));
            }
        });
        if (OperatingSystem.IsWindows()) NativeTests(suite);
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void NativeTests(TestSuite suite)
    {
        suite.Case("Managed GSX restores both saved pages through actual native objects after removal/recreation", () => {
            using var rig = new Rig(); rig.Enable(); var name = "Local\\Timbre.Tests." + Guid.NewGuid().ToString("N");
            using var host = new GsxHostLifecycle(Mic.Usb!.InstanceId, rig.Audio, rig.Store, (_, _) => rig.Startup,
                (mic, output, seed) => new NativeConnection(name, mic, output, seed));
            TestSuite.Assert(host.Tick(Start) is { State: "Connected", CreatedFresh: true, RestoreSource: "LastSavedProcessing" });
            rig.Audio.Devices = []; host.Tick(Start.AddSeconds(1));
            TestSuite.Throws<WaitHandleCannotBeOpenedException>(() => { using var mutex = Mutex.OpenExisting(name + "_mutex"); });
            var desired = Desired(rig.State) with { Microphone = rig.State.Microphone with { GatePercent = 50 } }; rig.Store.Remember(Mic, Output, desired);
            rig.Audio.Devices = [Mic with { Id = "new-mic" }, Output with { Id = "new-output" }];
            TestSuite.Assert(host.Tick(Start.AddSeconds(2)) is { State: "Connected", CreatedFresh: true, Generation: 2 } status && status.Effects!.Matches(desired));
            using var session = new WindowsApoMemory.Session(name); TestSuite.Assert(session.Read().SequenceEqual(Patched(Seed(), desired)));
        });
    }
    private sealed class Rig : IDisposable
    {
        public readonly Audio Audio = new([Mic, Output]);
        public readonly GsxProcessingStateStore Store = new(Path.Combine(Path.GetTempPath(), "Timbre.GsxHost." + Guid.NewGuid().ToString("N")));
        public readonly GsxProcessingState State = GsxProcessingState.Read(Seed());
        public GsxApoStartupState Startup;
        public GsxHostLifecycle Host;
        public FakeConnection? Last; public bool Fresh = true, BadReadback; public Exception? OpenError;
        public int Loads, Opens;
        public Rig(string? target = null) {
            Startup = new(1, Mic.Usb!.InstanceId, State.Microphone, State.Playback, Seed());
            Host = new(target ?? Mic.Usb.InstanceId, Audio, Store, (_, _) => { Loads++; return Startup; }, (_, _, _) => {
                Opens++; if (OpenError is not null) throw OpenError;
                return Last = new(State, Fresh) { BadReadback = BadReadback };
            });
        }
        public void Enable() { Store.Remember(Mic, Output, Desired(State)); Store.SetRestoreOnConnect(Mic, Output, true); }
        public void Dispose() { Host.Dispose(); if (Directory.Exists(Store.DirectoryPath)) Directory.Delete(Store.DirectoryPath, true); }
    }
    private sealed class FakeConnection(GsxProcessingState state, bool fresh) : IGsxHostConnection
    {
        public bool CreatedFresh => fresh; public GsxProcessingState State = state; public int Applies, Disposals;
        public bool BadReadback; public Exception? ReadError;
        public GsxProcessingState Read() { if (ReadError is not null) throw ReadError; return State; }
        public GsxProcessingState Apply(GsxProcessingState expected, GsxProcessingState desired) {
            Applies++; if (!State.Matches(expected)) throw new InvalidOperationException("stale");
            return State = BadReadback ? desired with { Playback = desired.Playback with { Equalizer = null } } : desired;
        }
        public void Dispose() => Disposals++;
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed class NativeConnection : IGsxHostConnection
    {
        private readonly WindowsApoObjectHost host;
        private readonly IGsxProcessingBackend backend;
        private readonly AudioEndpoint mic, output;
        public bool CreatedFresh => host.CreatedFresh;
        public NativeConnection(string name, AudioEndpoint mic, AudioEndpoint output, GsxApoStartupState seed) {
            this.mic = mic; this.output = output; host = WindowsApoObjectHost.OpenPrivate(name, true, seed.DiagnosticSeed, true);
            backend = new ApoGsxProcessingBackend((_, _) => OperatingSystem.IsWindows() ? new WindowsApoMemory.Session(name, (o, l) => WindowsApoMemory.ValidateGsxPatch(mic, o, l)) : throw new PlatformNotSupportedException());
        }
        public GsxProcessingState Read() => backend.Read(mic, output);
        public GsxProcessingState Apply(GsxProcessingState expected, GsxProcessingState desired) => backend.Apply(mic, output, expected, desired);
        public void Dispose() => host.Dispose();
    }
}
