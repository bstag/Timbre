using System.Text.Json;
using System.Runtime.Versioning;
using EposControl.Core;

[SupportedOSPlatform("windows")]
internal static class HardwareEffectsTests
{
    public static int Run(string[] args)
    {
        try { return RunCore(args); }
        catch (Exception ex) {
            var index = Array.IndexOf(args, "--report");
            if (index >= 0 && index + 1 < args.Length) {
                var path = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(new { Passed = false, Error = ex.ToString(), Scope = "Hardware check setup failed; no test writes attempted" }, new JsonSerializerOptions { WriteIndented = true }));
            }
            Console.Error.WriteLine(ex.Message); return 1;
        }
    }
    private static int RunCore(string[] args)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        string Value(string name) { var i = Array.IndexOf(args, name); if (i < 0 || i + 1 >= args.Length) throw new ArgumentException("Missing " + name); return args[i + 1]; }
        var product = Value("--hardware-effects");
        if (product is not ("009f" or "0098")) throw new ArgumentException("Only validated models can be exercised.");
        var report = Path.GetFullPath(Value("--report"));
        var audio = new CoreAudioBackend();
        var endpoint = audio.Discover().Single(e => e.Direction == AudioDirection.Microphone && e.Usb?.VendorId == "1395" && e.Usb.ProductId.Equals(product, StringComparison.OrdinalIgnoreCase));
        var backend = WindowsApoMemory.Create(audio);
        var before = backend.Read(endpoint);
        byte[] Snapshot(string pid) {
            using var session = new WindowsApoMemory.Session("Global\\CF4B411F-BE2B-4D84-8106-EC27CA0F8F05_1395_" + pid,
                (offset, length) => throw new InvalidOperationException("Diagnostic snapshot is read-only."));
            return session.Read();
        }
        var beforeBytes = Snapshot(product);
        var other = audio.Discover().Where(e => e.Direction == AudioDirection.Microphone && e.Usb?.VendorId == "1395" &&
            e.Usb.ProductId.ToLowerInvariant() is "009f" or "0098" && !e.Usb.ProductId.Equals(product, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(e => e.Usb!.ProductId.ToLowerInvariant(), e => Snapshot(e.Usb!.ProductId.ToLowerInvariant()));
        var beforeAudio = audio.Discover().ToDictionary(e => e.Id, e => audio.Read(e.Id));
        bool unownedPreserved = false, otherDevicesPreserved = false, audioPreserved = false;
        var restore = args.Contains("--restore-from") ? ApoMicrophoneCodec.Read(File.ReadAllBytes(Value("--restore-from"))) : before;
        MicrophoneEffects? changed = null, restored = null;
        Exception? failure = null;
        try {
            var processing = before.Processing ?? throw new InvalidDataException("Complete processing state is required for hardware tests.");
            var target = new MicrophoneEffects(before.GatePercent > 5 ? before.GatePercent - 1 : before.GatePercent + 1, (before.FilterLevel + 1) % 3,
                new(!processing.FilterEnabled, !processing.GateEnabled, product == "009f" ? !processing.HighPassEnabled : processing.HighPassEnabled, !processing.EqualizerEnabled,
                    MicrophoneEqPresets.Warm with { Band9 = processing.Equalizer.Band9 >= .25f ? 0 : .25f }));
            changed = backend.Apply(endpoint, before, target);
            TestSuite.Assert(ApoMicrophoneCodec.Matches(changed, target));
            var currentBytes = Snapshot(product);
            var ownedBytes = ApoMicrophoneCodec.Prepare(beforeBytes, target).SelectMany(p => Enumerable.Range(p.Offset, p.After.Length)).ToHashSet();
            unownedPreserved = Enumerable.Range(64, 104).Where(i => !ownedBytes.Contains(i)).All(i => currentBytes[i] == beforeBytes[i]);
            TestSuite.Assert(unownedPreserved, "Microphone test changed playback or an unowned field");
        } catch (Exception ex) { failure = ex; }
        finally {
            try {
                // Restoration uses the last state owned by this test; concurrent edits fail closed.
                restored = backend.Apply(endpoint, changed ?? before, restore);
            } catch (Exception ex) { failure = failure is null ? ex : new AggregateException(failure, ex); }
        }
        try {
            otherDevicesPreserved = other.All(p => Snapshot(p.Key).AsSpan(64, 104).SequenceEqual(p.Value.AsSpan(64, 104)));
            audioPreserved = beforeAudio.All(p => audio.Read(p.Key) == p.Value);
            TestSuite.Assert(otherDevicesPreserved && audioPreserved, "Another device or Windows level/mute changed");
            if (!args.Contains("--restore-from")) TestSuite.Assert(Snapshot(product).AsSpan(64, 104).SequenceEqual(beforeBytes.AsSpan(64, 104)), "Complete starting processing configuration was not restored");
        } catch (Exception ex) { failure = failure is null ? ex : new AggregateException(failure, ex); }
        var passed = failure is null && restored is not null && ApoMicrophoneCodec.Matches(restored, restore);
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = passed, endpoint.Name, endpoint.ProfileIdentity, Before = before, Changed = changed, RestoreTarget = restore, Restored = restored,
            UnownedConfigurationPreserved = unownedPreserved, OtherDeviceConfigurationsPreserved = otherDevicesPreserved, WindowsAudioControlsPreserved = audioPreserved,
            Error = failure?.ToString(), Scope = "Control state readback; does not measure audible DSP output" }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Hardware effects {(passed ? "PASS" : "FAIL")}: {report}");
        return passed ? 0 : 1;
    }
}
