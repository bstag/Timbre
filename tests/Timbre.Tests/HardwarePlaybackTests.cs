using System.Runtime.Versioning;
using System.Text.Json;
using Timbre.Core;

internal static class HardwarePlaybackTests
{
    [SupportedOSPlatform("windows")]
    public static int Run(string[] args)
    {
        var report = args[Array.IndexOf(args, "--report") + 1];
        var errors = new List<string>(); var steps = new List<object>();
        AudioEndpoint? endpoint = null; PlaybackEffects? before = null, owned = null, restored = null;
        byte[]? beforeBytes = null; var beforeOther = new Dictionary<string, byte[]>();
        IPlaybackEffectsBackend? playback = null;
        try {
            if (args[Array.IndexOf(args, "--hardware-playback") + 1] != "0098") throw new ArgumentException("Only GSX 300 playback is validated.");
            var audio = new CoreAudioBackend(); var endpoints = audio.Discover();
            endpoint = endpoints.Single(e => e.Direction == AudioDirection.Playback && e.Usb is { } u &&
                u.VendorId.Equals("1395", StringComparison.OrdinalIgnoreCase) && u.ProductId.Equals("0098", StringComparison.OrdinalIgnoreCase));
            WindowsApoMemory.ValidatePlaybackIdentity(endpoint, endpoints);
            playback = WindowsApoMemory.CreatePlayback(audio); before = playback.Read(endpoint);
            beforeBytes = Snapshot("0098");
            foreach (var product in endpoints.Select(e => e.Usb?.ProductId.ToLowerInvariant()).Where(p => p == "009f").Distinct())
                beforeOther[product!] = Snapshot(product!);
            var beforeAudio = endpoints.ToDictionary(e => e.Id, e => audio.Read(e.Id));
            foreach (var desired in new[] { before with { SurroundEnabled = !before.SurroundEnabled },
                new PlaybackEffects(before.SurroundEnabled, new(1, -1, 2, -2, 3, -3, 4, -4, 5)),
                before with { SurroundEnabled = true, Reverb = new(true, .5f) },
                before with { SurroundEnabled = true, Reverb = new(false, .5f) }, before }) {
                var expected = owned ?? before;
                owned = playback.Apply(endpoint, expected, desired);
                if (!ApoPlaybackCodec.Matches(owned, desired)) throw new IOException("Playback readback mismatch.");
                var after = Snapshot("0098");
                for (var i = 64; i < 168; i++) if (i != 64 && i != 69 && (i < 76 || i >= 112) && (i < 148 || i >= 152) && after[i] != beforeBytes[i])
                    throw new IOException($"Unowned GSX configuration byte {i} changed.");
                steps.Add(new { Desired = desired, Readback = owned, MicrophoneAndUnownedFieldsPreserved = true });
            }
            foreach (var pair in beforeAudio) {
                var after = audio.Read(pair.Key);
                if (Math.Abs(after.Level - pair.Value.Level) > .0001f || after.Muted != pair.Value.Muted) throw new IOException("Windows level/mute changed.");
            }
        } catch (Exception ex) { errors.Add(ex.ToString()); }
        finally {
            if (endpoint is not null && before is not null && playback is not null) {
                try {
                    var current = playback.Read(endpoint);
                    if (!ApoPlaybackCodec.Matches(current, owned ?? before)) throw new IOException("Newer sound settings detected; restoration cancelled.");
                    restored = playback.Apply(endpoint, current, before);
                    if (beforeBytes is not null && !Snapshot("0098").AsSpan(64, 104).SequenceEqual(beforeBytes.AsSpan(64, 104)))
                        throw new IOException("Starting GSX configuration was not fully restored.");
                } catch (Exception ex) { errors.Add("Restoration: " + ex); }
            }
            foreach (var pair in beforeOther) {
                try { if (!Snapshot(pair.Key).AsSpan(64, 104).SequenceEqual(pair.Value.AsSpan(64, 104))) throw new IOException("B20 processing changed during the GSX test."); }
                catch (Exception ex) { errors.Add("Other device: " + ex); }
            }
        }
        var passed = errors.Count == 0 && before is not null && restored is not null && ApoPlaybackCodec.Matches(restored, before);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = passed, Endpoint = endpoint, Before = before, Steps = steps, Restored = restored,
            SettingsRestored = before is not null && restored is not null && ApoPlaybackCodec.Matches(restored, before), OtherDeviceConfigurationsChecked = beforeOther.Keys,
            AudioListeningValidated = false, SuiteIndependenceValidated = false, Errors = errors }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"GSX 300 playback {(passed ? "PASS" : "FAIL")}. Report: {Path.GetFullPath(report)}");
        return passed ? 0 : 1;
    }
    [SupportedOSPlatform("windows")]
    internal static byte[] Snapshot(string product)
    {
        using var session = new WindowsApoMemory.Session("Global\\CF4B411F-BE2B-4D84-8106-EC27CA0F8F05_1395_" + product,
            (offset, length) => throw new InvalidOperationException("Diagnostic snapshot is read-only."));
        return session.Read();
    }
}
