using System.Buffers.Binary;
using System.Text.Json;
using Timbre.Core;

internal static class GsxProcessingStateTests
{
    internal static readonly AudioEndpoint Mic = new DemoAudioBackend().Discover().Single(e => e.Usb?.ProductId == "0098" && e.Direction == AudioDirection.Microphone);
    internal static readonly AudioEndpoint Output = new DemoAudioBackend().Discover().Single(e => e.Usb?.ProductId == "0098" && e.Direction == AudioDirection.Playback);
    internal static byte[] Seed() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "apo-memory-0098.bin"));
    internal static GsxProcessingState Desired(GsxProcessingState before) => new(
        before.Microphone with { GatePercent = 37, FilterLevel = 1, Processing = before.Microphone.Processing! with { EqualizerEnabled = true, Equalizer = MicrophoneEqPresets.Clear } },
        new(!before.Playback.SurroundEnabled, new(2, -2, 1, -1, 0, 3, -3, 4, -4), new(true, .5f)));

    public static void Run(TestSuite suite)
    {
        suite.Case("GSX paired apply updates both pages with one notification and preserves every unowned byte", () => {
            var memory = new Memory(); var before = GsxProcessingState.Read(memory.Bytes); var desired = Desired(before);
            var result = Backend(memory).Apply(Mic, Output, before, desired);
            TestSuite.Assert(result.Matches(desired) && memory.Notifications == 1 && memory.Disposals == 1);
            TestSuite.Assert(memory.Bytes.SequenceEqual(Patched(Seed(), desired)));
        });
        suite.Case("Unchanged GSX paired apply performs no writes or notifications", () => {
            var memory = new Memory(); var before = GsxProcessingState.Read(memory.Bytes);
            Backend(memory).Apply(Mic, Output, before, before);
            TestSuite.Assert(memory.Writes == 0 && memory.Notifications == 0 && memory.Disposals == 1);
        });
        foreach (var page in new[] { "microphone", "playback" }) suite.Case("Stale GSX " + page + " prevents all paired writes", () => {
            var memory = new Memory(); var before = GsxProcessingState.Read(memory.Bytes);
            var stale = page == "microphone" ? before with { Microphone = before.Microphone with { GatePercent = 99 } }
                : before with { Playback = before.Playback with { SurroundEnabled = !before.Playback.SurroundEnabled } };
            TestSuite.Reject(() => Backend(memory).Apply(Mic, Output, stale, Desired(before)));
            TestSuite.Assert(memory.Writes == 0 && memory.Bytes.SequenceEqual(Seed()));
        });
        suite.Case("Unsupported GSX high-pass restore refuses before opening or writing either page", () => {
            var memory = new Memory(); var before = GsxProcessingState.Read(memory.Bytes); var opens = 0;
            var backend = new ApoGsxProcessingBackend((_, _) => { opens++; return memory; });
            var desired = Desired(before) with { Microphone = before.Microphone with { Processing = before.Microphone.Processing! with { HighPassEnabled = !before.Microphone.Processing.HighPassEnabled } } };
            TestSuite.Reject(() => backend.Apply(Mic, Output, before, desired)); TestSuite.Assert(opens == 0);
        });
        suite.Case("Incomplete or invalid GSX paired settings refuse before opening transport", () => {
            var before = GsxProcessingState.Read(Seed()); var opens = 0;
            var backend = new ApoGsxProcessingBackend((_, _) => { opens++; return new Memory(); });
            foreach (var bad in new[] { before with { Microphone = null! }, before with { Playback = null! },
                before with { Microphone = before.Microphone with { Processing = null } },
                before with { Playback = before.Playback with { Equalizer = null } },
                before with { Playback = before.Playback with { Reverb = new(true, float.NaN) } } }) {
                TestSuite.Reject(() => backend.Apply(Mic, Output, before, bad));
                TestSuite.Reject(() => backend.Apply(Mic, Output, bad, before));
            }
            TestSuite.Assert(opens == 0);
        });
        suite.Case("GSX paired transport refuses mixed units, B20 and swapped directions before opening", () => {
            var before = GsxProcessingState.Read(Seed()); var opens = 0;
            var backend = new ApoGsxProcessingBackend((_, _) => { opens++; return new Memory(); });
            foreach (var pair in new[] { (Output, Mic), (Mic, Output with { Usb = Output.Usb! with { InstanceId = "OTHER" } }),
                (new DemoAudioBackend().Discover().First(), Output) }) TestSuite.Reject(() => backend.Read(pair.Item1, pair.Item2));
            TestSuite.Assert(opens == 0);
        });
        var plan = ApoMicrophoneCodec.Prepare(Seed(), Desired(GsxProcessingState.Read(Seed())).Microphone)
            .Concat(ApoPlaybackCodec.Prepare(Seed(), Desired(GsxProcessingState.Read(Seed())).Playback)).ToArray();
        foreach (var failureAt in new[] { 1, plan.Length / 2, plan.Length }) suite.Case("GSX paired write failure " + failureAt + " restores both pages exactly", () => {
            var memory = new Memory { FailWrite = failureAt }; var before = GsxProcessingState.Read(memory.Bytes);
            TestSuite.Throws<IOException>(() => Backend(memory).Apply(Mic, Output, before, Desired(before)));
            TestSuite.Assert(memory.Bytes.SequenceEqual(Seed()) && memory.Disposals == 1);
        });
        suite.Case("GSX paired notification failure rolls back both pages", () => {
            var memory = new Memory { OnNotify = (m, n) => { if (n == 1) throw new IOException("notify"); } };
            var before = GsxProcessingState.Read(memory.Bytes);
            TestSuite.Throws<IOException>(() => Backend(memory).Apply(Mic, Output, before, Desired(before)));
            TestSuite.Assert(memory.Bytes.SequenceEqual(Seed()) && memory.Notifications == 2);
        });
        suite.Case("GSX paired rollback preserves newer unowned data", () => {
            var memory = new Memory { OnNotify = (m, n) => { if (n == 1) { m.Bytes[58] = 123; throw new IOException(); } } };
            var before = GsxProcessingState.Read(memory.Bytes);
            TestSuite.Throws<IOException>(() => Backend(memory).Apply(Mic, Output, before, Desired(before)));
            var expected = Seed(); expected[58] = 123; TestSuite.Assert(memory.Bytes.SequenceEqual(expected));
        });
        suite.Case("GSX paired rollback refuses to overwrite a newer owned playback edit", () => {
            var memory = new Memory { OnNotify = (m, n) => { if (n == 1) BinaryPrimitives.WriteSingleLittleEndian(m.Bytes.AsSpan(76), 4.5f); } };
            var before = GsxProcessingState.Read(memory.Bytes);
            TestSuite.Throws<AggregateException>(() => Backend(memory).Apply(Mic, Output, before, Desired(before)));
            TestSuite.Assert(ApoPlaybackCodec.Read(memory.Bytes).Equalizer!.Band1 == 4.5f && memory.Notifications == 1);
        });
        suite.Case("GSX paired invalid playback read refuses without writing microphone", () => {
            var memory = new Memory(); memory.Bytes[64] = 255;
            TestSuite.Reject(() => Backend(memory).Apply(Mic, Output, GsxProcessingState.Read(Seed()), Desired(GsxProcessingState.Read(Seed()))));
            TestSuite.Assert(memory.Writes == 0 && memory.Bytes[64] == 255);
        });
        suite.Case("GSX paired rollback refuses to overwrite a newer owned microphone edit", () => {
            var memory = new Memory { OnNotify = (m, n) => { if (n == 1) BinaryPrimitives.WriteSingleLittleEndian(m.Bytes.AsSpan(152), .8f); } };
            var before = GsxProcessingState.Read(memory.Bytes);
            TestSuite.Throws<AggregateException>(() => Backend(memory).Apply(Mic, Output, before, Desired(before)));
            TestSuite.Assert(Math.Abs(ApoMicrophoneCodec.Read(memory.Bytes).GatePercent - 80) < .001f && memory.Notifications == 1);
        });
        StoreTests(suite);
        if (OperatingSystem.IsWindows()) NativeTests(suite);
    }
    private static void StoreTests(TestSuite suite)
    {
        suite.Case("GSX typed saved state defaults to manual restore and contains both complete pages only", () => WithStore(store => {
            var desired = Desired(GsxProcessingState.Read(Seed())); var saved = store.Remember(Mic, Output, desired);
            var text = File.ReadAllText(store.StatePath(Mic, Output));
            TestSuite.Assert(store.Load(Mic, Output) == saved && !saved.RestoreOnConnect && saved.Effects == desired && saved.SavedAtUtc.Offset == TimeSpan.Zero);
            TestSuite.Assert(!text.Contains("DiagnosticSeed") && !text.Contains("EndpointId"));
        }));
        suite.Case("GSX saved state follows physical USB identity across both endpoint renames", () => WithStore(store => {
            var saved = store.Remember(Mic, Output, GsxProcessingState.Read(Seed()));
            TestSuite.Assert(store.Load(Mic with { Id = "new-mic", Name = "new" }, Output with { Id = "new-output" }) == saved);
            var mic = Mic with { Usb = Mic.Usb! with { InstanceId = "OTHER" } }; var output = Output with { Usb = Output.Usb! with { InstanceId = "OTHER" } };
            TestSuite.Assert(store.Load(mic, output) is null);
        }));
        suite.Case("GSX restore preference requires saved state and survives later saves", () => WithStore(store => {
            TestSuite.Reject(() => store.SetRestoreOnConnect(Mic, Output, true));
            store.Remember(Mic, Output, GsxProcessingState.Read(Seed())); store.SetRestoreOnConnect(Mic, Output, true);
            var next = store.Remember(Mic, Output, Desired(GsxProcessingState.Read(Seed())));
            TestSuite.Assert(next.RestoreOnConnect && store.Load(Mic, Output) == next);
        }));
        suite.Case("GSX separate writers preserve preference and processing atomically", () => WithStore(store => {
            store.Remember(Mic, Output, GsxProcessingState.Read(Seed())); var desired = Desired(GsxProcessingState.Read(Seed()));
            Parallel.Invoke(() => new GsxProcessingStateStore(store.DirectoryPath).SetRestoreOnConnect(Mic, Output, true),
                () => new GsxProcessingStateStore(store.DirectoryPath).Remember(Mic, Output, desired));
            TestSuite.Assert(store.Load(Mic, Output) is { RestoreOnConnect: true } saved && saved.Effects == desired);
            TestSuite.Assert(!Directory.EnumerateFiles(store.DirectoryPath, "*.tmp").Any());
        }));
        suite.Case("GSX failed paired hardware apply never overwrites saved state", () => WithStore(store => {
            var before = GsxProcessingState.Read(Seed()); var saved = store.Remember(Mic, Output, before); var memory = new Memory { FailWrite = 2 };
            TestSuite.Throws<IOException>(() => store.ApplyAndSave(Mic, Output, Backend(memory), before, Desired(before)));
            TestSuite.Assert(store.Load(Mic, Output) == saved && memory.Bytes.SequenceEqual(Seed()));
        }));
        suite.Case("GSX save failure reports persistence separately from completed hardware apply", () => WithStore(store => {
            Directory.CreateDirectory(store.DirectoryPath); Directory.CreateDirectory(store.StatePath(Mic, Output));
            var memory = new Memory(); var before = GsxProcessingState.Read(memory.Bytes); var desired = Desired(before);
            var result = store.ApplyAndSave(Mic, Output, Backend(memory), before, desired);
            TestSuite.Assert(result.PersistenceError is not null && result.Effects.Matches(desired) && GsxProcessingState.Read(memory.Bytes).Matches(desired));
        }));
        suite.Case("GSX store rejects missing fields, schema, identity, timestamps and incomplete pages", () => WithStore(store => {
            var saved = store.Remember(Mic, Output, GsxProcessingState.Read(Seed())); var path = store.StatePath(Mic, Output);
            foreach (var bad in new[] { saved with { SchemaVersion = 2 }, saved with { DeviceIdentity = "OTHER" },
                saved with { SavedAtUtc = default }, saved with { SavedAtUtc = saved.SavedAtUtc.ToOffset(TimeSpan.FromHours(1)) },
                saved with { Effects = null! }, saved with { Effects = saved.Effects with { Playback = saved.Effects.Playback with { Equalizer = null } } } }) {
                File.WriteAllText(path, JsonSerializer.Serialize(bad)); TestSuite.Reject(() => store.Load(Mic, Output));
            }
            foreach (var text in new[] { "{}", JsonSerializer.Serialize(saved).Replace("\"SchemaVersion\":1", "\"SchemaVersion\":1,\"Unexpected\":true") }) {
                File.WriteAllText(path, text); TestSuite.Throws<JsonException>(() => store.Load(Mic, Output));
            }
            File.WriteAllText(path, new string(' ', 16385)); TestSuite.Reject(() => store.Load(Mic, Output));
        }));
        suite.Case("GSX corrupt saved state cannot be overwritten by Remember", () => WithStore(store => {
            store.Remember(Mic, Output, GsxProcessingState.Read(Seed())); var path = store.StatePath(Mic, Output); File.WriteAllText(path, "{}");
            TestSuite.Throws<JsonException>(() => store.Remember(Mic, Output, Desired(GsxProcessingState.Read(Seed()))));
            TestSuite.Assert(File.ReadAllText(path) == "{}");
        }));
        suite.Case("GSX manual restore requires both matching endpoints and rejects model ambiguity", () => WithStore(store => {
            store.Remember(Mic, Output, Desired(GsxProcessingState.Read(Seed()))); var memory = new Memory();
            foreach (var endpoints in new IReadOnlyList<AudioEndpoint>[] { [Mic], [Output], [Mic, Output, Mic with { Id = "second", Usb = Mic.Usb! with { InstanceId = "OTHER" } }] })
                TestSuite.Reject(() => store.Restore(Mic, Output, new Audio(endpoints), Backend(memory)));
            TestSuite.Assert(memory.Writes == 0);
            TestSuite.Assert(store.Restore(Mic, Output, new Audio([Mic, Output]), Backend(memory)).Matches(Desired(GsxProcessingState.Read(Seed()))));
        }));
        suite.Case("Busy GSX persistence lock fails within a bounded interval and keeps both saved pages", () => WithStore(store => {
            var saved = store.Remember(Mic, Output, GsxProcessingState.Read(Seed()));
            using (var held = new FileStream(store.StatePath(Mic, Output) + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                TestSuite.Throws<IOException>(() => store.Remember(Mic, Output, Desired(saved.Effects)));
            TestSuite.Assert(store.Load(Mic, Output) == saved);
        }));
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void NativeTests(TestSuite suite)
    {
        suite.Case("GSX paired saved state restores through native shared objects after complete recreation", () => WithStore(store => {
            var name = "Local\\Timbre.Tests." + Guid.NewGuid().ToString("N"); var seed = Seed(); var desired = Desired(GsxProcessingState.Read(seed));
            var backend = new ApoGsxProcessingBackend((_, _) => OperatingSystem.IsWindows() ? new WindowsApoMemory.Session(name, (o, l) => WindowsApoMemory.ValidateGsxPatch(Mic, o, l)) : throw new PlatformNotSupportedException());
            using (var host = WindowsApoObjectHost.OpenPrivate(name, true, seed, true)) {
                TestSuite.Assert(store.ApplyAndSave(Mic, Output, backend, backend.Read(Mic, Output), desired).PersistenceError is null);
            }
            using var recreated = WindowsApoObjectHost.OpenPrivate(name, true, seed, true);
            new GsxProcessingStateStore(store.DirectoryPath).Restore(Mic, Output, new Audio([Mic, Output]), backend);
            using var session = new WindowsApoMemory.Session(name);
            TestSuite.Assert(session.Read().SequenceEqual(Patched(seed, desired)));
            using var lease = WindowsApoObjectLease.OpenExisting(name, true); TestSuite.Assert(lease.ReadGsx().Matches(desired));
        }));
        suite.Case("GSX combined native write guard rejects unsupported fields and invalid sizes", () => {
            var name = "Local\\Timbre.Tests." + Guid.NewGuid().ToString("N"); using var host = WindowsApoObjectHost.OpenPrivate(name, true, Seed(), true);
            using var session = new WindowsApoMemory.Session(name, (o, l) => WindowsApoMemory.ValidateGsxPatch(Mic, o, l));
            foreach (var patch in new[] { (71, new byte[] { 1 }), (70, new byte[] { 1 }), (64, new byte[4]), (77, new byte[4]), (152, new byte[1]), (0, new byte[4]) })
                TestSuite.Reject(() => session.Write(patch.Item1, patch.Item2));
            TestSuite.Assert(session.Read().SequenceEqual(Seed()));
        });
        suite.Case("GSX read-only retention rejects corrupt playback without repair or state changes", () => {
            var name = "Local\\Timbre.Tests." + Guid.NewGuid().ToString("N"); using var host = WindowsApoObjectHost.OpenPrivate(name, true, Seed(), true);
            using var map = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(name + "_memory"); using var view = map.CreateViewAccessor();
            view.Write(64, (byte)255);
            TestSuite.Throws<InvalidDataException>(() => WindowsApoObjectLease.OpenExisting(name, true));
            TestSuite.Assert(view.ReadByte(64) == 255);
        });
        suite.Case("GSX paired native processing leaves the separate B20 mapping unchanged", () => {
            var gsxName = "Local\\Timbre.Tests." + Guid.NewGuid().ToString("N"); var b20Name = "Local\\Timbre.Tests." + Guid.NewGuid().ToString("N");
            var b20Seed = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "apo-memory-009f.bin"));
            using var b20 = WindowsApoObjectHost.OpenPrivate(b20Name, true, b20Seed);
            using var gsx = WindowsApoObjectHost.OpenPrivate(gsxName, true, Seed(), true);
            var backend = new ApoGsxProcessingBackend((_, _) => OperatingSystem.IsWindows() ? new WindowsApoMemory.Session(gsxName, (o, l) => WindowsApoMemory.ValidateGsxPatch(Mic, o, l)) : throw new PlatformNotSupportedException());
            backend.Apply(Mic, Output, backend.Read(Mic, Output), Desired(GsxProcessingState.Read(Seed())));
            using var session = new WindowsApoMemory.Session(b20Name); TestSuite.Assert(session.Read().SequenceEqual(b20Seed));
        });
    }
    internal static byte[] Patched(byte[] seed, GsxProcessingState desired)
    {
        var expected = seed.ToArray();
        foreach (var patch in ApoMicrophoneCodec.Prepare(seed, desired.Microphone).Concat(ApoPlaybackCodec.Prepare(seed, desired.Playback))) patch.After.CopyTo(expected, patch.Offset);
        return expected;
    }
    internal sealed class Audio(IReadOnlyList<AudioEndpoint> devices) : IAudioBackend
    {
        public IReadOnlyList<AudioEndpoint> Devices = devices;
        public IReadOnlyList<AudioEndpoint> Discover() => Devices;
        public AudioState Read(string id) => throw new NotSupportedException();
        public AudioState Apply(string id, float level, bool muted) => throw new NotSupportedException();
    }
    private static ApoGsxProcessingBackend Backend(Memory memory) => new((_, _) => memory);
    private sealed class Memory : IEffectMemorySession
    {
        public byte[] Bytes = Seed(); public int Writes, Notifications, Disposals, FailWrite;
        public Action<Memory, int>? OnNotify;
        public byte[] Read() => Bytes.ToArray();
        public void Write(int offset, byte[] data) { Writes++; if (Writes == FailWrite) throw new IOException("write"); data.CopyTo(Bytes, offset); }
        public void Notify() { Notifications++; OnNotify?.Invoke(this, Notifications); }
        public void Dispose() => Disposals++;
    }
    internal static void WithStore(Action<GsxProcessingStateStore> action)
    {
        var store = new GsxProcessingStateStore(Path.Combine(Path.GetTempPath(), "Timbre.GsxState." + Guid.NewGuid().ToString("N")));
        try { action(store); } finally { if (Directory.Exists(store.DirectoryPath)) Directory.Delete(store.DirectoryPath, true); }
    }
}
