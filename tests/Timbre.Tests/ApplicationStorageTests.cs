using Timbre.Core;

internal static class ApplicationStorageTests
{
    private static readonly AudioEndpoint Mic = new DemoAudioBackend().Discover().First();
    private static readonly AudioProfile Profile = new("Meeting", Mic.Id, ProfileStore.DeviceIdentity(Mic), Mic.Direction, .4f, false);
    private static readonly SetupProfile Setup = new(1, "Meeting", [new(Mic.Name, Profile)]);
    private static readonly MicrophoneEffects Effects = new(34, 2, new(true, true, true, true, MicrophoneEqualizer.Flat));

    private static void Scratch(Action<string, string, string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "Timbre.Startup." + Guid.NewGuid().ToString("N"));
        var executable = Path.Combine(root, "app");
        var localData = Path.Combine(root, "local");
        Directory.CreateDirectory(executable);
        Directory.CreateDirectory(localData);
        try { action(root, executable, localData); }
        finally { Directory.Delete(root, true); }
    }

    private static void Empty(ApplicationStorage storage)
    {
        TestSuite.Assert(storage.Profiles.Load().Count == 0 && storage.Setups.Load().Count == 0 && storage.Processing.Load(Mic) is null);
        TestSuite.Assert(!Directory.Exists(storage.DirectoryPath), "Reading fresh startup stores must not create user data.");
    }

    public static void Run(TestSuite suite)
    {
        suite.Case("Live startup selects Timbre stores and does not create data on read", () => Scratch((_, executable, localData) => {
            var storage = ApplicationStorage.Open(executable, localData);
            TestSuite.Assert(storage.DirectoryPath == Path.Combine(localData, "Timbre"));
            TestSuite.Assert(storage.Profiles.FilePath == Path.Combine(storage.DirectoryPath, "profiles.json") &&
                storage.Setups.FilePath == Path.Combine(storage.DirectoryPath, "setups.json") &&
                storage.Processing.DirectoryPath == Path.Combine(storage.DirectoryPath, "processing-state"));
            Empty(storage);
        }));
        suite.Case("Live startup never imports or alters prior EPOS-Control or executable data", () => Scratch((root, executable, localData) => {
            var oldDirectory = Path.Combine(localData, "EPOS-Control");
            var oldProfiles = new ProfileStore(Path.Combine(oldDirectory, "profiles.json")); oldProfiles.Save(Profile);
            new SetupProfileStore(Path.Combine(oldDirectory, "setups.json")).Save(Setup);
            var oldProcessing = new ProcessingStateStore(Path.Combine(oldDirectory, "processing-state"));
            oldProcessing.Remember(Mic, Effects); oldProcessing.SetRestoreOnConnect(Mic, true);
            // Both previously used executable-side locations contain valid import candidates.
            new ProfileStore(Path.Combine(executable, "profiles.json")).Save(Profile);
            new ProfileStore(Path.Combine(executable, "data", "profiles.json")).Save(Profile);
            var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
            Empty(ApplicationStorage.Open(executable, localData));
            TestSuite.Assert(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().SequenceEqual(before.Keys.Order()));
            foreach (var (path, bytes) in before) TestSuite.Assert(File.ReadAllBytes(path).SequenceEqual(bytes), "Startup changed a previous data file.");
            TestSuite.Assert(oldProcessing.Load(Mic)!.RestoreOnConnect);
        }));
        suite.Case("Existing Timbre profiles setups and processing survive startup reload", () => Scratch((_, executable, localData) => {
            var storage = ApplicationStorage.Open(executable, localData);
            storage.Profiles.Save(Profile); storage.Setups.Save(Setup);
            var processing = storage.Processing.Remember(Mic, Effects); storage.Processing.SetRestoreOnConnect(Mic, true);
            var before = Directory.GetFiles(storage.DirectoryPath, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
            var reopened = ApplicationStorage.Open(executable, localData);
            TestSuite.Assert(reopened.Profiles.Load().Single() == Profile);
            var setup = reopened.Setups.Load().Single();
            TestSuite.Assert(setup.Name == Setup.Name && setup.Devices.SequenceEqual(Setup.Devices));
            TestSuite.Assert(reopened.Processing.Load(Mic) == processing with { RestoreOnConnect = true });
            foreach (var (path, bytes) in before) TestSuite.Assert(File.ReadAllBytes(path).SequenceEqual(bytes));
        }));
        suite.Case("Explicit data directory overrides live and demo storage for every store", () => Scratch((root, executable, localData) => {
            var custom = Path.Combine(root, "chosen");
            var storage = ApplicationStorage.Open(executable, localData, dataDirectory: custom);
            Empty(storage); storage.Profiles.Save(Profile); storage.Setups.Save(Setup); storage.Processing.Remember(Mic, Effects);
            var reopened = ApplicationStorage.Open(executable, localData, demo: true, dataDirectory: custom);
            TestSuite.Assert(reopened.DirectoryPath == custom && reopened.Profiles.Load().Single() == Profile &&
                reopened.Setups.Load().Single().Name == Setup.Name && reopened.Processing.Load(Mic)!.Effects == Effects);
            TestSuite.Assert(!Directory.Exists(Path.Combine(localData, "Timbre")) && !Directory.Exists(Path.Combine(executable, "data", "demo")));
        }));
        suite.Case("Demo startup uses separate stores and cannot read live profiles", () => Scratch((_, executable, localData) => {
            ApplicationStorage.Open(executable, localData).Profiles.Save(Profile);
            var demo = ApplicationStorage.Open(executable, localData, demo: true);
            TestSuite.Assert(demo.DirectoryPath == Path.Combine(executable, "data", "demo")); Empty(demo);
            demo.Profiles.Save(Profile with { Name = "Demo" });
            TestSuite.Assert(ApplicationStorage.Open(executable, localData).Profiles.Load().Single() == Profile);
        }));
        suite.Case("Render verification ignores chosen data and isolates each run", () => Scratch((root, executable, localData) => {
            var chosen = Path.Combine(root, "chosen");
            var storage = ApplicationStorage.Open(executable, localData, dataDirectory: chosen); storage.Profiles.Save(Profile);
            var render = Path.Combine(root, "reports", "preview.png");
            var first = ApplicationStorage.Open(executable, localData, demo: true, dataDirectory: chosen, renderDestination: render);
            var second = ApplicationStorage.Open(executable, localData, demo: true, dataDirectory: chosen, renderDestination: render);
            TestSuite.Assert(Path.GetDirectoryName(first.DirectoryPath) == Path.GetDirectoryName(render) && first.DirectoryPath != second.DirectoryPath);
            Empty(first); Empty(second); first.Profiles.Save(Profile with { Name = "Preview" });
            TestSuite.Assert(storage.Profiles.Load().Single() == Profile && second.Profiles.Load().Count == 0);
            TestSuite.Assert(!Directory.Exists(Path.Combine(localData, "Timbre")) && !Directory.Exists(Path.Combine(executable, "data", "demo")));
        }));
    }
}
