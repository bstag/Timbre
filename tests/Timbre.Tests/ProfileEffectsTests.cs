using System.Text.Json;
using Timbre.Core;

internal static class ProfileEffectsTests
{
    public static void Run(TestSuite suite)
    {
        var mic = new DemoAudioBackend().Discover().First();
        AudioProfile Profile() => new("Processing", mic.Id, mic.ProfileIdentity, mic.Direction, .6f, true, new(31, 1));
        suite.Case("Old profile JSON loads without effects", () => {
            var json = "{\"Name\":\"Old\",\"EndpointId\":\"id\",\"DeviceIdentity\":\"usb\",\"Direction\":1,\"Level\":0.5,\"Muted\":false}";
            TestSuite.Assert(JsonSerializer.Deserialize<AudioProfile>(json)!.Effects is null);
        });
        suite.Case("Effects profile JSON round-trips", () => { var p = Profile(); TestSuite.Assert(JsonSerializer.Deserialize<AudioProfile>(JsonSerializer.Serialize(p)) == p); });
        suite.Case("Effects profile applies level, mute, gate and filter", () => {
            var audio = new DemoAudioBackend(); var effects = new DemoMicrophoneEffectsBackend();
            ProfileStore.Apply(audio, mic, Profile(), effects);
            TestSuite.Assert(audio.Read(mic.Id).Level == .6f && audio.Read(mic.Id).Muted && ApoMicrophoneCodec.Matches(effects.Read(mic), new(31, 1)));
        });
        suite.Case("Missing effects adapter rejects profile before audio writes", () => { var audio = new CountingAudio(); TestSuite.Reject(() => ProfileStore.Apply(audio, mic, Profile())); TestSuite.Assert(audio.Writes == 0); });
        suite.Case("Effects read failure rejects profile before audio writes", () => { var audio = new CountingAudio(); TestSuite.Throws<IOException>(() => ProfileStore.Apply(audio, mic, Profile(), new FailingEffects(true))); TestSuite.Assert(audio.Writes == 0); });
        suite.Case("Effects apply failure restores endpoint level and mute", () => { var audio = new CountingAudio(); var before = audio.Read(mic.Id); TestSuite.Throws<IOException>(() => ProfileStore.Apply(audio, mic, Profile(), new FailingEffects(false))); TestSuite.Assert(audio.Read(mic.Id) == before && audio.Writes == 2); });
        suite.Case("Profile rollback failure reports both errors", () => { var audio = new CountingAudio { FailWrite = 2 }; var error = TestSuite.Throws<AggregateException>(() => ProfileStore.Apply(audio, mic, Profile(), new FailingEffects(false))); TestSuite.Assert(error.InnerExceptions.Count == 2); });
        suite.Case("Profile failure preserves concurrent endpoint edits", () => { var audio = new CountingAudio(); var effects = new FailingEffects(false) { BeforeFailure = () => audio.Apply(mic.Id, .9f, false) }; TestSuite.Throws<AggregateException>(() => ProfileStore.Apply(audio, mic, Profile(), effects)); TestSuite.Assert(audio.Read(mic.Id).Level == .9f && !audio.Read(mic.Id).Muted && audio.Writes == 2); });
        suite.Case("Invalid profile gate rejects before any writes", () => { var audio = new CountingAudio(); TestSuite.Reject(() => ProfileStore.Apply(audio, mic, Profile() with { Effects = new(float.NaN, 1) }, new DemoMicrophoneEffectsBackend())); TestSuite.Assert(audio.Writes == 0); });
        suite.Case("Playback profile cannot contain mic effects", () => { var audio = new CountingAudio(); var playback = audio.Discover().First(e => e.Direction == AudioDirection.Playback); var p = Profile() with { Direction = AudioDirection.Playback, DeviceIdentity = playback.ProfileIdentity }; TestSuite.Reject(() => ProfileStore.Apply(audio, playback, p, new DemoMicrophoneEffectsBackend())); TestSuite.Assert(audio.Writes == 0); });
        suite.Case("Foreign effects profile rejects before writes", () => { var audio = new CountingAudio(); TestSuite.Reject(() => ProfileStore.Apply(audio, mic with { Usb = mic.Usb! with { InstanceId = "OTHER" } }, Profile(), new DemoMicrophoneEffectsBackend())); TestSuite.Assert(audio.Writes == 0); });
        suite.Case("Profile store persists effects without losing legacy profiles", () => {
            var directory = Path.Combine(Path.GetTempPath(), "timbre-profile-effects-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); var path = Path.Combine(directory, "profiles.json");
            try { var store = new ProfileStore(path); store.Save(Profile()); store.Save(Profile() with { Name = "Legacy", Effects = null }); TestSuite.Assert(store.Load().Count == 2 && store.Load().Single(p => p.Name == "Processing").Effects == new MicrophoneEffects(31, 1)); }
            finally { File.Delete(path); File.Delete(path + ".lock"); Directory.Delete(directory); }
        });
    }
    private sealed class CountingAudio : IAudioBackend
    {
        private readonly DemoAudioBackend inner = new();
        public int Writes, FailWrite;
        public IReadOnlyList<AudioEndpoint> Discover() => inner.Discover();
        public AudioState Read(string id) => inner.Read(id);
        public AudioState Apply(string id, float level, bool muted) { if (++Writes == FailWrite) throw new IOException("Injected audio apply failure"); return inner.Apply(id, level, muted); }
    }
    private sealed class FailingEffects(bool failRead) : IMicrophoneEffectsBackend
    {
        public Action? BeforeFailure;
        public MicrophoneEffects Read(AudioEndpoint endpoint) => failRead ? throw new IOException("Injected effects read failure") : new(0, 2);
        public MicrophoneEffects Apply(AudioEndpoint endpoint, MicrophoneEffects expected, MicrophoneEffects desired) { BeforeFailure?.Invoke(); throw new IOException("Injected effects apply failure"); }
    }
}
