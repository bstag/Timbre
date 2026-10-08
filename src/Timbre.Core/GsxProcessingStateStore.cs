using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Timbre.Core;

public sealed record SavedGsxProcessingState(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string DeviceIdentity,
    [property: JsonRequired] DateTimeOffset SavedAtUtc,
    [property: JsonRequired] GsxProcessingState Effects,
    [property: JsonRequired] bool RestoreOnConnect);

public sealed record GsxProcessingApplyResult(GsxProcessingState Effects, string? PersistenceError);

// One atomic file per physical GSX, containing both processing pages.
// Profiles, hardware gain/sidetone and diagnostic memory remain separate.
public sealed class GsxProcessingStateStore(string directory)
{
    private static readonly JsonSerializerOptions Options = new() {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public string DirectoryPath { get; } = Path.GetFullPath(directory);

    public static string DeviceIdentity(AudioEndpoint microphone, AudioEndpoint playback)
    {
        GsxApoStartupState.ValidateEndpoints(microphone, playback);
        // Endpoint GUIDs and friendly names can change on reconnect or rename.
        return $"1395|0098|Processing|{microphone.Usb!.InstanceId.ToUpperInvariant()}";
    }

    internal string StatePath(AudioEndpoint microphone, AudioEndpoint playback) => Path.Combine(DirectoryPath,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DeviceIdentity(microphone, playback)))) + ".json");

    public SavedGsxProcessingState? Load(AudioEndpoint microphone, AudioEndpoint playback)
    {
        var path = StatePath(microphone, playback);
        if (!Directory.Exists(DirectoryPath)) return null;
        using var held = Lock(path);
        return Read(path, DeviceIdentity(microphone, playback));
    }

    public SavedGsxProcessingState Remember(AudioEndpoint microphone, AudioEndpoint playback, GsxProcessingState verifiedEffects)
    {
        var path = StatePath(microphone, playback); ValidateEffects(verifiedEffects);
        Directory.CreateDirectory(DirectoryPath);
        using var held = Lock(path);
        var previous = Read(path, DeviceIdentity(microphone, playback));
        var state = new SavedGsxProcessingState(1, DeviceIdentity(microphone, playback), DateTimeOffset.UtcNow,
            verifiedEffects, previous?.RestoreOnConnect ?? false);
        Write(path, state); return state;
    }

    public void SetRestoreOnConnect(AudioEndpoint microphone, AudioEndpoint playback, bool enabled)
    {
        var path = StatePath(microphone, playback);
        if (!Directory.Exists(DirectoryPath)) throw new InvalidOperationException("Apply and save processing first.");
        using var held = Lock(path);
        var previous = Read(path, DeviceIdentity(microphone, playback)) ?? throw new InvalidOperationException("Apply and save processing first.");
        Write(path, previous with { RestoreOnConnect = enabled });
    }

    public GsxProcessingApplyResult ApplyAndSave(AudioEndpoint microphone, AudioEndpoint playback, IGsxProcessingBackend backend,
        GsxProcessingState expected, GsxProcessingState desired)
    {
        DeviceIdentity(microphone, playback); ValidateEffects(desired);
        var applied = backend.Apply(microphone, playback, expected, desired);
        // A failed transaction never reaches persistence. A failed save must not roll back
        // a completed hardware transaction or be presented as an unsuccessful Apply.
        try {
            ValidateEffects(applied);
            if (!applied.Matches(desired)) throw new InvalidDataException("Applied processing readback does not match the requested settings.");
            Remember(microphone, playback, applied);
            return new(applied, null);
        } catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException) {
            return new(applied, ex.Message);
        }
    }

    public GsxProcessingState Restore(AudioEndpoint microphone, AudioEndpoint playback, IAudioBackend audio, IGsxProcessingBackend backend)
    {
        var state = Load(microphone, playback) ?? throw new InvalidOperationException("No saved processing exists for this GSX.");
        var discovered = audio.Discover();
        WindowsApoMemory.ValidateIdentity(microphone, discovered);
        WindowsApoMemory.ValidatePlaybackIdentity(playback, discovered);
        return backend.Apply(microphone, playback, backend.Read(microphone, playback), state.Effects);
    }

    private static void ValidateEffects(GsxProcessingState effects)
    {
        if (effects is null) throw new InvalidDataException("Complete processing settings are required.");
        effects.Validate();
    }
    private static SavedGsxProcessingState? Read(string path, string identity)
    {
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        if (stream.Length > 16384) throw new InvalidDataException("Saved processing file is too large.");
        var state = JsonSerializer.Deserialize<SavedGsxProcessingState>(stream, Options)
            ?? throw new InvalidDataException("Saved processing is missing.");
        if (state.SchemaVersion != 1 || state.DeviceIdentity != identity || state.SavedAtUtc == default || state.SavedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Saved processing version, identity or timestamp is invalid.");
        ValidateEffects(state.Effects); return state;
    }
    private static void Write(string path, SavedGsxProcessingState state)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                JsonSerializer.Serialize(stream, state, Options); stream.Flush(true);
            }
            File.Move(temporary, path, true);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static FileStream Lock(string path)
    {
        var elapsed = Stopwatch.StartNew();
        while (true) {
            try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (elapsed.ElapsedMilliseconds < 1000) { Thread.Sleep(25); }
        }
    }
}
