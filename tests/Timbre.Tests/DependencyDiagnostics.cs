using System.Text.Json;
using System.Text.Json.Serialization;
using Timbre.Core;

internal sealed record DependencyEndpoint(AudioEndpoint Endpoint, MicrophoneEffects? Effects, SidetoneState? Sidetone,
    PickupPatternState? PickupPattern, string? EffectsError, string? SidetoneError, string? PatternError);
internal sealed record DependencySnapshot(DateTimeOffset CollectedAt, bool SettingsWrites, bool HardwareStatusQuerySent,
    IReadOnlyList<DependencyEndpoint> Endpoints);
internal static class DependencyDiagnostics
{
    internal static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 1;
        var exit = 1;
        var thread = new Thread(() => {
            try {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                var i = Array.IndexOf(args, "--report"); if (i < 0 || i + 1 >= args.Length) throw new ArgumentException("Use --dependencies --report FILE.");
                var audio = new CoreAudioBackend(); var effects = WindowsApoMemory.Create(audio); var sidetone = WindowsSidetone.Create(audio);
                var patterns = new WindowsPickupPattern(audio); var rows = new List<DependencyEndpoint>(); var queried = false;
                foreach (var endpoint in audio.Discover()) {
                    MicrophoneEffects? effectState = null; SidetoneState? sideState = null; PickupPatternState? patternState = null;
                    string? effectError = null, sideError = null, patternError = null;
                    if (endpoint is { Direction: AudioDirection.Microphone, Usb: { VendorId: "1395", ProductId: "009F" } }) {
                        try { effectState = effects.Read(endpoint); } catch (Exception ex) { effectError = ex.Message; }
                        try { sideState = sidetone.Read(endpoint); } catch (Exception ex) { sideError = ex.Message; }
                        queried = true;
                        try { patternState = patterns.ReadAsync(endpoint).GetAwaiter().GetResult(); } catch (Exception ex) { patternError = ex.Message; }
                    }
                    rows.Add(new(endpoint, effectState, sideState, patternState, effectError, sideError, patternError));
                }
                var snapshot = new DependencySnapshot(DateTimeOffset.UtcNow, false, queried, rows);
                var path = Path.GetFullPath(args[i + 1]); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var options = new JsonSerializerOptions { WriteIndented = true }; options.Converters.Add(new JsonStringEnumConverter());
                File.WriteAllText(path, JsonSerializer.Serialize(snapshot, options));
                Console.WriteLine($"Dependency/status snapshot: {path}"); exit = 0;
            } catch (Exception ex) { Console.Error.WriteLine(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return exit;
    }
}
