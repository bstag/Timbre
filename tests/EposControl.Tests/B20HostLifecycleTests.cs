using System.Text.Json;
using EposControl.Core;

internal static class B20HostLifecycleTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-06T12:00:00Z");
    private static readonly AudioEndpoint Mic = new DemoAudioBackend().Discover().First();
    private static readonly MicrophoneEffects Saved = new(37, 1, new(false, true, false, true, MicrophoneEqPresets.Clear));
    public static void Run(TestSuite suite)
    {
        void Case(string name, Action<Rig> action) => suite.Case(name, () => { using var rig = new Rig(); action(rig); });
        Case("Managed host waits for its absent B20 without loading or creating objects", r => {
            r.Audio.Devices = []; TestSuite.Assert(r.Host.Tick(Start).State == "WaitingForDevice" && r.Opens == 0 && r.Loads == 0);
            r.Audio.Devices = [Mic]; TestSuite.Assert(r.Host.Tick(Start.AddSeconds(1)).State == "Connected" && r.Opens == 1);
        });
        Case("Managed host ignores a different physical B20 and playback-only arrivals", r => {
            r.Audio.Devices = [Mic with { Usb = Mic.Usb! with { InstanceId = "OTHER" } }];
            TestSuite.Assert(r.Host.Tick(Start).State == "WaitingForDevice");
            r.Audio.Devices = [Mic with { Direction = AudioDirection.Playback }];
            TestSuite.Assert(r.Host.Tick(Start.AddSeconds(1)).State == "WaitingForDevice" && r.Opens == 0);
        });
        Case("Managed host refuses multiple physical B20 units before native access", r => {
            r.Audio.Devices = [Mic, Mic with { Id = "other", Usb = Mic.Usb! with { InstanceId = "OTHER" } }];
            TestSuite.Assert(r.Host.Tick(Start).State == "Faulted" && r.Opens == 0);
        });
        Case("Managed host refuses duplicate target microphone endpoints", r => {
            r.Audio.Devices = [Mic, Mic with { Id = "duplicate" }];
            TestSuite.Assert(r.Host.Tick(Start).State == "Faulted" && r.Opens == 0);
        });
        Case("Fresh managed objects use the explicit snapshot when automatic restore is off", r => {
            r.Store.Remember(Mic, Saved); var state = r.Host.Tick(Start);
            TestSuite.Assert(state.State == "Connected" && state.CreatedFresh && state.SettingsWrites && state.RestoreSource == "ExplicitSnapshot" &&
                state.Effects == r.Seed.Effects && !r.Store.Load(Mic)!.RestoreOnConnect && r.Store.Load(Mic)!.Effects == Saved);
        });
        Case("Saved opt-in processing takes precedence over the fresh diagnostic snapshot", r => {
            r.Enable(); var state = r.Host.Tick(Start);
            TestSuite.Assert(state.State == "Connected" && state.RestoreSource == "LastSavedProcessing" && state.Effects == Saved && r.Last!.Applies == 1);
        });
        Case("Existing objects are retained unchanged when restore is off", r => {
            r.Factory = () => new(Saved, false); var state = r.Host.Tick(Start);
            TestSuite.Assert(state.State == "Connected" && !state.CreatedFresh && !state.SettingsWrites && state.RestoreSource == "None" && r.Last!.Applies == 0 && state.Effects == Saved);
        });
        Case("Opt-in saved processing also restores through retained existing objects", r => {
            r.Enable(); r.Factory = () => new(r.Seed.Effects, false); var state = r.Host.Tick(Start);
            TestSuite.Assert(state.State == "Connected" && !state.CreatedFresh && state.RestoreSource == "LastSavedProcessing" && state.Effects == Saved);
        });
        Case("Managed polling preserves external edits and does not continually replay saved state", r => {
            r.Enable(); r.Host.Tick(Start); r.Last!.State = Saved with { GatePercent = 12 };
            r.Store.Remember(Mic, Saved with { GatePercent = 50 });
            var state = r.Host.Tick(Start.AddSeconds(1));
            TestSuite.Assert(state.Effects!.GatePercent == 12 && r.Last.Applies == 1 && r.Loads == 1 && r.Opens == 1);
        });
        Case("Disconnect releases objects; reconnect reloads the latest saved processing", r => {
            r.Enable(); r.Host.Tick(Start); var old = r.Last!; r.Audio.Devices = [];
            TestSuite.Assert(r.Host.Tick(Start.AddSeconds(1)).State == "WaitingForDevice" && old.Disposals == 1);
            r.Store.Remember(Mic, Saved with { GatePercent = 50 }); r.Audio.Devices = [Mic];
            var next = r.Host.Tick(Start.AddSeconds(2));
            TestSuite.Assert(next.State == "Connected" && next.Generation == 2 && next.Effects!.GatePercent == 50 && r.Opens == 2);
        });
        Case("New endpoint GUID and friendly name reconnect to the same physical seed", r => {
            r.Enable(); r.Host.Tick(Start); var old = r.Last!;
            r.Audio.Devices = [Mic with { Id = "new-guid", Name = "Renamed B20" }];
            var next = r.Host.Tick(Start.AddSeconds(1));
            TestSuite.Assert(next.State == "Connected" && next.Generation == 2 && old.Disposals == 1 && next.Endpoint!.Id == "new-guid");
        });
        Case("Invalid or missing diagnostic seed fails before objects open", r => {
            r.Seed = r.Seed with { DiagnosticSeed = null }; TestSuite.Assert(r.Host.Tick(Start).State == "Faulted" && r.Opens == 0);
        });
        Case("Mismatched seed identity fails before objects open", r => {
            r.Seed = r.Seed with { DeviceIdentity = "OTHER" }; TestSuite.Assert(r.Host.Tick(Start).State == "Faulted" && r.Opens == 0);
        });
        Case("Corrupt diagnostic layout fails before objects open", r => {
            var bad = r.Seed.DiagnosticSeed!.ToArray(); bad[0] = 99; r.Seed = r.Seed with { DiagnosticSeed = bad };
            TestSuite.Assert(r.Host.Tick(Start).State == "Faulted" && r.Opens == 0);
        });
        Case("Corrupt saved processing fails closed even if the native interface would work", r => {
            r.Enable(); File.WriteAllText(r.Store.StatePath(Mic), "{");
            TestSuite.Assert(r.Host.Tick(Start).State == "Faulted" && r.Opens == 0);
        });
        Case("Transport retries are limited to three failures with two-second spacing", r => {
            r.OpenError = new IOException("temporarily missing");
            TestSuite.Assert(r.Host.Tick(Start).State == "Retrying" && r.Opens == 1);
            TestSuite.Assert(r.Host.Tick(Start.AddSeconds(1)).State == "Retrying" && r.Opens == 1);
            TestSuite.Assert(r.Host.Tick(Start.AddSeconds(2)).Attempts == 2 && r.Opens == 2);
            TestSuite.Assert(r.Host.Tick(Start.AddSeconds(4)).State == "Faulted" && r.Opens == 3);
            r.Host.Tick(Start.AddSeconds(100)); TestSuite.Assert(r.Opens == 3);
        });
        Case("Discovery failures are bounded even before the first native attempt", r => {
            r.Audio.Error = new IOException("discovery unavailable");
            TestSuite.Assert(r.Host.Tick(Start).Attempts == 1);
            TestSuite.Assert(r.Host.Tick(Start.AddSeconds(2)).Attempts == 2);
            TestSuite.Assert(r.Host.Tick(Start.AddSeconds(4)).State == "Faulted" && r.Opens == 0);
        });
        Case("Transient failure can recover without exceeding retry policy", r => {
            r.OpenError = new TimeoutException("busy"); r.Host.Tick(Start); r.OpenError = null;
            TestSuite.Assert(r.Host.Tick(Start.AddSeconds(2)).State == "Connected" && r.Opens == 2);
        });
        Case("Read failure releases ownership before a bounded reconnect attempt", r => {
            r.Enable(); r.Host.Tick(Start); var old = r.Last!; old.ReadError = new IOException("lost interface");
            TestSuite.Assert(r.Host.Tick(Start.AddSeconds(1)).State == "Retrying" && old.Disposals == 1);
            TestSuite.Assert(r.Host.Tick(Start.AddSeconds(3)).State == "Connected" && r.Opens == 2);
        });
        Case("Access refusal is terminal instead of repeatedly recreating controls", r => {
            r.OpenError = new UnauthorizedAccessException("access denied");
            TestSuite.Assert(r.Host.Tick(Start).State == "Faulted" && r.Opens == 1);
        });
        Case("Failed restore closes its connection and never saves a replacement state", r => {
            r.Enable(); var before = r.Store.Load(Mic);
            r.Factory = () => new(r.Seed.Effects, true) { ApplyError = new InvalidOperationException("stale state") };
            TestSuite.Assert(r.Host.Tick(Start).State == "Faulted" && r.Last!.Disposals == 1 && r.Store.Load(Mic) == before);
        });
        Case("Mismatched managed restore readback fails closed and releases handles", r => {
            r.Enable(); r.Factory = () => new(r.Seed.Effects, true) { BadReadback = true };
            TestSuite.Assert(r.Host.Tick(Start).State == "Faulted" && r.Last!.Disposals == 1);
        });
        Case("Lifecycle shutdown is idempotent and disallows subsequent ticks", r => {
            r.Host.Tick(Start); var old = r.Last!; r.Host.Dispose(); r.Host.Dispose();
            TestSuite.Assert(old.Disposals == 1 && r.Host.Status.State == "Stopped");
            TestSuite.Throws<ObjectDisposedException>(() => r.Host.Tick(Start));
        });
        suite.Case("Host argument parsing preserves read-only mode and accepts explicit managed inputs", () => {
            var common = new[] { "--report-directory", "reports", "--stop-file", "stop", "--seconds", "30" };
            var read = ApoHostOptions.Parse(["--retain-b20", .. common]); TestSuite.Assert(read.InitialState is null && read.Mode == "--retain-b20");
            var managed = ApoHostOptions.Parse(["--manage-b20", "--initial-state", "seed", "--state-directory", "state", "--device-instance", "USB\\target", .. common]);
            TestSuite.Assert(managed.DeviceInstance == "USB\\target" && managed.StateDirectory is not null);
            foreach (var bad in new string[][] { [], ["--unknown"], ["--retain-b20", "--seconds", "30"],
                ["--retain-b20", .. common, "--state-directory", "state"], ["--initialize-b20", .. common],
                ["--manage-b20", "--initial-state", "seed", .. common], ["--retain-b20", .. common, "--seconds", "30"],
                ["--retain-b20", .. common.Take(4), "--seconds", "601"] }) TestSuite.Throws<ArgumentException>(() => ApoHostOptions.Parse(bad));
        });
        if (OperatingSystem.IsWindows()) NativeTests(suite);
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void NativeTests(TestSuite suite)
    {
        suite.Case("Managed lifecycle restores saved processing through native objects after disconnect and recreation", () => {
            using var rig = new Rig(); rig.Enable(); var name = "Local\\EposControl.Tests." + Guid.NewGuid().ToString("N");
            using var host = new B20HostLifecycle(Mic.Usb!.InstanceId, rig.Audio, rig.Store, _ => rig.Seed, (endpoint, seed) => new NativeConnection(name, endpoint, seed));
            TestSuite.Assert(host.Tick(Start) is { State: "Connected", CreatedFresh: true, RestoreSource: "LastSavedProcessing" });
            rig.Audio.Devices = []; host.Tick(Start.AddSeconds(1));
            TestSuite.Throws<WaitHandleCannotBeOpenedException>(() => { using var gone = Mutex.OpenExisting(name + "_mutex"); });
            rig.Store.Remember(Mic, Saved with { GatePercent = 50 }); rig.Audio.Devices = [Mic with { Id = "reconnected" }];
            TestSuite.Assert(host.Tick(Start.AddSeconds(2)) is { State: "Connected", Generation: 2, CreatedFresh: true, Effects.GatePercent: 50 });
            using var memory = new WindowsApoMemory.Session(name); var actual = memory.Read(); var expected = rig.Seed.DiagnosticSeed!.ToArray();
            foreach (var patch in ApoMicrophoneCodec.Prepare(expected, rig.Store.Load(Mic)!.Effects)) patch.After.CopyTo(expected, patch.Offset);
            TestSuite.Assert(actual.SequenceEqual(expected), "Restoration changed unowned configuration.");
        });
        suite.Case("Initializer owner guard excludes another control thread and releases on shutdown", () => {
            var name = "Local\\EposControl.Tests." + Guid.NewGuid().ToString("N"); using var first = B20HostOwner.AcquirePrivate(name);
            Exception? failure = null; var thread = new Thread(() => { try { using var second = B20HostOwner.AcquirePrivate(name); } catch (Exception ex) { failure = ex; } });
            thread.Start(); TestSuite.Assert(thread.Join(3000) && failure is InvalidOperationException);
            first.Dispose(); using var later = B20HostOwner.AcquirePrivate(name);
        });
        suite.Case("Abandoned helper owner lock can recover after native state will be revalidated", () => {
            var name = "Local\\EposControl.Tests." + Guid.NewGuid().ToString("N"); B20HostOwner? abandoned = null;
            var thread = new Thread(() => abandoned = B20HostOwner.AcquirePrivate(name)); thread.Start(); TestSuite.Assert(thread.Join(3000));
            using var recovered = B20HostOwner.AcquirePrivate(name);
            GC.KeepAlive(abandoned); // Do not release a mutex from its former owner's thread.
        });
    }
    private sealed class Rig : IDisposable
    {
        public MutableAudio Audio { get; } = new();
        public ProcessingStateStore Store { get; } = new(Path.Combine(Path.GetTempPath(), "EposControl.Host." + Guid.NewGuid().ToString("N")));
        public ApoStartupState Seed;
        public B20HostLifecycle Host;
        public FakeConnection? Last;
        public Func<FakeConnection>? Factory;
        public Exception? OpenError;
        public int Opens, Loads;
        public Rig()
        {
            var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "apo-memory-009f.bin"));
            Seed = new(1, Mic.ProfileIdentity, ApoMicrophoneCodec.Read(bytes), bytes, ProcessingStateStore.DeviceIdentity(Mic));
            Host = new(Mic.Usb!.InstanceId, Audio, Store, _ => { Loads++; return Seed; }, (_, _) => {
                Opens++; if (OpenError is not null) throw OpenError; return Last = Factory?.Invoke() ?? new(Seed.Effects, true);
            });
        }
        public void Enable() { Store.Remember(Mic, Saved); Store.SetRestoreOnConnect(Mic, true); }
        public void Dispose() { Host.Dispose(); if (Directory.Exists(Store.DirectoryPath)) Directory.Delete(Store.DirectoryPath, true); }
    }
    private sealed class MutableAudio : IAudioBackend
    {
        public IReadOnlyList<AudioEndpoint> Devices = [Mic]; public Exception? Error;
        public IReadOnlyList<AudioEndpoint> Discover() { if (Error is not null) throw Error; return Devices; }
        public AudioState Read(string id) => throw new NotSupportedException();
        public AudioState Apply(string id, float level, bool muted) => throw new NotSupportedException();
    }
    private sealed class FakeConnection(MicrophoneEffects state, bool fresh) : IB20HostConnection
    {
        public bool CreatedFresh => fresh; public MicrophoneEffects State = state;
        public int Applies, Disposals; public Exception? ReadError, ApplyError; public bool BadReadback;
        public MicrophoneEffects Read() { if (ReadError is not null) throw ReadError; return State; }
        public MicrophoneEffects Apply(MicrophoneEffects expected, MicrophoneEffects desired) {
            Applies++; if (ApplyError is not null) throw ApplyError;
            if (!ApoMicrophoneCodec.Matches(State, expected)) throw new InvalidOperationException("stale");
            return State = BadReadback ? desired with { GatePercent = 99 } : desired;
        }
        public void Dispose() => Disposals++;
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed class NativeConnection : IB20HostConnection
    {
        private readonly WindowsApoObjectHost owner;
        private readonly IMicrophoneEffectsBackend backend;
        private readonly AudioEndpoint endpoint;
        public bool CreatedFresh => owner.CreatedFresh;
        public NativeConnection(string name, AudioEndpoint endpoint, byte[] seed) {
            this.endpoint = endpoint; owner = WindowsApoObjectHost.OpenPrivate(name, diagnosticSeed: seed);
            backend = new ApoMicrophoneEffectsBackend(_ => OperatingSystem.IsWindows() ? new WindowsApoMemory.Session(name) : throw new PlatformNotSupportedException());
        }
        public MicrophoneEffects Read() => owner.Read();
        public MicrophoneEffects Apply(MicrophoneEffects expected, MicrophoneEffects desired) => backend.Apply(endpoint, expected, desired);
        public void Dispose() => owner.Dispose();
    }
}
