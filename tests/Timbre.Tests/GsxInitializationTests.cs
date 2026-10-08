using System.Buffers.Binary;
using System.Text.Json;
using Timbre.Core;

internal static class GsxInitializationTests
{
    public static void Run(TestSuite suite)
    {
        var devices = new DemoAudioBackend().Discover();
        var microphone = devices.Single(e => e.Direction == AudioDirection.Microphone && e.Usb!.ProductId == "0098");
        var playback = devices.Single(e => e.Direction == AudioDirection.Playback);
        var seed = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "apo-memory-0098.bin"));
        var valid = new GsxApoStartupState(1, microphone.Usb!.InstanceId,
            ApoMicrophoneCodec.Read(seed), ApoPlaybackCodec.Read(seed), seed);
        void WithFile(string text, Action<string> action)
        {
            var path = Path.Combine(Path.GetTempPath(), "Timbre.GsxStartup." + Guid.NewGuid().ToString("N") + ".json");
            try { File.WriteAllText(path, text); action(path); } finally { File.Delete(path); }
        }
        suite.Case("GSX startup preserves both complete captured pages and physical identity", () =>
            WithFile(JsonSerializer.Serialize(valid), path => {
                var loaded = GsxApoStartupState.Load(path, microphone, playback);
                TestSuite.Assert(loaded.DiagnosticSeed.SequenceEqual(seed) && loaded.Microphone == valid.Microphone && loaded.Playback == valid.Playback);
            }));
        suite.Case("GSX startup tolerates endpoint GUID and name changes on the same physical device", () =>
            valid.Validate(microphone with { Id = "new-mic", Name = "Renamed mic" }, playback with { Id = "new-output", Name = "Renamed output" }));
        var pinned = valid with { MicrophoneEndpointId = microphone.Id, PlaybackEndpointId = playback.Id };
        suite.Case("Paused GSX selection uses captured endpoints and excludes historical endpoint aliases", () => {
            var historical = playback with { Id = "old-output" };
            var selected = pinned.SelectCapturedEndpoints([historical, microphone, playback]);
            TestSuite.Assert(selected.Microphone == microphone && selected.Playback == playback);
        });
        suite.Case("Paused GSX selection refuses missing, duplicate, wrong direction and foreign physical endpoints", () => {
            foreach (var candidate in new IReadOnlyList<AudioEndpoint>[] { [], [microphone], [microphone, playback, playback],
                [microphone with { Direction = AudioDirection.Playback }, playback],
                [microphone, playback, microphone with { Id = "other", Usb = microphone.Usb! with { InstanceId = "OTHER" } }] })
                TestSuite.Reject(() => pinned.SelectCapturedEndpoints(candidate));
            TestSuite.Reject(() => valid.SelectCapturedEndpoints(devices));
            TestSuite.Reject(() => (pinned with { PlaybackEndpointId = microphone.Id }).SelectCapturedEndpoints(devices));
        });
        suite.Case("Pinned GSX endpoints survive metadata-only selection without active volume state", () => {
            var metadata = new[] { microphone with { State = null, Format = null }, playback with { State = null, Format = null } };
            var pair = pinned.SelectCapturedEndpoints(metadata);
            TestSuite.Assert(pair.Microphone.State is null && pair.Playback.Format is null);
        });
        suite.Case("GSX startup rejects another USB instance, mixed devices and wrong directions", () => {
            TestSuite.Reject(() => (valid with { DeviceInstance = "OTHER" }).Validate(microphone, playback));
            TestSuite.Reject(() => valid.Validate(microphone, playback with { Usb = playback.Usb! with { InstanceId = "OTHER" } }));
            TestSuite.Reject(() => valid.Validate(devices.First(), playback));
            TestSuite.Reject(() => valid.Validate(playback, microphone));
            TestSuite.Reject(() => valid.Validate(microphone, playback with { Usb = playback.Usb! with { VendorId = "1234" } }));
        });
        suite.Case("GSX startup rejects corrupt schema, missing complete pages and unknown fields", () => {
            foreach (var bad in new[] { valid with { SchemaVersion = 2 }, valid with { Microphone = valid.Microphone with { Processing = null } },
                valid with { Playback = valid.Playback with { Equalizer = null } }, valid with { Playback = valid.Playback with { Reverb = null } } })
                TestSuite.Reject(() => bad.Validate(microphone, playback));
            WithFile("{}", path => TestSuite.Throws<JsonException>(() => GsxApoStartupState.Load(path, microphone, playback)));
            WithFile(JsonSerializer.Serialize(valid).Replace("\"SchemaVersion\":1", "\"SchemaVersion\":1,\"GuessDefaults\":true"),
                path => TestSuite.Throws<JsonException>(() => GsxApoStartupState.Load(path, microphone, playback)));
            WithFile("null", path => TestSuite.Reject(() => GsxApoStartupState.Load(path, microphone, playback)));
            WithFile(new string(' ', 16385), path => TestSuite.Reject(() => GsxApoStartupState.Load(path, microphone, playback)));
        });
        suite.Case("GSX startup rejects microphone or playback settings inconsistent with the capture", () => {
            TestSuite.Reject(() => (valid with { Microphone = valid.Microphone with { GatePercent = 101 } }).Validate(microphone, playback));
            TestSuite.Reject(() => (valid with { Microphone = valid.Microphone with { GatePercent = valid.Microphone.GatePercent == 0 ? 50 : 0 } }).Validate(microphone, playback));
            TestSuite.Reject(() => (valid with { Playback = valid.Playback with { SurroundEnabled = !valid.Playback.SurroundEnabled } }).Validate(microphone, playback));
            TestSuite.Reject(() => (valid with { Playback = valid.Playback with { Reverb = new(true, float.NaN) } }).Validate(microphone, playback));
        });
        suite.Case("GSX startup rejects incompatible captured layouts and unknown nonzero padding", () => {
            foreach (var length in new[] { 4095, 4097 }) TestSuite.Reject(() => (valid with { DiagnosticSeed = new byte[length] }).Validate(microphone, playback));
            foreach (var offset in new[] { 0, 64, 69, 2112, 4095 }) {
                var corrupt = seed.ToArray(); corrupt[offset] = 255;
                TestSuite.Reject(() => (valid with { DiagnosticSeed = corrupt }).Validate(microphone, playback));
            }
        });
        suite.Case("GSX startup refuses absent or multiple same-model devices before opening native objects", () => {
            WindowsApoMemory.ValidateIdentity(microphone, devices);
            WindowsApoMemory.ValidatePlaybackIdentity(playback, devices);
            TestSuite.Reject(() => WindowsApoMemory.ValidateIdentity(microphone, devices.Where(e => e.Id != microphone.Id).ToArray()));
            var other = microphone with { Id = "another-gsx", Usb = microphone.Usb with { InstanceId = "OTHER" } };
            TestSuite.Reject(() => WindowsApoMemory.ValidateIdentity(microphone, [.. devices, other]));
            TestSuite.Reject(() => WindowsApoMemory.ValidatePlaybackIdentity(playback, [.. devices, other]));
        });
        suite.Case("GSX helper options require explicit startup and refuse B20 managed restore options", () => {
            string[] common = ["--report-directory", "reports", "--stop-file", "stop", "--seconds", "30"];
            TestSuite.Assert(ApoHostOptions.Parse(["--initialize-gsx", "--initial-state", "state", .. common]).Mode == "--initialize-gsx");
            TestSuite.Assert(ApoHostOptions.Parse(["--validate-gsx", "--initial-state", "state", .. common]).Mode == "--validate-gsx");
            TestSuite.Assert(ApoHostOptions.Parse(["--initialize-gsx-paused", "--initial-state", "state", .. common]).Mode == "--initialize-gsx-paused");
            TestSuite.Throws<ArgumentException>(() => ApoHostOptions.Parse(["--initialize-gsx", .. common]));
            TestSuite.Throws<ArgumentException>(() => ApoHostOptions.Parse(["--initialize-gsx", "--initial-state", "state", "--state-directory", "store", .. common]));
            TestSuite.Throws<ArgumentException>(() => ApoHostOptions.Parse(["--initialize-gsx", "--initial-state", "state", "--device-instance", "unit", .. common]));
        });
        if (OperatingSystem.IsWindows()) RunWindows(suite, microphone, playback, seed);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RunWindows(TestSuite suite, AudioEndpoint microphone, AudioEndpoint playback, byte[] seed)
    {
        string NewName() => "Local\\Timbre.Tests." + Guid.NewGuid().ToString("N");
        suite.Case("Fresh GSX native objects preserve all bytes and both pages without B20 changes", () => {
            var gsxName = NewName(); var b20Name = NewName();
            using var b20 = WindowsApoObjectHost.OpenPrivate(b20Name, true);
            byte[] b20Before; using (var session = new WindowsApoMemory.Session(b20Name)) b20Before = session.Read();
            var capture = seed.ToArray(); capture[58] = 71;
            using var gsx = WindowsApoObjectHost.OpenPrivate(gsxName, true, capture, true);
            capture[58] = 99;
            TestSuite.Assert(gsx.CreatedFresh && ApoMicrophoneCodec.Matches(gsx.Read(), ApoMicrophoneCodec.Read(seed)) &&
                ApoPlaybackCodec.Matches(gsx.ReadPlayback(), ApoPlaybackCodec.Read(seed)));
            using (var session = new WindowsApoMemory.Session(gsxName)) {
                var bytes = session.Read(); TestSuite.Assert(bytes[58] == 71); bytes[58] = seed[58]; TestSuite.Assert(bytes.SequenceEqual(seed));
            }
            using (var session = new WindowsApoMemory.Session(b20Name)) TestSuite.Assert(session.Read().SequenceEqual(b20Before));
            using var changed = EventWaitHandle.OpenExisting(gsxName + "_event"); TestSuite.Assert(changed.WaitOne(0) && changed.WaitOne(0));
        });
        suite.Case("GSX native transports restore microphone and playback separately with exact shared memory", () => {
            var name = NewName(); using var host = WindowsApoObjectHost.OpenPrivate(name, true, seed, true);
            var micBackend = new ApoMicrophoneEffectsBackend(_ => new WindowsApoMemory.Session(name,
                (offset, length) => WindowsApoMemory.ValidateMicrophonePatch(microphone, offset, length)));
            var playbackBackend = new ApoPlaybackEffectsBackend(_ => new WindowsApoMemory.Session(name, ApoPlaybackCodec.ValidatePatch));
            var micBefore = micBackend.Read(microphone); var soundBefore = playbackBackend.Read(playback);
            var micDesired = micBefore with { GatePercent = 50, FilterLevel = 1, Processing = micBefore.Processing! with { EqualizerEnabled = true, Equalizer = MicrophoneEqPresets.Warm } };
            micBackend.Apply(microphone, micBefore, micDesired);
            TestSuite.Assert(ApoPlaybackCodec.Matches(host.ReadPlayback(), soundBefore));
            var soundDesired = soundBefore with { SurroundEnabled = !soundBefore.SurroundEnabled, Equalizer = new(6, 0, 0, 0, 0, 0, 0, 0, -6), Reverb = new(true, .5f) };
            playbackBackend.Apply(playback, soundBefore, soundDesired);
            TestSuite.Assert(ApoMicrophoneCodec.Matches(host.Read(), micDesired));
            micBackend.Apply(microphone, micDesired, micBefore); playbackBackend.Apply(playback, soundDesired, soundBefore);
            using var session = new WindowsApoMemory.Session(name); TestSuite.Assert(session.Read().SequenceEqual(seed));
        });
        suite.Case("GSX native initializer refuses an existing complete set without resetting pages", () => {
            var name = NewName(); using var first = WindowsApoObjectHost.OpenPrivate(name, true, seed, true);
            TestSuite.Throws<InvalidOperationException>(() => WindowsApoObjectHost.OpenPrivate(name, true, seed, true));
            using var session = new WindowsApoMemory.Session(name); TestSuite.Assert(session.Read().SequenceEqual(seed));
        });
        suite.Case("Invalid GSX playback seed rejects before named objects are created", () => {
            var name = NewName(); var corrupt = seed.ToArray(); BinaryPrimitives.WriteSingleLittleEndian(corrupt.AsSpan(148), float.NaN);
            TestSuite.Throws<InvalidDataException>(() => WindowsApoObjectHost.OpenPrivate(name, true, corrupt, true));
            TestSuite.Throws<WaitHandleCannotBeOpenedException>(() => { using var mutex = Mutex.OpenExisting(name + "_mutex"); });
            TestSuite.Throws<FileNotFoundException>(() => { using var map = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(name + "_memory"); });
        });
        suite.Case("GSX health read rejects newly corrupt playback while leaving memory unchanged", () => {
            var name = NewName(); using var host = WindowsApoObjectHost.OpenPrivate(name, true, seed, true);
            using var map = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(name + "_memory");
            using var view = map.CreateViewAccessor(); view.Write(64, (byte)255);
            TestSuite.Throws<InvalidDataException>(() => host.Read());
            TestSuite.Throws<InvalidDataException>(() => host.ReadPlayback());
            TestSuite.Assert(view.ReadByte(64) == 255);
        });
        suite.Case("GSX initializer ownership refuses a competing thread and releases cleanly", () => {
            var name = NewName(); using var first = GsxHostOwner.AcquirePrivate(name); Exception? failure = null;
            var thread = new Thread(() => { try { TestSuite.Throws<InvalidOperationException>(() => GsxHostOwner.AcquirePrivate(name)); } catch (Exception ex) { failure = ex; } });
            thread.Start(); TestSuite.Assert(thread.Join(3000)); if (failure is not null) throw failure;
            first.Dispose(); first.Dispose(); using var next = GsxHostOwner.AcquirePrivate(name);
        });
    }
}
