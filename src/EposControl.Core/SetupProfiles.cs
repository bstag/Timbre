using System.Text.Json;
using System.Text.Json.Serialization;

namespace EposControl.Core;

public sealed record SetupDeviceProfile([property: JsonRequired] string DeviceName, [property: JsonRequired] AudioProfile Settings);
public sealed record SetupProfile([property: JsonRequired] int SchemaVersion, [property: JsonRequired] string Name,
    [property: JsonRequired] IReadOnlyList<SetupDeviceProfile> Devices);

// A setup owns snapshots, never references to mutable named device profiles.
public sealed class SetupProfileStore(string path)
{
    public string FilePath { get; } = Path.GetFullPath(path);
    public IReadOnlyList<SetupProfile> Load()
    {
        if (!File.Exists(FilePath)) return [];
        List<SetupProfile> setups;
        try { setups = JsonSerializer.Deserialize<List<SetupProfile>>(File.ReadAllText(FilePath)) ?? throw new InvalidDataException("Setup list is missing."); }
        catch (JsonException ex) { throw new InvalidDataException("Setup data is malformed or incomplete.", ex); }
        foreach (var setup in setups) Validate(setup);
        if (setups.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != setups.Count)
            throw new InvalidDataException("Setup names must be unique.");
        return setups;
    }
    public void Save(SetupProfile setup)
    {
        Validate(setup);
        using var guard = Lock();
        Write(Load().Where(s => !s.Name.Equals(setup.Name, StringComparison.OrdinalIgnoreCase)).Append(setup).ToArray());
    }
    public void UpdateSelected(SetupProfile expected, SetupProfile replacement)
    {
        Validate(expected); Validate(replacement);
        if (!expected.Name.Equals(replacement.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Save as setup to change its name.");
        using var guard = Lock();
        var setups = Load().ToList();
        var index = setups.FindIndex(s => s.Name.Equals(expected.Name, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || !Equivalent(setups[index], expected))
            throw new InvalidOperationException("The selected setup changed or was removed. Reload setups before saving.");
        setups[index] = replacement with { Name = expected.Name };
        Write(setups);
    }
    public static void Validate(SetupProfile? setup)
    {
        if (setup is null || setup.SchemaVersion != 1) throw new InvalidDataException("Unsupported or missing setup format.");
        if (string.IsNullOrWhiteSpace(setup.Name) || setup.Name.Length > 80) throw new InvalidDataException("Setup names must contain 1–80 characters.");
        if (setup.Devices is null || setup.Devices.Count is < 1 or > 64) throw new InvalidDataException("Choose at least one device page for the setup (maximum 64).");
        foreach (var device in setup.Devices) {
            if (device is null || string.IsNullOrWhiteSpace(device.DeviceName) || device.Settings is null)
                throw new InvalidDataException("Setup device settings are missing.");
            ProfileStore.Validate(device.Settings);
            if (device.Settings.Effects is { Processing: null } || device.Settings.Playback is { Equalizer: null })
                throw new InvalidDataException("Setups require complete processing settings.");
        }
        if (setup.Devices.Select(d => (d.Settings.DeviceIdentity, d.Settings.Direction)).Distinct().Count() != setup.Devices.Count)
            throw new InvalidDataException("A device page may appear only once in a setup.");
    }
    private static bool Equivalent(SetupProfile a, SetupProfile b) => a.SchemaVersion == b.SchemaVersion && a.Name == b.Name && a.Devices.SequenceEqual(b.Devices);
    private FileStream Lock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true) {
            try { return new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (started.ElapsedMilliseconds < 1000) { Thread.Sleep(25); }
        }
    }
    private void Write(IReadOnlyList<SetupProfile> setups)
    {
        var temp = Path.Combine(Path.GetDirectoryName(FilePath)!, Guid.NewGuid().ToString("N") + ".tmp");
        try {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(setups, new JsonSerializerOptions { WriteIndented = true });
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
            File.Move(temp, FilePath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public enum SetupApplyStatus { Applied, Missing, Failed, NotApplied }
public sealed record SetupApplyResult(SetupDeviceProfile Device, AudioEndpoint? Endpoint, SetupApplyStatus Status, string Message);

// Coordinates existing guarded adapters. Device discovery and capture are read-only.
public sealed class SetupProfileController(IAudioBackend audio, IMicrophoneEffectsBackend effects, ISidetoneBackend sidetone, IPlaybackEffectsBackend playback, IGsxSidetoneBackend? gsxSidetone = null)
{
    public SetupProfile Capture(string name, IReadOnlyList<AudioEndpoint> selected,
        IReadOnlyList<SetupDeviceProfile>? retainedDisconnected = null, SetupProfile? previous = null)
    {
        if (previous is not null) SetupProfileStore.Validate(previous);
        var connected = audio.Discover();
        var devices = selected.Select(endpoint => {
            var current = connected.SingleOrDefault(e => e.Id == endpoint.Id && ProfileStore.DeviceIdentity(e) == ProfileStore.DeviceIdentity(endpoint))
                ?? throw new InvalidOperationException(endpoint.Name + " disconnected or changed. Refresh before saving.");
            var captured = CaptureDevice(current, name);
            var old = previous?.Devices.FirstOrDefault(d => ProfileStore.BelongsTo(current, d.Settings));
            if (old is not null) RequireSavedControls(old.Settings, captured);
            return new SetupDeviceProfile(current.Label, captured);
        }).ToList();
        foreach (var retained in retainedDisconnected ?? []) {
            // A disconnected member can keep its existing snapshot only through explicit inclusion.
            if (previous is null || !previous.Devices.Contains(retained))
                throw new InvalidOperationException("Disconnected pages must come from the selected saved setup.");
            if (Resolve(connected, retained.Settings) is not null)
                throw new InvalidOperationException("A device reconnected. Refresh before saving its current settings.");
            devices.Add(retained with { Settings = retained.Settings with { Name = name } });
        }
        var setup = new SetupProfile(1, name, devices); SetupProfileStore.Validate(setup); return setup;
    }
    private static void RequireSavedControls(AudioProfile previous, AudioProfile captured)
    {
        if (previous.Effects is not null && captured.Effects is null || previous.Sidetone is not null && captured.Sidetone is null || previous.Playback is not null && captured.Playback is null || previous.GsxSidetone is not null && captured.GsxSidetone is null)
            throw new InvalidOperationException("Some saved controls are unavailable. Their settings were kept; refresh before saving.");
    }
    private AudioProfile CaptureDevice(AudioEndpoint endpoint, string name)
    {
        var state = audio.Read(endpoint.Id);
        var usb = endpoint.Usb;
        var mapped = usb?.VendorId.Equals("1395", StringComparison.OrdinalIgnoreCase) == true;
        var b20 = mapped && usb!.ProductId.Equals("009F", StringComparison.OrdinalIgnoreCase);
        var gsx = mapped && usb!.ProductId.Equals("0098", StringComparison.OrdinalIgnoreCase);
        // A mapped adapter that is temporarily unavailable aborts capture, rather than silently dropping controls.
        var mic = endpoint.Direction == AudioDirection.Microphone && (b20 || gsx) ? effects.Read(endpoint) : null;
        var monitor = endpoint.Direction == AudioDirection.Microphone && b20 ? sidetone.Read(endpoint).Settings : null;
        var sound = endpoint.Direction == AudioDirection.Playback && gsx ? playback.Read(endpoint) : null;
        var gsxMonitor = endpoint.Direction == AudioDirection.Microphone && gsx && gsxSidetone is not null ? gsxSidetone.Read(endpoint).Settings : null;
        return new(name, endpoint.Id, ProfileStore.DeviceIdentity(endpoint), endpoint.Direction, state.Level, state.Muted, mic, monitor, sound, gsxMonitor);
    }
    private static AudioEndpoint? Resolve(IReadOnlyList<AudioEndpoint> connected, AudioProfile profile)
    {
        var candidates = connected.Where(e => ProfileStore.BelongsTo(e, profile)).ToArray();
        if (candidates.Length > 1) throw new InvalidOperationException("Multiple connected endpoints match a saved device page. Resolve the ambiguity before applying or saving.");
        return candidates.SingleOrDefault();
    }
    public IReadOnlyList<SetupApplyResult> Apply(SetupProfile setup)
    {
        SetupProfileStore.Validate(setup);
        var connected = audio.Discover();
        var plans = new List<Plan>();
        var results = new List<SetupApplyResult>();
        // Preflight every connected member before writing any device. Missing members are skipped explicitly.
        foreach (var device in setup.Devices) {
            var endpoint = Resolve(connected, device.Settings);
            if (endpoint is null) { results.Add(new(device, null, SetupApplyStatus.Missing, "Disconnected; skipped.")); continue; }
            try {
                var level = audio.Read(endpoint.Id);
                var mic = device.Settings.Effects is not null ? effects.Read(endpoint) : null;
                var monitor = device.Settings.Sidetone is not null ? sidetone.Read(endpoint) : null;
                var sound = device.Settings.Playback is not null ? playback.Read(endpoint) : null;
                var gsxMonitor = device.Settings.GsxSidetone is not null ? (gsxSidetone ?? throw new InvalidOperationException("GSX sidetone adapter unavailable.")).Read(endpoint) : null;
                if (mic is not null) MicrophoneEffects.ValidateUpdate(endpoint, mic, device.Settings.Effects!);
                monitor?.Validate();
                if (sound is not null) PlaybackEffects.ValidateUpdate(sound, device.Settings.Playback!);
                plans.Add(new(device, endpoint, level, mic, monitor, sound, gsxMonitor));
            } catch (Exception ex) { throw new InvalidOperationException("Setup was not applied. Cannot read controls for " + device.DeviceName + ": " + ex.Message, ex); }
        }
        var failed = false;
        foreach (var plan in plans) {
            if (failed) { results.Add(new(plan.Device, plan.Endpoint, SetupApplyStatus.NotApplied, "Not applied after another device failed.")); continue; }
            try {
                var fresh = Resolve(audio.Discover(), plan.Device.Settings);
                if (fresh?.Id != plan.Endpoint.Id) throw new InvalidOperationException("Device disconnected or changed during setup application.");
                var now = audio.Read(plan.Endpoint.Id);
                if (Math.Abs(now.Level - plan.Level.Level) > .0001f || now.Muted != plan.Level.Muted ||
                    plan.Mic is not null && !ApoMicrophoneCodec.Matches(effects.Read(plan.Endpoint), plan.Mic) ||
                    plan.Monitor is not null && !SidetoneBackend.Matches(sidetone.Read(plan.Endpoint), plan.Monitor) ||
                    plan.Sound is not null && !ApoPlaybackCodec.Matches(playback.Read(plan.Endpoint), plan.Sound) ||
                    plan.GsxMonitor is not null && gsxSidetone!.Read(plan.Endpoint) != plan.GsxMonitor)
                    throw new InvalidOperationException("Settings changed during setup application. Newer settings were preserved.");
                ProfileStore.Apply(audio, plan.Endpoint, plan.Device.Settings, effects, sidetone, playback, gsxSidetone);
                results.Add(new(plan.Device, plan.Endpoint, SetupApplyStatus.Applied, "Applied."));
            } catch (Exception ex) { failed = true; results.Add(new(plan.Device, plan.Endpoint, SetupApplyStatus.Failed, ex.Message)); }
        }
        // Successful earlier devices remain applied on a later failure; the caller must report the partial result.
        return setup.Devices.Select(d => results.Single(r => r.Device == d)).ToArray();
    }
    private sealed record Plan(SetupDeviceProfile Device, AudioEndpoint Endpoint, AudioState Level, MicrophoneEffects? Mic, SidetoneState? Monitor, PlaybackEffects? Sound, GsxSidetoneState? GsxMonitor);
}
