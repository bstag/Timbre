using System.ComponentModel;

namespace Timbre.Core;

public interface IGsxHostConnection : IDisposable
{
    bool CreatedFresh { get; }
    GsxProcessingState Read();
    GsxProcessingState Apply(GsxProcessingState expected, GsxProcessingState desired);
}

public sealed record GsxHostStatus(string State, AudioEndpoint? Endpoint = null, GsxProcessingState? Effects = null,
    bool CreatedFresh = false, bool SettingsWrites = false, string RestoreSource = "None", int Generation = 0,
    int Attempts = 0, string? Error = null, AudioEndpoint? PlaybackEndpoint = null);

// Called on one control thread. Discovery and transport are real adapter seams, so
// lifecycle policy can be tested without unplugging hardware or stopping a service.
public sealed class GsxHostLifecycle : IDisposable
{
    private readonly string instance;
    private readonly IAudioBackend audio;
    private readonly GsxProcessingStateStore store;
    private readonly Func<AudioEndpoint, AudioEndpoint, GsxApoStartupState> startup;
    private readonly Func<AudioEndpoint, AudioEndpoint, GsxApoStartupState, IGsxHostConnection> connect;
    private IGsxHostConnection? connection;
    private AudioEndpoint? endpoint, playbackEndpoint;
    private DateTimeOffset retryAt;
    private int attempts, generation;
    private bool disposed;
    public GsxHostStatus Status { get; private set; } = new("WaitingForDevice");

    public GsxHostLifecycle(string instance, IAudioBackend audio, GsxProcessingStateStore store,
        Func<AudioEndpoint, AudioEndpoint, GsxApoStartupState> startup, Func<AudioEndpoint, AudioEndpoint, GsxApoStartupState, IGsxHostConnection> connect)
    {
        if (string.IsNullOrWhiteSpace(instance)) throw new ArgumentException("An explicit GSX USB instance is required.");
        this.instance = instance; this.audio = audio; this.store = store; this.startup = startup; this.connect = connect;
    }

    public GsxHostStatus Tick(DateTimeOffset now)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Status.State == "Faulted") return Status;
        if (Status.State == "Retrying" && now < retryAt) return Status;
        var attemptStarted = false;
        try {
            var devices = audio.Discover();
            var units = devices.Where(e => e.Usb is { } u && u.VendorId.Equals("1395", StringComparison.OrdinalIgnoreCase)
                && u.ProductId.Equals("0098", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (units.Select(e => e.Usb!.InstanceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                throw new InvalidOperationException("Multiple GSX units cannot be isolated by the vendor shared objects.");
            var microphones = units.Where(e => e.Direction == AudioDirection.Microphone &&
                e.Usb!.InstanceId.Equals(instance, StringComparison.OrdinalIgnoreCase)).ToArray();
            var outputs = units.Where(e => e.Direction == AudioDirection.Playback &&
                e.Usb!.InstanceId.Equals(instance, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (microphones.Length > 1 || outputs.Length > 1) throw new InvalidOperationException("The target GSX has ambiguous endpoints.");
            if (microphones.Length == 0 || outputs.Length == 0) {
                Close(); endpoint = playbackEndpoint = null; attempts = 0; retryAt = default;
                return Status = new("WaitingForDevice", Generation: generation);
            }
            var current = microphones[0]; var output = outputs[0];
            GsxApoStartupState.ValidateEndpoints(current, output);
            if (endpoint is not null && (endpoint.Id != current.Id || endpoint.ProfileIdentity != current.ProfileIdentity ||
                playbackEndpoint!.Id != output.Id || playbackEndpoint.ProfileIdentity != output.ProfileIdentity)) {
                Close(); attempts = 0; retryAt = default;
            }
            endpoint = current; playbackEndpoint = output;
            WindowsApoMemory.ValidateIdentity(current, devices);
            WindowsApoMemory.ValidatePlaybackIdentity(output, devices);
            if (connection is not null) {
                var observed = connection.Read(); observed.Validate();
                return Status = Status with { Effects = observed, Endpoint = current, PlaybackEndpoint = output };
            }
            if (now < retryAt) return Status;
            attempts++; attemptStarted = true;
            // Validate all disk input BEFORE native objects are opened/created.
            var seed = startup(current, output);
            seed.Validate(current, output);
            var saved = store.Load(current, output);
            var restore = saved?.RestoreOnConnect == true;
            connection = connect(current, output, seed);
            var before = connection.Read(); before.Validate();
            var desired = restore ? saved!.Effects : new GsxProcessingState(seed.Microphone, seed.Playback);
            var writes = connection.CreatedFresh || restore;
            var applied = writes ? connection.Apply(before, desired) : before;
            applied.Validate();
            if (writes && !applied.Matches(desired)) throw new InvalidDataException("Host restore readback does not match the requested settings.");
            generation++;
            Status = new("Connected", current, applied, connection.CreatedFresh, writes,
                restore ? "LastSavedProcessing" : connection.CreatedFresh ? "ExplicitSnapshot" : "None", generation, attempts, PlaybackEndpoint: output);
            attempts = 0; return Status;
        } catch (Exception ex) {
            Close();
            if (!attemptStarted) attempts++;
            // Disk corruption, wrong devices, ambiguity and access failures fail closed.
            // Transient transport/discovery failures get at most three attempts, two seconds apart.
            var transient = ex is IOException or TimeoutException or Win32Exception;
            if (transient && attempts < 3) {
                retryAt = now.AddSeconds(2);
                return Status = new("Retrying", endpoint, Generation: generation, Attempts: attempts, Error: ex.Message, PlaybackEndpoint: playbackEndpoint);
            }
            return Status = new("Faulted", endpoint, Generation: generation, Attempts: attempts, Error: ex.Message, PlaybackEndpoint: playbackEndpoint);
        }
    }
    private void Close() { var prior = connection; connection = null; prior?.Dispose(); }
    public void Dispose()
    {
        if (disposed) return;
        Close(); disposed = true; Status = Status with { State = "Stopped" };
    }
}
