using EposControl.Core;

internal static class ProfileWorkflowTests
{
    private static readonly AudioEndpoint Mic = new DemoAudioBackend().Discover().First();
    private static AudioProfile Profile(AudioEndpoint endpoint, string name = "Everyday") => new(name, endpoint.Id,
        ProfileStore.DeviceIdentity(endpoint), endpoint.Direction, .4f, false);
    private static void Scratch(Action<ProfileStore, string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "epos-profile-workflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(new(Path.Combine(directory, "profiles.json")), directory); }
        finally { foreach (var file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
    }
    public static void Run(TestSuite suite)
    {
        suite.Case("Device profiles survive friendly-name and endpoint GUID changes", () => Scratch((store, _) => {
            var profile = Profile(Mic); store.SaveForDevice(Mic, profile);
            var renamed = Mic with { Id = "new-guid", Name = "Desk microphone", AdapterName = "Renamed audio device" };
            TestSuite.Assert(store.ForDevice(renamed).Single() == profile && ProfileStore.BelongsTo(renamed, profile));
        }));
        suite.Case("Profile binding includes vendor, model, USB instance and audio page", () => Scratch((store, _) => {
            store.SaveForDevice(Mic, Profile(Mic));
            foreach (var other in new[] { Mic with { Direction = AudioDirection.Playback }, Mic with { Usb = Mic.Usb! with { VendorId = "1234" } },
                Mic with { Usb = Mic.Usb! with { ProductId = "0098" } }, Mic with { Usb = Mic.Usb! with { InstanceId = "SECOND" } }, Mic with { Usb = null } })
                TestSuite.Assert(store.ForDevice(other).Count == 0);
        }));
        suite.Case("USB profile identity normalizes instance and model casing", () => {
            TestSuite.Assert(ProfileStore.DeviceIdentity(Mic) == ProfileStore.DeviceIdentity(Mic with { Usb = Mic.Usb! with { InstanceId = Mic.Usb.InstanceId.ToLowerInvariant(), ProductId = "009f" } }));
        });
        suite.Case("Same profile name is independent on each device and page", () => Scratch((store, _) => {
            var output = new DemoAudioBackend().Discover().First(e => e.Direction == AudioDirection.Playback);
            store.SaveForDevice(Mic, Profile(Mic)); store.SaveForDevice(output, Profile(output));
            TestSuite.Assert(store.Load().Count == 2 && store.ForDevice(Mic).Single().DeviceIdentity != store.ForDevice(output).Single().DeviceIdentity);
        }));
        suite.Case("Save to selected updates its values and preserves its name", () => Scratch((store, _) => {
            var original = Profile(Mic); store.SaveForDevice(Mic, original);
            store.UpdateSelected(Mic, original, original with { Level = .7f, Muted = true });
            TestSuite.Assert(store.ForDevice(Mic).Single() == original with { Level = .7f, Muted = true });
        }));
        suite.Case("Save to selected refuses a stale profile without replacing newer values", () => Scratch((store, _) => {
            var original = Profile(Mic); store.SaveForDevice(Mic, original); store.SaveForDevice(Mic, original with { Level = .8f });
            TestSuite.Reject(() => store.UpdateSelected(Mic, original, original with { Level = .7f }));
            TestSuite.Assert(store.ForDevice(Mic).Single().Level == .8f);
        }));
        suite.Case("Save to selected refuses a different device, page or name", () => Scratch((store, _) => {
            var original = Profile(Mic); store.SaveForDevice(Mic, original);
            TestSuite.Reject(() => store.UpdateSelected(Mic with { Direction = AudioDirection.Playback }, original, original));
            TestSuite.Reject(() => store.UpdateSelected(Mic, original, original with { Name = "Other" }));
            TestSuite.Assert(store.ForDevice(Mic).Single() == original);
        }));
        suite.Case("Legacy device profiles remain readable and upgrade on explicit save", () => Scratch((store, _) => {
            var old = Profile(Mic) with { DeviceIdentity = Mic.ProfileIdentity }; store.Save(old);
            TestSuite.Assert(store.ForDevice(Mic).Single() == old);
            store.UpdateSelected(Mic, old, old with { Level = .6f });
            TestSuite.Assert(store.Load().Single().DeviceIdentity == ProfileStore.DeviceIdentity(Mic));
        }));
        suite.Case("Save as same device/name replaces legacy entry without duplicates", () => Scratch((store, _) => {
            store.Save(Profile(Mic) with { DeviceIdentity = Mic.ProfileIdentity }); store.SaveForDevice(Mic, Profile(Mic, "everyday"));
            TestSuite.Assert(store.Load().Count == 1 && store.Load().Single().Name == "everyday");
        }));
        suite.Case("Profile import validates and preserves the legacy file", () => Scratch((store, directory) => {
            var source = Path.Combine(directory, "legacy.json"); var legacy = new ProfileStore(source); legacy.Save(Profile(Mic)); var bytes = File.ReadAllBytes(source);
            TestSuite.Assert(store.ImportIfMissing(source) && store.Load().Single() == Profile(Mic) && File.ReadAllBytes(source).SequenceEqual(bytes));
            legacy.Save(Profile(Mic) with { Level = .8f });
            TestSuite.Assert(!store.ImportIfMissing(source) && store.Load().Single().Level == .4f);
        }));
        suite.Case("Absent legacy file creates no profile file", () => Scratch((store, directory) => {
            TestSuite.Assert(!store.ImportIfMissing(Path.Combine(directory, "absent.json")) && !File.Exists(store.FilePath));
        }));
        suite.Case("Invalid profile import does not create or overwrite destination", () => Scratch((store, directory) => {
            var source = Path.Combine(directory, "legacy.json"); File.WriteAllText(source, "[{\"Name\":\"Bad\",\"EndpointId\":\"x\",\"DeviceIdentity\":\"x\",\"Level\":9}]");
            TestSuite.Reject(() => store.ImportIfMissing(source)); TestSuite.Assert(!File.Exists(store.FilePath));
            store.Save(Profile(Mic)); TestSuite.Assert(!store.ImportIfMissing(source) && store.Load().Single() == Profile(Mic));
        }));
        suite.Case("Invalid selected-profile values leave its file unchanged", () => Scratch((store, _) => {
            var original = Profile(Mic); store.SaveForDevice(Mic, original); var bytes = File.ReadAllBytes(store.FilePath);
            TestSuite.Reject(() => store.UpdateSelected(Mic, original, original with { Level = float.NaN }));
            TestSuite.Assert(File.ReadAllBytes(store.FilePath).SequenceEqual(bytes));
        }));
        suite.Case("Profile writers fail within a bounded wait when another writer owns the file", () => Scratch((store, _) => {
            store.Save(Profile(Mic)); var before = File.ReadAllBytes(store.FilePath);
            using var held = new FileStream(store.FilePath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var elapsed = System.Diagnostics.Stopwatch.StartNew(); TestSuite.Throws<IOException>(() => store.Save(Profile(Mic) with { Level = .6f }));
            TestSuite.Assert(elapsed.Elapsed < TimeSpan.FromSeconds(3) && File.ReadAllBytes(store.FilePath).SequenceEqual(before));
        }));
        suite.Case("Failed profile replacement preserves data and cleans temporary files", () => Scratch((store, directory) => {
            var original = Profile(Mic); store.Save(original); var bytes = File.ReadAllBytes(store.FilePath);
            using (var held = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                var failed = false;
                try { store.UpdateSelected(Mic, original, original with { Level = .6f }); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
                TestSuite.Assert(failed);
            }
            TestSuite.Assert(File.ReadAllBytes(store.FilePath).SequenceEqual(bytes) && !Directory.EnumerateFiles(directory, "*.tmp").Any());
        }));
        suite.Case("Incomplete or null profiles fail closed before replacing saved data", () => Scratch((store, _) => {
            foreach (var content in new[] { "null", "[null]", "[{\"Name\":\"Partial\",\"EndpointId\":\"x\",\"DeviceIdentity\":\"x\"}]" }) {
                File.WriteAllText(store.FilePath, content); TestSuite.Reject(() => store.Save(Profile(Mic)));
                TestSuite.Assert(File.ReadAllText(store.FilePath) == content);
            }
        }));
    }
}
