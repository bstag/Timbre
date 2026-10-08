using System.Runtime.Versioning;
using System.Text.Json;
using EposControl.Core;

internal static class HardwareGsxSidetoneTests
{
    [SupportedOSPlatform("windows")]
    public static int Run(string[] args)
    {
        var report = args[Array.IndexOf(args, "--report") + 1]; var errors = new List<string>(); var steps = new List<object>();
        var audio = new CoreAudioBackend(); var control = WindowsGsxSidetone.Create(audio);
        AudioEndpoint? endpoint = null; GsxSidetoneState? before = null, owned = null, restored = null;
        var snapshots = new Dictionary<string, byte[]>(); var audioBefore = new Dictionary<string, AudioState>();
        try {
            var endpoints = audio.Discover(); endpoint = endpoints.Single(GsxSidetoneProtocol.Supports);
            foreach (var e in endpoints) audioBefore[e.Id] = audio.Read(e.Id);
            foreach (var pid in endpoints.Where(e => e.Usb is { } usb && usb.VendorId.Equals("1395", StringComparison.OrdinalIgnoreCase) &&
                (usb.ProductId.Equals("0098", StringComparison.OrdinalIgnoreCase) || usb.ProductId.Equals("009f", StringComparison.OrdinalIgnoreCase)))
                .Select(e => e.Usb!.ProductId.ToLowerInvariant()).Distinct()) snapshots[pid] = Snapshot(pid);
            before = control.Read(endpoint); owned = before;
            // Keep both apps idle at a known state; no audio is generated or recorded.
            foreach (var percent in new[] { 25d, 50d, 75d, 0d, 100d }) {
                var desired = GsxSidetoneSettings.FromPercent(percent);
                owned = control.Apply(endpoint, owned, desired);
                if (owned.Settings != desired || control.Read(endpoint) != owned) throw new IOException("GSX hardware readback mismatch.");
                foreach (var snapshot in snapshots) if (!Snapshot(snapshot.Key).SequenceEqual(snapshot.Value)) throw new IOException("Processor buffer changed during hardware sidetone update.");
                foreach (var state in audioBefore) if (audio.Read(state.Key) != state.Value) throw new IOException("Windows volume/mute changed during hardware sidetone update.");
                steps.Add(new { RequestedPercent = percent, Desired = desired, Verified = owned });
            }
        } catch (Exception ex) { errors.Add(ex.ToString()); }
        finally {
            if (endpoint is not null && before is not null) {
                try {
                    var current = control.Read(endpoint);
                    if (current != (owned ?? before)) throw new IOException("Newer GSX sidetone detected; preserving it instead of overwriting it.");
                    restored = control.Apply(endpoint, current, before.Settings);
                    if (restored != before) throw new IOException("GSX restoration mismatch.");
                    foreach (var snapshot in snapshots) if (!Snapshot(snapshot.Key).SequenceEqual(snapshot.Value)) throw new IOException("Processor buffer changed after sidetone restoration.");
                    foreach (var state in audioBefore) if (audio.Read(state.Key) != state.Value) throw new IOException("Windows volume/mute changed after sidetone restoration.");
                } catch (Exception ex) { errors.Add("Restoration: " + ex); }
            }
        }
        var passed = errors.Count == 0 && before is not null && restored == before;
        File.WriteAllText(Path.GetFullPath(report), JsonSerializer.Serialize(new { Passed = passed, Endpoint = endpoint, Before = before, Steps = steps, Restored = restored,
            SettingsRestored = restored is not null && restored == before, ProcessorBuffersAndWindowsControlsPreserved = errors.Count == 0,
            ObservedProcessorProducts = snapshots.Keys.ToArray(),
            AudioPlayed = false, AudioCaptured = false, ListeningValidated = false, Errors = errors }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("GSX sidetone hardware " + (passed ? "PASS" : "FAIL") + ": " + Path.GetFullPath(report));
        return passed ? 0 : 1;
    }
    [SupportedOSPlatform("windows")]
    private static byte[] Snapshot(string product)
    {
        using var session = new WindowsApoMemory.Session("Global\\CF4B411F-BE2B-4D84-8106-EC27CA0F8F05_1395_" + product,
            (_, _) => throw new InvalidOperationException("Sidetone test processor capture is read-only."));
        return session.Read();
    }
}
