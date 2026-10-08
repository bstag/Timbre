using System.ComponentModel;

namespace Timbre.Core;

public interface IB20HostConnection : IDisposable
{
    bool CreatedFresh { get; }
    MicrophoneEffects Read();
    MicrophoneEffects Apply(MicrophoneEffects expected, MicrophoneEffects desired);
}

public sealed record B20HostStatus(string State, AudioEndpoint? Endpoint = null, MicrophoneEffects? Effects = null,
    bool CreatedFresh = false, bool SettingsWrites = false, string RestoreSource = "None", int Generation = 0,
    int Attempts = 0, string? Error = null);

// Called on one control thread. Discovery and transport are real adapter seams, so
// lifecycle policy can be tested without unplugging hardware or stopping a service.
public sealed class B20HostLifecycle : IDisposable
{
    private readonly string instance;
    private readonly IAudioBackend audio;
    private readonly ProcessingStateStore store;
    private readonly Func<AudioEndpoint, ApoStartupState> startup;
    private readonly Func<AudioEndpoint, byte[], IB20HostConnection> connect;
    private IB20HostConnection? connection;
    private AudioEndpoint? endpoint;
    private DateTimeOffset retryAt;
    private int attempts, generation;
    private bool disposed;
    public B20HostStatus Status { get; private set; } = new("WaitingForDevice");

    public B20HostLifecycle(string instance, IAudioBackend audio, ProcessingStateStore store,
        Func<AudioEndpoint, ApoStartupState> startup, Func<AudioEndpoint, byte[], IB20HostConnection> connect)
    {
        if (string.IsNullOrWhiteSpace(instance)) throw new ArgumentException("An explicit B20 USB instance is required.");
        this.instance = instance; this.audio = audio; this.store = store; this.startup = startup; this.connect = connect;
    }

    public B20HostStatus Tick(DateTimeOffset now)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Status.State == "Faulted") return Status;
        if (Status.State == "Retrying" && now < retryAt) return Status;
        var attemptStarted = false;
        try {
            var devices = audio.Discover();
            var units = devices.Where(e => e.Usb is { } u && u.VendorId.Equals("1395", StringComparison.OrdinalIgnoreCase)
                && u.ProductId.Equals("009F", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (units.Select(e => e.Usb!.InstanceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                throw new InvalidOperationException("Multiple B20 units cannot be isolated by the vendor shared objects.");
            var microphones = units.Where(e => e.Direction == AudioDirection.Microphone &&
                e.Usb!.InstanceId.Equals(instance, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (microphones.Length > 1) throw new InvalidOperationException("The target B20 has ambiguous microphone endpoints.");
            if (microphones.Length == 0) {
                Close(); endpoint = null; attempts = 0; retryAt = default;
                return Status = new("WaitingForDevice", Generation: generation);
            }
            var current = microphones[0];
            if (endpoint is not null && (endpoint.Id != current.Id || endpoint.ProfileIdentity != current.ProfileIdentity)) {
                Close(); attempts = 0; retryAt = default;
            }
            endpoint = current;
            WindowsApoMemory.ValidateIdentity(current, devices);
            if (connection is not null) return Status = Status with { Effects = connection.Read(), Endpoint = current };
            if (now < retryAt) return Status;
            attempts++; attemptStarted = true;
            // Validate all disk input BEFORE native objects are opened/created.
            var seed = startup(current);
            seed.ValidateForB20(current);
            if (seed.DiagnosticSeed is null) throw new InvalidDataException("A device-matched diagnostic seed is required to preserve unmapped configuration.");
            var saved = store.Load(current);
            var restore = saved?.RestoreOnConnect == true;
            connection = connect(current, seed.DiagnosticSeed);
            var before = connection.Read();
            var desired = restore ? saved!.Effects : seed.Effects;
            var writes = connection.CreatedFresh || restore;
            var applied = writes ? connection.Apply(before, desired) : before;
            if (writes && !ApoMicrophoneCodec.Matches(applied, desired)) throw new InvalidDataException("Host restore readback does not match the requested settings.");
            generation++;
            Status = new("Connected", current, applied, connection.CreatedFresh, writes,
                restore ? "LastSavedProcessing" : connection.CreatedFresh ? "ExplicitSnapshot" : "None", generation, attempts);
            attempts = 0; return Status;
        } catch (Exception ex) {
            Close();
            if (!attemptStarted) attempts++;
            // Disk corruption, wrong devices, ambiguity and access failures fail closed.
            // Transient transport/discovery failures get at most three attempts, two seconds apart.
            var transient = ex is IOException or TimeoutException or Win32Exception;
            if (transient && attempts < 3) {
                retryAt = now.AddSeconds(2);
                return Status = new("Retrying", endpoint, Generation: generation, Attempts: attempts, Error: ex.Message);
            }
            return Status = new("Faulted", endpoint, Generation: generation, Attempts: attempts, Error: ex.Message);
        }
    }
    private void Close() { var prior = connection; connection = null; prior?.Dispose(); }
    public void Dispose()
    {
        if (disposed) return;
        Close(); disposed = true; Status = Status with { State = "Stopped" };
    }
}
