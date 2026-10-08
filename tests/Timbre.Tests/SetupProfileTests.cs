using System.Text.Json;
using Timbre.Core;

internal static class SetupProfileTests
{
    private sealed class Audio : IAudioBackend
    {
        public Dictionary<string, AudioEndpoint> Endpoints { get; } = new DemoAudioBackend().Discover().ToDictionary(e => e.Id);
        public int Writes;
        public Action<string>? AfterWrite;
        public IReadOnlyList<AudioEndpoint> Discover() => Endpoints.Values.ToArray();
        public AudioState Read(string id) => Endpoints[id].State!;
        public AudioState Apply(string id, float level, bool muted)
        {
            var state = Read(id) with { Level = level, Muted = muted };
            Endpoints[id] = Endpoints[id] with { State = state }; Writes++; AfterWrite?.Invoke(id); return state;
        }
        public void Set(AudioEndpoint endpoint, float level, bool muted) => Endpoints[endpoint.Id] = endpoint with { State = Read(endpoint.Id) with { Level = level, Muted = muted } };
    }
    private sealed class Mic : IMicrophoneEffectsBackend
    {
        private readonly DemoMicrophoneEffectsBackend inner = new();
        public string? UnavailableId;
        public MicrophoneEffects Read(AudioEndpoint endpoint) => endpoint.Id == UnavailableId ? throw new IOException("Test adapter unavailable.") : inner.Read(endpoint);
        public MicrophoneEffects Apply(AudioEndpoint endpoint, MicrophoneEffects expected, MicrophoneEffects desired) => inner.Apply(endpoint, expected, desired);
    }
    private sealed class Rig
    {
        public Audio Audio { get; } = new();
        public Mic Mic { get; } = new();
        public DemoSidetoneBackend Monitor { get; } = new();
        public DemoPlaybackEffectsBackend Sound { get; } = new();
        public SetupProfileController Controller => new(Audio, Mic, Monitor, Sound);
        public AudioEndpoint B20 => Audio.Discover().First(e => e.Usb?.ProductId == "009F");
        public AudioEndpoint Output => Audio.Discover().First(e => e.Direction == AudioDirection.Playback);
        public SetupProfile Capture(string name = "Gaming") => Controller.Capture(name, Audio.Discover());
    }
    private static void Scratch(Action<SetupProfileStore, string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "epos-setups-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try { action(new(Path.Combine(directory, "setups.json")), directory); }
        finally { foreach (var file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
    }
    public static void Run(TestSuite suite)
    {
        suite.Case("Microphone-only setup leaves the same B20 device's speaker settings untouched", () => {
            var rig = new Rig(); var mic = rig.B20;
            var speaker = mic with { Id = "b20-speaker", Name = "Speakers (EPOS B20)", Direction = AudioDirection.Playback,
                State = new AudioState(.71321f, true, -5, 3) };
            rig.Audio.Endpoints[speaker.Id] = speaker;
            var setup = rig.Controller.Capture("Gaming", [mic]);
            TestSuite.Assert(setup.Devices.Count == 1 && setup.Devices[0].Settings.Direction == AudioDirection.Microphone);
            rig.Audio.Set(mic, .9f, true); var beforeSpeaker = rig.Audio.Read(speaker.Id);
            var result = rig.Controller.Apply(setup);
            TestSuite.Assert(result.Single().Status == SetupApplyStatus.Applied && rig.Audio.Writes == 1);
            TestSuite.Assert(rig.Audio.Read(speaker.Id) == beforeSpeaker, "Excluded speaker level/mute must not be changed even on the same physical USB device.");
        });
        suite.Case("Removing an entire physical device from a setup leaves both of its pages untouched", () => Scratch((store, _) => {
            var rig = new Rig(); var complete = rig.Capture(); store.Save(complete);
            var micOnly = rig.Controller.Capture(complete.Name, [rig.B20]); store.UpdateSelected(complete, micOnly);
            foreach (var endpoint in rig.Audio.Discover().Where(e => e.Usb?.ProductId == "0098")) rig.Audio.Set(endpoint, .87654f, true);
            rig.Sound.Apply(rig.Output, rig.Sound.Read(rig.Output), new(true, Reverb: new(true, .423f)));
            var beforeSound = rig.Sound.Read(rig.Output);
            var before = rig.Audio.Discover().Where(e => e.Usb?.ProductId == "0098").ToDictionary(e => e.Id, e => rig.Audio.Read(e.Id));
            var results = rig.Controller.Apply(store.Load().Single());
            TestSuite.Assert(results.Count == 1 && rig.Audio.Writes == 1);
            TestSuite.Assert(before.All(p => rig.Audio.Read(p.Key) == p.Value), "An excluded device must not receive writes on either audio page.");
            TestSuite.Assert(rig.Sound.Read(rig.Output) == beforeSound, "Excluded GSX reverb must remain untouched.");
        }));
        suite.Case("Setup capture is read-only and saves complete selected microphone and sound settings", () => {
            var rig = new Rig(); var mic = rig.B20; var output = rig.Output;
            rig.Audio.Set(mic, .43219f, false);
            var monitor = rig.Monitor.Read(mic); rig.Monitor.Apply(mic, monitor, new(-12, -9, true));
            rig.Sound.Apply(output, rig.Sound.Read(output), new(true, new(1, -2, 3, -4, 5, -6, 2, 1, -.75f), new(true, .376f)));
            var setup = rig.Controller.Capture("Streaming", [mic, output]);
            TestSuite.Assert(rig.Audio.Writes == 0 && setup.Devices.Count == 2);
            TestSuite.Assert(setup.Devices[0].Settings.Level == .43219f && setup.Devices[0].Settings.Effects?.Processing is not null && setup.Devices[0].Settings.Sidetone == new SidetoneSettings(-12, -9, true));
            TestSuite.Assert(setup.Devices[1].Settings.Playback?.Equalizer?.Band9 == -.75f && setup.Devices[1].Settings.Playback?.SurroundEnabled == true);
            TestSuite.Assert(setup.Devices[1].Settings.Playback?.Reverb == new PlaybackReverb(true, .376f));
        });
        suite.Case("Setup applies independent B20 and GSX microphone and playback snapshots", () => {
            var rig = new Rig(); var setup = rig.Capture();
            foreach (var endpoint in rig.Audio.Discover()) rig.Audio.Set(endpoint, .9f, true);
            var b20 = rig.B20; rig.Mic.Apply(b20, rig.Mic.Read(b20), rig.Mic.Read(b20) with { GatePercent = 91 });
            rig.Monitor.Apply(b20, rig.Monitor.Read(b20), new(-15, -12, true));
            var output = rig.Output; rig.Sound.Apply(output, rig.Sound.Read(output), new(true, PlaybackEqualizer.Flat with { Band2 = 4 }, new(true, .812f)));
            var results = rig.Controller.Apply(setup);
            TestSuite.Assert(results.All(r => r.Status == SetupApplyStatus.Applied) && rig.Audio.Writes == 3);
            foreach (var device in setup.Devices) {
                var endpoint = rig.Audio.Discover().Single(e => ProfileStore.BelongsTo(e, device.Settings));
                TestSuite.Assert(rig.Audio.Read(endpoint.Id).Level == device.Settings.Level && rig.Audio.Read(endpoint.Id).Muted == device.Settings.Muted);
                if (device.Settings.Effects is { } effects) TestSuite.Assert(ApoMicrophoneCodec.Matches(rig.Mic.Read(endpoint), effects));
                if (device.Settings.Playback is { } sound) TestSuite.Assert(ApoPlaybackCodec.Matches(rig.Sound.Read(endpoint), sound));
                if (device.Settings.Sidetone is { } monitor) TestSuite.Assert(rig.Monitor.Read(endpoint).Settings == monitor);
            }
        });
        suite.Case("Setup skips disconnected members and explicitly applies connected members", () => {
            var rig = new Rig(); var setup = rig.Capture(); var b20 = rig.B20; rig.Audio.Endpoints.Remove(b20.Id);
            var results = rig.Controller.Apply(setup);
            TestSuite.Assert(results[0].Status == SetupApplyStatus.Missing && results.Count(r => r.Status == SetupApplyStatus.Applied) == 2 && rig.Audio.Writes == 2);
            rig.Audio.Endpoints[b20.Id] = b20;
            TestSuite.Assert(rig.Audio.Writes == 2, "Reconnection must not automatically apply a setup.");
        });
        suite.Case("All missing setup members cause no writes", () => {
            var rig = new Rig(); var setup = rig.Capture(); rig.Audio.Endpoints.Clear();
            TestSuite.Assert(rig.Controller.Apply(setup).All(r => r.Status == SetupApplyStatus.Missing) && rig.Audio.Writes == 0);
        });
        suite.Case("Setup matching tolerates endpoint GUID and friendly-name changes", () => {
            var rig = new Rig(); var setup = rig.Controller.Capture("Meeting", [rig.Output]); var output = rig.Output;
            rig.Audio.Endpoints.Remove(output.Id); var renamed = output with { Id = "new-guid", Name = "Desk speakers", AdapterName = "New name" };
            rig.Audio.Endpoints[renamed.Id] = renamed;
            var result = rig.Controller.Apply(setup).Single();
            TestSuite.Assert(result.Status == SetupApplyStatus.Applied && result.Endpoint?.Id == "new-guid");
        });
        suite.Case("A different USB instance is not substituted for a saved setup member", () => {
            var rig = new Rig(); var setup = rig.Controller.Capture("Meeting", [rig.Output]); var output = rig.Output;
            rig.Audio.Endpoints[output.Id] = output with { Usb = output.Usb! with { InstanceId = "SECOND" } };
            TestSuite.Assert(rig.Controller.Apply(setup).Single().Status == SetupApplyStatus.Missing && rig.Audio.Writes == 0);
        });
        suite.Case("Ambiguous setup identities are rejected before any device writes", () => {
            var rig = new Rig(); var setup = rig.Capture(); rig.Audio.Endpoints["duplicate"] = rig.Output with { Id = "duplicate" };
            TestSuite.Reject(() => rig.Controller.Apply(setup)); TestSuite.Assert(rig.Audio.Writes == 0);
        });
        suite.Case("Unavailable connected setup controls fail preflight before any writes", () => {
            var rig = new Rig(); var setup = rig.Capture(); rig.Mic.UnavailableId = setup.Devices.Last().Settings.EndpointId;
            TestSuite.Reject(() => rig.Controller.Apply(setup)); TestSuite.Assert(rig.Audio.Writes == 0);
        });
        suite.Case("Unsupported GSX high-pass in a setup rejects before any device is changed", () => {
            var rig = new Rig(); var setup = rig.Capture();
            var gsx = rig.Audio.Discover().Single(e => e.Direction == AudioDirection.Microphone && e.Usb?.ProductId == "0098");
            var beforeAudio = rig.Audio.Discover().ToDictionary(e => e.Id, e => rig.Audio.Read(e.Id));
            var beforeB20 = rig.Mic.Read(rig.B20); var beforeGsx = rig.Mic.Read(gsx); var beforeSound = rig.Sound.Read(rig.Output);
            var invalid = setup with { Devices = setup.Devices.Select(d => d.Settings.EndpointId == gsx.Id
                ? d with { Settings = d.Settings with { Effects = beforeGsx with {
                    Processing = beforeGsx.Processing! with { HighPassEnabled = !beforeGsx.Processing!.HighPassEnabled } } } }
                : d with { Settings = d.Settings with { Level = .01f, Muted = true } }).ToArray() };
            TestSuite.Reject(() => rig.Controller.Apply(invalid));
            TestSuite.Assert(rig.Audio.Writes == 0 && beforeAudio.All(p => rig.Audio.Read(p.Key) == p.Value) &&
                rig.Mic.Read(rig.B20) == beforeB20 && rig.Mic.Read(gsx) == beforeGsx && rig.Sound.Read(rig.Output) == beforeSound);
        });
        suite.Case("A newer change during setup application is preserved and remaining devices are not applied", () => {
            var rig = new Rig(); var setup = rig.Capture(); var output = rig.Output;
            rig.Audio.AfterWrite = id => { if (id == rig.B20.Id) rig.Audio.Set(output, .876f, true); };
            var results = rig.Controller.Apply(setup);
            TestSuite.Assert(results[0].Status == SetupApplyStatus.Applied && results[1].Status == SetupApplyStatus.Failed && results[2].Status == SetupApplyStatus.NotApplied);
            TestSuite.Assert(rig.Audio.Read(output.Id).Level == .876f && rig.Audio.Read(output.Id).Muted && rig.Audio.Writes == 1);
        });
        suite.Case("Setup capture refuses empty, duplicate or disconnected selections", () => {
            var rig = new Rig(); var b20 = rig.B20;
            TestSuite.Reject(() => rig.Controller.Capture("Gaming", []));
            TestSuite.Reject(() => rig.Controller.Capture("Gaming", [b20, b20]));
            rig.Audio.Endpoints.Remove(b20.Id); TestSuite.Reject(() => rig.Controller.Capture("Gaming", [b20]));
            TestSuite.Assert(rig.Audio.Writes == 0);
        });
        suite.Case("Setup capture refuses unavailable mapped settings instead of omitting them", () => {
            var rig = new Rig(); rig.Mic.UnavailableId = rig.B20.Id;
            TestSuite.Throws<IOException>(() => rig.Capture()); TestSuite.Assert(rig.Audio.Writes == 0);
        });
        suite.Case("Selection capture refreshes checked members only", () => {
            var rig = new Rig(); var setup = rig.Controller.Capture("Meeting", [rig.B20]); rig.Audio.Set(rig.B20, .76f, true);
            var replacement = rig.Controller.Capture(setup.Name, [rig.B20], [], setup);
            TestSuite.Assert(replacement.Devices.Count == 1 && replacement.Devices[0].Settings.Level == .76f && replacement.Devices[0].Settings.Muted && rig.Audio.Writes == 0);
        });
        suite.Case("A setup can keep only a checked disconnected snapshot under a new name", () => {
            var rig = new Rig(); var setup = rig.Capture(); rig.Audio.Endpoints.Remove(rig.B20.Id);
            var captured = rig.Controller.Capture("Offline mic", [], [setup.Devices[0]], setup);
            TestSuite.Assert(captured.Devices.Count == 1 && captured.Devices[0].Settings == setup.Devices[0].Settings with { Name = "Offline mic" } && rig.Audio.Writes == 0);
        });
        suite.Case("Setup JSON round trip retains all devices and complete processing snapshots", () => Scratch((store, _) => {
            var setup = new Rig().Capture(); store.Save(setup); var loaded = store.Load().Single();
            TestSuite.Assert(loaded.Name == setup.Name && loaded.SchemaVersion == 1 && loaded.Devices.SequenceEqual(setup.Devices));
            store.UpdateSelected(loaded, loaded); TestSuite.Assert(store.Load().Single().Devices.SequenceEqual(setup.Devices));
        }));
        suite.Case("Gaming Meeting and Streaming setups can share devices with different settings", () => Scratch((store, _) => {
            var rig = new Rig(); store.Save(rig.Capture()); rig.Audio.Set(rig.B20, .65f, true); store.Save(rig.Capture("Meeting")); store.Save(rig.Capture("Streaming"));
            var loaded = store.Load(); TestSuite.Assert(loaded.Count == 3 && loaded[0].Devices[0].Settings.Level != loaded[1].Devices[0].Settings.Level);
            store.Save(rig.Capture("gaming")); TestSuite.Assert(store.Load().Count == 3 && store.Load().Single(s => s.Name == "gaming").Devices[0].Settings.Muted);
        }));
        suite.Case("Updating a setup preserves other setups and rejects stale concurrent saves", () => Scratch((store, _) => {
            var rig = new Rig(); var setup = rig.Capture(); var meeting = rig.Capture("Meeting"); store.Save(setup); store.Save(meeting);
            rig.Audio.Set(rig.B20, .8f, true); var changed = rig.Controller.Capture(setup.Name, rig.Audio.Discover(), [], setup); store.UpdateSelected(setup, changed);
            TestSuite.Reject(() => store.UpdateSelected(setup, setup));
            TestSuite.Assert(store.Load().Single(s => s.Name == "Gaming").Devices[0].Settings.Level == .8f && store.Load().Single(s => s.Name == "Meeting").Devices.SequenceEqual(meeting.Devices));
        }));
        suite.Case("Selected setup updates its explicit membership while refusing a different name", () => Scratch((store, _) => {
            var setup = new Rig().Capture(); store.Save(setup);
            TestSuite.Reject(() => store.UpdateSelected(setup, setup with { Name = "Other" }));
            store.UpdateSelected(setup, setup with { Devices = setup.Devices.Take(1).ToArray() });
            TestSuite.Assert(store.Load().Single().Devices.SequenceEqual(setup.Devices.Take(1)));
            TestSuite.Reject(() => store.UpdateSelected(setup, setup));
        }));
        suite.Case("Applying an explicitly selected subset leaves every excluded device unchanged", () => {
            var rig = new Rig(); var setup = rig.Controller.Capture("Sound only", [rig.Output]);
            var mic = rig.B20; rig.Audio.Set(mic, .987f, true); var before = rig.Audio.Read(mic.Id);
            var processing = rig.Mic.Read(mic); var monitor = rig.Monitor.Read(mic);
            TestSuite.Assert(rig.Controller.Apply(setup).Single().Status == SetupApplyStatus.Applied && rig.Audio.Writes == 1);
            TestSuite.Assert(rig.Audio.Read(mic.Id) == before && rig.Mic.Read(mic) == processing && rig.Monitor.Read(mic) == monitor);
        });
        suite.Case("Explicitly included disconnected members keep their saved snapshots", () => {
            var rig = new Rig(); var old = rig.Capture(); var b20 = old.Devices.First(); rig.Audio.Endpoints.Remove(rig.B20.Id);
            var captured = rig.Controller.Capture("Gaming", rig.Audio.Discover(), [b20], old);
            TestSuite.Assert(captured.Devices.Count == 3 && captured.Devices.Contains(b20) && rig.Audio.Writes == 0);
        });
        suite.Case("Unchecking a disconnected member removes it from the selected setup", () => Scratch((store, _) => {
            var rig = new Rig(); var old = rig.Capture(); store.Save(old); rig.Audio.Endpoints.Remove(rig.B20.Id);
            var replacement = rig.Controller.Capture(old.Name, rig.Audio.Discover(), [], old);
            store.UpdateSelected(old, replacement);
            TestSuite.Assert(store.Load().Single().Devices.Count == 2 && store.Load().Single().Devices.All(d => d.Settings.DeviceIdentity != old.Devices[0].Settings.DeviceIdentity));
        }));
        suite.Case("Retaining a disconnected snapshot refuses unknown or reconnected members", () => {
            var rig = new Rig(); var old = rig.Capture(); var b20 = old.Devices[0];
            TestSuite.Reject(() => rig.Controller.Capture("Gaming", [], [b20], old));
            rig.Audio.Endpoints.Remove(rig.B20.Id);
            TestSuite.Reject(() => rig.Controller.Capture("Gaming", [], [b20]));
            TestSuite.Reject(() => rig.Controller.Capture("Gaming", [], [b20 with { Settings = b20.Settings with { Level = .99f } }], old));
            TestSuite.Assert(rig.Audio.Writes == 0);
        });
        suite.Case("Unavailable unchecked adapters do not block capture of checked device pages", () => {
            var rig = new Rig(); var old = rig.Capture(); rig.Mic.UnavailableId = rig.B20.Id;
            var subset = rig.Controller.Capture(old.Name, [rig.Output], [], old);
            TestSuite.Assert(subset.Devices.Count == 1 && subset.Devices[0].Settings.Playback is not null && rig.Audio.Writes == 0);
        });
        suite.Case("Setups are independent of named device profiles", () => Scratch((store, directory) => {
            var setup = new Rig().Capture(); store.Save(setup); var profiles = new ProfileStore(Path.Combine(directory, "profiles.json"));
            profiles.Save(setup.Devices[0].Settings with { Level = .99f });
            TestSuite.Assert(store.Load().Single().Devices[0].Settings.Level != .99f);
        }));
        suite.Case("Invalid setup values are rejected without replacing the last good file", () => Scratch((store, _) => {
            var setup = new Rig().Capture(); store.Save(setup); var bytes = File.ReadAllBytes(store.FilePath);
            foreach (var invalid in new[] { setup with { SchemaVersion = 2 }, setup with { Name = "" }, setup with { Devices = [] },
                setup with { Devices = [setup.Devices[0], setup.Devices[0]] }, setup with { Devices = [setup.Devices[0] with { Settings = setup.Devices[0].Settings with { Level = float.NaN } }] },
                setup with { Devices = [setup.Devices[0] with { Settings = setup.Devices[0].Settings with { Effects = setup.Devices[0].Settings.Effects! with { Processing = null } } }] } })
                TestSuite.Reject(() => store.Save(invalid));
            TestSuite.Assert(File.ReadAllBytes(store.FilePath).SequenceEqual(bytes));
        }));
        suite.Case("Malformed incomplete null or duplicate setup JSON is never overwritten", () => Scratch((store, _) => {
            var setup = new Rig().Capture();
            foreach (var data in new[] { "null", "[null]", "[{\"SchemaVersion\":1,\"Name\":\"Gaming\"}]", "[{", JsonSerializer.Serialize(new[] { setup, setup }) }) {
                File.WriteAllText(store.FilePath, data); TestSuite.Reject(() => store.Save(setup)); TestSuite.Assert(File.ReadAllText(store.FilePath) == data);
            }
        }));
        suite.Case("Setup writes use a bounded exclusive writer lock", () => Scratch((store, _) => {
            var setup = new Rig().Capture(); store.Save(setup); var bytes = File.ReadAllBytes(store.FilePath);
            using var held = new FileStream(store.FilePath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var watch = System.Diagnostics.Stopwatch.StartNew(); TestSuite.Throws<IOException>(() => store.Save(setup));
            TestSuite.Assert(watch.Elapsed < TimeSpan.FromSeconds(3) && File.ReadAllBytes(store.FilePath).SequenceEqual(bytes));
        }));
        suite.Case("Failed atomic setup replacement preserves the existing file and removes temporary data", () => Scratch((store, directory) => {
            var setup = new Rig().Capture(); store.Save(setup); var bytes = File.ReadAllBytes(store.FilePath);
            using (var held = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                try { store.Save(setup with { Name = "Meeting" }); throw new Exception("Expected atomic replacement to fail."); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            TestSuite.Assert(File.ReadAllBytes(store.FilePath).SequenceEqual(bytes) && Directory.GetFiles(directory, "*.tmp").Length == 0);
        }));
    }
}
