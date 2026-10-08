using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EposControl.Core;

public sealed record SavedProcessingState(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string DeviceIdentity,
    [property: JsonRequired] DateTimeOffset SavedAtUtc,
    [property: JsonRequired] MicrophoneEffects Effects,
    [property: JsonRequired] bool RestoreOnConnect);

public sealed record ProcessingApplyResult(MicrophoneEffects Effects, string? PersistenceError);

// One atomic file per physical microphone. Profiles and diagnostic memory are separate.
public sealed class ProcessingStateStore(string directory)
{
    private static readonly JsonSerializerOptions Options = new() {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public string DirectoryPath { get; } = Path.GetFullPath(directory);

    public static string DeviceIdentity(AudioEndpoint endpoint)
    {
        DemoMicrophoneEffectsBackend.ValidateEndpoint(endpoint);
        ApoSharedObjects.B20Name(endpoint.Usb!);
        if (string.IsNullOrWhiteSpace(endpoint.Usb!.InstanceId))
            throw new InvalidDataException("A physical USB instance identity is required.");
        // Endpoint GUIDs and friendly names can change on reconnect or rename.
        return $"1395|009F|Microphone|{endpoint.Usb.InstanceId.ToUpperInvariant()}";
    }

    internal string StatePath(AudioEndpoint endpoint) => Path.Combine(DirectoryPath,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DeviceIdentity(endpoint)))) + ".json");

    public SavedProcessingState? Load(AudioEndpoint endpoint)
    {
        var path = StatePath(endpoint);
        if (!Directory.Exists(DirectoryPath)) return null;
        using var held = Lock(path);
        return Read(path, DeviceIdentity(endpoint));
    }

    public SavedProcessingState Remember(AudioEndpoint endpoint, MicrophoneEffects verifiedEffects)
    {
        var path = StatePath(endpoint); ValidateEffects(verifiedEffects);
        Directory.CreateDirectory(DirectoryPath);
        using var held = Lock(path);
        var previous = Read(path, DeviceIdentity(endpoint));
        var state = new SavedProcessingState(1, DeviceIdentity(endpoint), DateTimeOffset.UtcNow,
            verifiedEffects, previous?.RestoreOnConnect ?? false);
        Write(path, state); return state;
    }

    public void SetRestoreOnConnect(AudioEndpoint endpoint, bool enabled)
    {
        var path = StatePath(endpoint);
        if (!Directory.Exists(DirectoryPath)) throw new InvalidOperationException("Apply and save processing first.");
        using var held = Lock(path);
        var previous = Read(path, DeviceIdentity(endpoint)) ?? throw new InvalidOperationException("Apply and save processing first.");
        Write(path, previous with { RestoreOnConnect = enabled });
    }

    public ProcessingApplyResult ApplyAndSave(AudioEndpoint endpoint, IMicrophoneEffectsBackend backend,
        MicrophoneEffects expected, MicrophoneEffects desired)
    {
        DeviceIdentity(endpoint); ValidateEffects(desired);
        var applied = backend.Apply(endpoint, expected, desired);
        // A failed transaction never reaches persistence. A failed save must not roll back
        // a completed hardware transaction or be presented as an unsuccessful Apply.
        try {
            ValidateEffects(applied);
            if (!ApoMicrophoneCodec.Matches(applied, desired)) throw new InvalidDataException("Applied processing readback does not match the requested settings.");
            Remember(endpoint, applied);
            return new(applied, null);
        } catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException) {
            return new(applied, ex.Message);
        }
    }

    public MicrophoneEffects Restore(AudioEndpoint endpoint, IAudioBackend audio, IMicrophoneEffectsBackend backend)
    {
        var state = Load(endpoint) ?? throw new InvalidOperationException("No saved processing exists for this microphone.");
        WindowsApoMemory.ValidateIdentity(endpoint, audio.Discover());
        return backend.Apply(endpoint, backend.Read(endpoint), state.Effects);
    }

    private static void ValidateEffects(MicrophoneEffects effects)
    {
        if (effects?.Processing is null) throw new InvalidDataException("Complete processing settings are required.");
        effects.Validate();
    }
    private static SavedProcessingState? Read(string path, string identity)
    {
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        if (stream.Length > 16384) throw new InvalidDataException("Saved processing file is too large.");
        var state = JsonSerializer.Deserialize<SavedProcessingState>(stream, Options)
            ?? throw new InvalidDataException("Saved processing is missing.");
        if (state.SchemaVersion != 1 || state.DeviceIdentity != identity || state.SavedAtUtc == default || state.SavedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Saved processing version, identity or timestamp is invalid.");
        ValidateEffects(state.Effects); return state;
    }
    private static void Write(string path, SavedProcessingState state)
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

// Opt-in restore once per observed physical connection, including endpoint GUID changes.
// No object creation or service manipulation: an available processing interface is required.
public sealed class ProcessingRestoreSession(ProcessingStateStore store)
{
    private readonly HashSet<string> attempted = [];
    private static string Connection(AudioEndpoint endpoint) => ProcessingStateStore.DeviceIdentity(endpoint) + "|" + endpoint.Id;
    public void Observe(IReadOnlyList<AudioEndpoint> endpoints)
    {
        var connections = endpoints.Where(e => e.Direction == AudioDirection.Microphone &&
            e.Usb is { } u && u.VendorId.Equals("1395", StringComparison.OrdinalIgnoreCase) && u.ProductId.Equals("009F", StringComparison.OrdinalIgnoreCase))
            .Select(Connection).ToHashSet();
        attempted.RemoveWhere(key => !connections.Contains(key));
    }
    public MicrophoneEffects? RestoreIfEnabled(AudioEndpoint endpoint, IAudioBackend audio, IMicrophoneEffectsBackend backend)
    {
        if (!attempted.Add(Connection(endpoint))) return null;
        var saved = store.Load(endpoint);
        return saved?.RestoreOnConnect == true ? store.Restore(endpoint, audio, backend) : null;
    }
}
