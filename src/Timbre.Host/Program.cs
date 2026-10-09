using System.Text.Json;
using Timbre.Core;

if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("This experimental host requires Windows."); return 1; }
var result = 1;
var stopRequested = false;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; Volatile.Write(ref stopRequested, true); };
var thread = new Thread(() => {
    if (!OperatingSystem.IsWindows()) return;
    string? directory = null;
    IDisposable? lifetime = null;
    IDisposable? owner = null;
    B20HostLifecycle? lifecycle = null;
    GsxHostLifecycle? gsxLifecycle = null;
    var started = DateTimeOffset.UtcNow;
    string? failure = null;
    try {
        var options = ApoHostOptions.Parse(args);
        var initialize = options.Mode == "--initialize-b20";
        var managed = options.Mode == "--manage-b20";
        var managedGsx = options.Mode == "--manage-gsx";
        var pausedGsx = options.Mode == "--initialize-gsx-paused";
        var initializeGsx = options.Mode == "--initialize-gsx" || pausedGsx;
        var validateManagedGsx = options.Mode == "--validate-managed-gsx";
        var validateGsx = options.Mode == "--validate-gsx" || validateManagedGsx;
        if (File.Exists(options.StopFile)) throw new IOException("The stop file already exists; choose a fresh run directory.");
        if (Directory.Exists(options.ReportDirectory) && Directory.EnumerateFileSystemEntries(options.ReportDirectory).Any())
            throw new IOException("The report directory must be empty to prevent stale readiness reports.");
        Directory.CreateDirectory(options.ReportDirectory); directory = options.ReportDirectory;
        var deadline = started.AddSeconds(options.Seconds);
        // Holding/configuring objects must not activate an audio client during discovery.
        var audio = pausedGsx ? CoreAudioBackend.PrepareGsxInitialization(GsxApoStartupState.Load(options.InitialState!)) : new CoreAudioBackend(false);
        bool Continue() => !Volatile.Read(ref stopRequested) && !File.Exists(options.StopFile) && DateTimeOffset.UtcNow < deadline;
        if (initialize || managed) owner = B20HostOwner.Acquire();
        if (initializeGsx || validateGsx) {
            if (initializeGsx) owner = GsxHostOwner.Acquire();
            IReadOnlyList<AudioEndpoint> discovered;
            try { discovered = audio.Discover(); }
            catch {
                if (pausedGsx) Save("startup-discovery", new { Endpoints = audio.LastMetadataDiscovery, CapturedEndpointSelection = true,
                    DiscoveryActivatesAudioClients = false });
                throw;
            }
            if (pausedGsx) Save("startup-discovery", new { Endpoints = audio.LastMetadataDiscovery, CapturedEndpointSelection = true,
                DiscoveryActivatesAudioClients = false });
            var gsx = discovered.Where(e => e.Usb is { } usb && usb.VendorId.Equals("1395", StringComparison.OrdinalIgnoreCase) &&
                usb.ProductId.Equals("0098", StringComparison.OrdinalIgnoreCase)).ToArray();
            var microphones = gsx.Where(e => e.Direction == AudioDirection.Microphone).ToArray();
            var outputs = gsx.Where(e => e.Direction == AudioDirection.Playback).ToArray();
            if (microphones.Length != 1 || outputs.Length != 1)
                throw new InvalidOperationException("Exactly one GSX microphone and one GSX sound endpoint are required.");
            var microphone = microphones[0]; var playback = outputs[0];
            var startup = GsxApoStartupState.Load(options.InitialState!, microphone, playback);
            WindowsApoMemory.ValidateIdentity(microphone, discovered);
            WindowsApoMemory.ValidatePlaybackIdentity(playback, discovered);
            if (validateGsx) {
                SavedGsxProcessingState? saved = null;
                if (validateManagedGsx) {
                    if (!options.DeviceInstance!.Equals(startup.DeviceInstance, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Managed validation target differs from the diagnostic snapshot.");
                    saved = new GsxProcessingStateStore(options.StateDirectory!).Load(microphone, playback)
                        ?? throw new InvalidDataException("Managed validation requires a saved GSX processing record.");
                    MicrophoneEffects.ValidateUpdate(microphone, startup.Microphone, saved.Effects.Microphone);
                }
                Save("validated", new { ProcessId = Environment.ProcessId, Started = started, Endpoint = microphone,
                    PlaybackEndpoint = playback, Effects = startup.Microphone, Playback = startup.Playback,
                    Mode = validateManagedGsx ? "ValidateManagedGsxStartup" : "ValidateGsxStartup", SavedProcessing = saved,
                    DiscoveryActivatesAudioClients = false,
                    SettingsWrites = false, CreatesMissingObjects = false, CreatedFresh = false });
                Console.WriteLine("GSX startup snapshot validated; no vendor objects opened or created.");
            } else {
            if (pausedGsx) {
                var requestPath = Path.Combine(directory!, "initialize-request.json");
                Save("prepared", new { ProcessId = Environment.ProcessId, Started = started, Deadline = deadline,
                    Endpoint = microphone, PlaybackEndpoint = playback, PhysicalInstances = audio.LastPhysicalGsxInstances,
                    audio.DiscoveryMethod, DiscoveryActivatesAudioClients = false, SettingsWrites = false,
                    CreatesMissingObjects = false, CreatedFresh = false, WaitingForExplicitInitializationRequest = true });
                Console.WriteLine("GSX live endpoint pair prepared; waiting for the explicit paused-audio request. No settings changed.");
                while (Continue() && !File.Exists(requestPath)) Thread.Sleep(100);
                if (File.Exists(options.StopFile) || Volatile.Read(ref stopRequested)) { result = 0; return; }
                if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("GSX preparation expired; no native objects created.");
                if (!File.Exists(requestPath)) {
                    throw new TimeoutException("GSX initialization request did not arrive before the deadline; no native objects created.");
                }
                using var request = JsonDocument.Parse(File.ReadAllText(requestPath));
                if (request.RootElement.GetProperty("ProcessId").GetInt32() != Environment.ProcessId ||
                    request.RootElement.GetProperty("DeviceInstance").GetString() != startup.DeviceInstance)
                    throw new InvalidDataException("GSX initialization request does not match this prepared process/device.");
            }
            var fresh = WindowsApoObjectHost.StartFreshGsx(microphone, playback, audio, startup); lifetime = fresh;
            if (!ApoMicrophoneCodec.Matches(fresh.Read(), startup.Microphone) ||
                !ApoPlaybackCodec.Matches(fresh.ReadPlayback(), startup.Playback))
                throw new IOException("Fresh GSX settings differ from the explicit startup snapshot.");
            Save("ready", new { ProcessId = Environment.ProcessId, Started = started, Deadline = deadline,
                Endpoint = microphone, PlaybackEndpoint = playback, Effects = fresh.Read(), Playback = fresh.ReadPlayback(),
                Mode = "CreateFreshGsxObjects", DiscoveryActivatesAudioClients = false,
                UsesPreparedEndpointPair = pausedGsx, audio.DiscoveryMethod, PhysicalInstances = audio.LastPhysicalGsxInstances,
                SettingsWrites = true, CreatesMissingObjects = true, CreatedFresh = fresh.CreatedFresh,
                AutomaticRestore = false, StartupRestorePolicy = "ExplicitSnapshotOnly" });
            Console.WriteLine("Fresh GSX objects initialized; microphone and sound settings preserved.");
            while (Continue()) {
                var current = audio.Discover();
                WindowsApoMemory.ValidateIdentity(microphone, current);
                WindowsApoMemory.ValidatePlaybackIdentity(playback, current);
                Save("heartbeat", new { ProcessId = Environment.ProcessId, CollectedAt = DateTimeOffset.UtcNow,
                    Effects = fresh.Read(), Playback = fresh.ReadPlayback(), CreatedFresh = fresh.CreatedFresh,
                    audio.DiscoveryMethod, PhysicalInstances = audio.LastPhysicalGsxInstances });
                Thread.Sleep(1000);
            }
            }
        } else if (managedGsx) {
            owner = GsxHostOwner.Acquire();
            gsxLifecycle = new(options.DeviceInstance!, audio, new GsxProcessingStateStore(options.StateDirectory!),
                (microphone, playback) => GsxApoStartupState.Load(options.InitialState!, microphone, playback),
                (microphone, playback, startup) => OperatingSystem.IsWindows()
                    ? WindowsGsxHostConnection.Connect(microphone, playback, audio, startup) : throw new PlatformNotSupportedException());
            Save("starting", new { ProcessId = Environment.ProcessId, Started = started, Deadline = deadline,
                Mode = "ManageGsxLifecycle", options.DeviceInstance, options.StateDirectory, AutomaticRestorePolicy = "SavedPerDeviceOptIn" });
            var recordedGeneration = 0;
            while (Continue()) {
                var status = gsxLifecycle.Tick(DateTimeOffset.UtcNow);
                var report = new { ProcessId = Environment.ProcessId, CollectedAt = DateTimeOffset.UtcNow, Started = started, Deadline = deadline,
                    Mode = "ManageGsxLifecycle", status.State, status.Endpoint, status.PlaybackEndpoint, status.Effects, status.CreatedFresh,
                    status.SettingsWrites, CreatesMissingObjects = true, AutomaticRestore = status.RestoreSource == "LastSavedProcessing",
                    StartupRestorePolicy = status.RestoreSource, status.Generation, status.Attempts, status.Error };
                Save("heartbeat", report);
                if (status.State == "Faulted") throw new InvalidOperationException("Managed GSX host failed: " + status.Error);
                if (status.State == "Connected" && status.Generation != recordedGeneration) {
                    Save("connection-" + status.Generation.ToString("D4"), report);
                    if (recordedGeneration == 0) Save("ready", report);
                    recordedGeneration = status.Generation;
                    Console.WriteLine($"GSX connected (generation {status.Generation}); restore source: {status.RestoreSource}.");
                }
                Thread.Sleep(1000);
            }
            if (recordedGeneration == 0 && !File.Exists(options.StopFile) && !Volatile.Read(ref stopRequested))
                throw new TimeoutException("The target GSX never became ready before the experiment deadline.");
        } else if (managed) {
            lifecycle = new(options.DeviceInstance!, audio, new ProcessingStateStore(options.StateDirectory!),
                endpoint => ApoStartupState.LoadForB20(options.InitialState!, endpoint),
                (endpoint, seed) => OperatingSystem.IsWindows() ? WindowsB20HostConnection.Connect(endpoint, audio, seed) : throw new PlatformNotSupportedException());
            Save("starting", new { ProcessId = Environment.ProcessId, Started = started, Deadline = deadline,
                Mode = "ManageB20Lifecycle", options.DeviceInstance, options.StateDirectory, AutomaticRestorePolicy = "SavedPerDeviceOptIn" });
            var recordedGeneration = 0;
            while (Continue()) {
                var status = lifecycle.Tick(DateTimeOffset.UtcNow);
                var report = new { ProcessId = Environment.ProcessId, CollectedAt = DateTimeOffset.UtcNow, Started = started, Deadline = deadline,
                    Mode = "ManageB20Lifecycle", status.State, status.Endpoint, status.Effects, status.CreatedFresh,
                    status.SettingsWrites, CreatesMissingObjects = true, AutomaticRestore = status.RestoreSource == "LastSavedProcessing",
                    StartupRestorePolicy = status.RestoreSource, status.Generation, status.Attempts, status.Error };
                Save("heartbeat", report);
                if (status.State == "Faulted") throw new InvalidOperationException("Managed B20 host failed: " + status.Error);
                if (status.State == "Connected" && status.Generation != recordedGeneration) {
                    Save("connection-" + status.Generation.ToString("D4"), report);
                    if (recordedGeneration == 0) Save("ready", report);
                    recordedGeneration = status.Generation;
                    Console.WriteLine($"B20 connected (generation {status.Generation}); restore source: {status.RestoreSource}.");
                }
                Thread.Sleep(1000);
            }
            if (recordedGeneration == 0 && !File.Exists(options.StopFile) && !Volatile.Read(ref stopRequested))
                throw new TimeoutException("The target B20 never became ready before the experiment deadline.");
        } else {
            var endpoints = audio.Discover().Where(e => e.Direction == AudioDirection.Microphone && e.Usb is { } usb &&
                usb.VendorId.Equals("1395", StringComparison.OrdinalIgnoreCase) && usb.ProductId.Equals("009f", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (endpoints.Length != 1) throw new InvalidOperationException("Exactly one active B20 microphone is required.");
            var endpoint = endpoints[0]; Func<MicrophoneEffects> read;
            if (initialize) {
                var startup = ApoStartupState.LoadForB20(options.InitialState!, endpoint);
                if (startup.DiagnosticSeed is null) throw new InvalidDataException("A current diagnostic snapshot is required for the live pilot to preserve unmapped configuration.");
                var fresh = WindowsApoObjectHost.StartFreshB20(endpoint, audio, startup.DiagnosticSeed); lifetime = fresh; read = fresh.Read;
                WindowsApoMemory.Create(audio).Apply(endpoint, fresh.Read(), startup.Effects);
            } else {
                var lease = WindowsApoObjectLease.RetainB20(endpoint, audio); lifetime = lease; read = lease.Read;
            }
            Save("ready", new { ProcessId = Environment.ProcessId, Started = started, Deadline = deadline,
                Endpoint = endpoint, Effects = read(), Mode = initialize ? "CreateFreshB20Objects" : "RetainExistingB20Objects", SettingsWrites = initialize,
                CreatesMissingObjects = initialize, CreatedFresh = initialize, AutomaticRestore = false,
                StartupRestorePolicy = initialize ? "ExplicitSnapshotOnly" : "None" });
            Console.WriteLine(initialize ? "Fresh B20 objects initialized; explicit starting settings applied." : "B20 objects retained. Settings are unchanged.");
            while (Continue()) {
                var current = audio.Discover();
                // A retained lease remains read-only during the explicitly paused audio engine.
                // Require the original physical B20 to remain present; ordinary unplug still fails.
                if (initialize || current.Any(e => e.Id == endpoint.Id) || !CoreAudioBackend.CanRetainB20WhileAudioStopped(endpoint))
                    WindowsApoMemory.ValidateIdentity(endpoint, current);
                Save("heartbeat", new { ProcessId = Environment.ProcessId, CollectedAt = DateTimeOffset.UtcNow, Effects = read() });
                Thread.Sleep(1000);
            }
        }
        result = 0;
    } catch (Exception ex) { failure = ex.ToString(); Console.Error.WriteLine(ex.Message); }
    finally {
        try { gsxLifecycle?.Dispose(); lifecycle?.Dispose(); lifetime?.Dispose(); }
        catch (Exception ex) { failure = (failure ?? "") + "\nShutdown: " + ex; result = 1; }
        finally { owner?.Dispose(); }
        if (directory is not null) {
            try { Save("stopped", new { ProcessId = Environment.ProcessId, Stopped = DateTimeOffset.UtcNow, Success = result == 0,
                LastState = (object?)gsxLifecycle?.Status ?? lifecycle?.Status, Error = failure }); }
            catch (Exception ex) { Console.Error.WriteLine("Could not save host shutdown report: " + ex.Message); result = 1; }
        }
    }
    void Save(string name, object report)
    {
        var path = Path.Combine(directory!, name + ".json"); var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
});
thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return result;
