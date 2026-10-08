using System.Text.Json;
using System.Text.Json.Serialization;

namespace EposControl.Core;

public sealed record AudioProfile([property: JsonRequired] string Name, [property: JsonRequired] string EndpointId,
    [property: JsonRequired] string DeviceIdentity, [property: JsonRequired] AudioDirection Direction,
    [property: JsonRequired] float Level, [property: JsonRequired] bool Muted,
    MicrophoneEffects? Effects = null, SidetoneSettings? Sidetone = null, PlaybackEffects? Playback = null, GsxSidetoneSettings? GsxSidetone = null);
public sealed class ProfileStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string FilePath { get; } = Path.GetFullPath(path);
    public static string DeviceIdentity(AudioEndpoint endpoint) => endpoint.Usb is { } usb
        ? $"usb-v1:{usb.VendorId.ToUpperInvariant()}:{usb.ProductId.ToUpperInvariant()}:{usb.InstanceId.ToUpperInvariant()}|{endpoint.Direction}"
        : endpoint.Id;
    public static bool BelongsTo(AudioEndpoint endpoint, AudioProfile profile) => endpoint.Direction == profile.Direction &&
        (profile.DeviceIdentity.Equals(DeviceIdentity(endpoint), StringComparison.Ordinal) || profile.DeviceIdentity == endpoint.ProfileIdentity);
    public IReadOnlyList<AudioProfile> ForDevice(AudioEndpoint endpoint) => Load().Where(p => BelongsTo(endpoint, p)).ToArray();
    public IReadOnlyList<AudioProfile> Load()
    {
        if (!File.Exists(FilePath)) return [];
        List<AudioProfile> profiles;
        try { profiles = JsonSerializer.Deserialize<List<AudioProfile>>(File.ReadAllText(FilePath)) ?? throw new InvalidDataException("Profile list is missing."); }
        catch (JsonException ex) { throw new InvalidDataException("Profile data is malformed or incomplete.", ex); }
        if (profiles.Any(p => p is null)) throw new InvalidDataException("Profile entry is missing.");
        foreach (var profile in profiles) Validate(profile);
        return profiles;
    }
    public void Save(AudioProfile profile)
    {
        Validate(profile);
        using var guard = Lock();
        var profiles = Load().Where(p => !(p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)
            && p.DeviceIdentity == profile.DeviceIdentity && p.Direction == profile.Direction)).Append(profile).ToList();
        Write(profiles);
    }
    public void SaveForDevice(AudioEndpoint endpoint, AudioProfile profile)
    {
        Validate(profile);
        if (!BelongsTo(endpoint, profile)) throw new InvalidOperationException("This profile belongs to another device or audio direction.");
        using var guard = Lock();
        var profiles = Load().Where(p => !(BelongsTo(endpoint, p) && p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)))
            .Append(profile with { DeviceIdentity = DeviceIdentity(endpoint) }).ToList();
        Write(profiles);
    }
    public void UpdateSelected(AudioEndpoint endpoint, AudioProfile expected, AudioProfile replacement)
    {
        Validate(expected); Validate(replacement);
        if (!BelongsTo(endpoint, expected) || !BelongsTo(endpoint, replacement) || !expected.Name.Equals(replacement.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a profile for this device before saving to it.");
        using var guard = Lock();
        var profiles = Load().ToList();
        var index = profiles.FindIndex(p => p.DeviceIdentity == expected.DeviceIdentity && p.Direction == expected.Direction && p.Name.Equals(expected.Name, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || profiles[index] != expected) throw new InvalidOperationException("The selected profile changed or was removed. Reload profiles before saving.");
        var updated = replacement with { Name = expected.Name, DeviceIdentity = DeviceIdentity(endpoint) };
        if (profiles.Where((_, i) => i != index).Any(p => BelongsTo(endpoint, p) && p.Name.Equals(updated.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Another profile already uses this name for the device.");
        profiles[index] = updated; Write(profiles);
    }
    public bool ImportIfMissing(string legacyPath)
    {
        if (File.Exists(FilePath) || !File.Exists(legacyPath)) return false;
        var legacy = new ProfileStore(legacyPath).Load();
        using var guard = Lock();
        if (File.Exists(FilePath)) return false;
        Write(legacy, false); return true;
    }
    private FileStream Lock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true) {
            try { return new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (started.ElapsedMilliseconds < 1000) { Thread.Sleep(25); }
        }
    }
    private void Write(IReadOnlyList<AudioProfile> profiles, bool overwrite = true)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(profiles, JsonOptions);
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
            File.Move(temp, FilePath, overwrite);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static AudioState Apply(IAudioBackend backend, AudioEndpoint endpoint, AudioProfile profile, IMicrophoneEffectsBackend? effects = null, ISidetoneBackend? sidetone = null, IPlaybackEffectsBackend? playback = null, IGsxSidetoneBackend? gsxSidetone = null)
    {
        Validate(profile);
        if (!BelongsTo(endpoint, profile))
            throw new InvalidOperationException("This profile belongs to another device or audio direction.");
        if (profile.Effects is null && profile.Sidetone is null && profile.Playback is null && profile.GsxSidetone is null) return backend.Apply(endpoint.Id, profile.Level, profile.Muted);
        if (profile.Effects is not null && effects is null) throw new InvalidOperationException("This profile requires an available microphone effects adapter.");
        if (profile.Sidetone is not null && sidetone is null) throw new InvalidOperationException("This profile requires an available sidetone adapter.");
        if (profile.Playback is not null && playback is null) throw new InvalidOperationException("This profile requires an available playback effects adapter.");
        if (profile.GsxSidetone is not null) { GsxSidetoneProtocol.ValidateEndpoint(endpoint); if (gsxSidetone is null) throw new InvalidOperationException("This profile requires a GSX sidetone adapter."); }
        var beforeEffects = profile.Effects is not null ? effects!.Read(endpoint) : null;
        var beforeSidetone = profile.Sidetone is not null ? sidetone!.Read(endpoint) : null;
        var beforePlayback = profile.Playback is not null ? playback!.Read(endpoint) : null;
        var beforeGsxSidetone = profile.GsxSidetone is not null ? gsxSidetone!.Read(endpoint) : null;
        if (beforeEffects is not null) MicrophoneEffects.ValidateUpdate(endpoint, beforeEffects, profile.Effects!);
        if (beforePlayback is not null) PlaybackEffects.ValidateUpdate(beforePlayback, profile.Playback!);
        beforeSidetone?.Validate();
        var beforeAudio = backend.Read(endpoint.Id);
        var result = backend.Apply(endpoint.Id, profile.Level, profile.Muted);
        MicrophoneEffects? ownedEffects = null;
        try {
            if (beforeEffects is not null) ownedEffects = effects!.Apply(endpoint, beforeEffects, profile.Effects!);
            if (beforeSidetone is not null) sidetone!.Apply(endpoint, beforeSidetone, profile.Sidetone!);
            if (beforePlayback is not null) playback!.Apply(endpoint, beforePlayback, profile.Playback!);
            if (beforeGsxSidetone is not null) gsxSidetone!.Apply(endpoint, beforeGsxSidetone, profile.GsxSidetone!);
        }
        catch (Exception failure) {
            var errors = new List<Exception> { failure };
            if (ownedEffects is not null) {
                try { effects!.Apply(endpoint, ownedEffects, beforeEffects!); }
                catch (Exception rollback) { errors.Add(rollback); }
            }
            try {
                var current = backend.Read(endpoint.Id);
                if (Math.Abs(current.Level - result.Level) > .0001f || current.Muted != result.Muted)
                    throw new IOException("Endpoint level changed during failure; preserving the newer state.");
                backend.Apply(endpoint.Id, beforeAudio.Level, beforeAudio.Muted);
            }
            catch (Exception rollbackFailure) { errors.Add(rollbackFailure); }
            if (errors.Count > 1) throw new AggregateException("Profile apply failed and some previous settings could not be restored.", errors);
            throw;
        }
        return result;
    }
    internal static void Validate(AudioProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 80) throw new InvalidDataException("Profile names must contain 1–80 characters.");
        if (string.IsNullOrWhiteSpace(profile.EndpointId) || string.IsNullOrWhiteSpace(profile.DeviceIdentity)) throw new InvalidDataException("Profile device identity is missing.");
        if (!Enum.IsDefined(profile.Direction) || !float.IsFinite(profile.Level) || profile.Level is < 0 or > 1) throw new InvalidDataException("Invalid profile level or direction.");
        if (profile.Effects is not null) {
            if (profile.Direction != AudioDirection.Microphone) throw new InvalidDataException("Playback profiles cannot contain microphone effects.");
            profile.Effects.Validate();
        }
        if (profile.Sidetone is not null) {
            if (profile.Direction != AudioDirection.Microphone) throw new InvalidDataException("Playback profiles cannot contain sidetone settings.");
            profile.Sidetone.Validate();
        }
        if (profile.Playback is not null && profile.Direction != AudioDirection.Playback)
            throw new InvalidDataException("Microphone profiles cannot contain playback effects.");
        profile.Playback?.Validate();
        if (profile.GsxSidetone is not null) {
            if (profile.Direction != AudioDirection.Microphone || profile.Sidetone is not null) throw new InvalidDataException("GSX sidetone belongs only to GSX microphone profiles.");
            profile.GsxSidetone.Validate();
        }
    }
}
