using System.Text.Json;
using EposControl.Core;

internal static class HardwareSidetoneTests
{
    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 1;
        var exit = 1;
        var thread = new Thread(() => {
            string Value(string name) { var i = Array.IndexOf(args, name); if (i < 0 || i + 1 >= args.Length) throw new ArgumentException("Missing " + name); return args[i + 1]; }
            object result;
            try {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                if (!Value("--hardware-sidetone").Equals("009f", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Only B20 sidetone is validated.");
                IAudioBackend audio = new CoreAudioBackend(); var endpoints = audio.Discover();
                var endpoint = endpoints.Single(e => e.Direction == AudioDirection.Microphone && e.Usb is { VendorId: "1395", ProductId: "009F" });
                var backend = WindowsSidetone.Create(audio); var before = backend.Read(endpoint); var target = before.Settings;
                if (args.Contains("--restore-from")) {
                    var rows = JsonSerializer.Deserialize<List<Capture>>(File.ReadAllText(Value("--restore-from")))!;
                    var capture = rows.Single(r => r.Endpoint.Id == endpoint.Id && r.Endpoint.ProfileIdentity == endpoint.ProfileIdentity);
                    var volume = B20SidetoneLayout.ValidateLayout(capture.Topology);
                    target = new(volume.Levels![0].Decibels, volume.Levels[1].Decibels, capture.Topology.Parts.Single(p => p.LocalId == 0x20006).Muted!.Value);
                    target.Validate();
                }
                var topology = WindowsAudioTopology.Read(endpoint);
                var levelsBefore = endpoints.ToDictionary(e => e.Id, e => audio.Read(e.Id));
                SidetoneState? changed = null, restored = null; Exception? failure = null;
                try {
                    var left = before.Settings.LeftDb > -31 ? before.Settings.LeftDb - 1.5f : before.Settings.LeftDb + 1.5f;
                    var right = before.Settings.RightDb > -31 ? before.Settings.RightDb - 1.5f : before.Settings.RightDb + 1.5f;
                    changed = backend.Apply(endpoint, before, new(left, right, !before.Settings.Muted));
                    var after = WindowsAudioTopology.Read(endpoint);
                    foreach (var part in topology.Parts.Where(p => p.LocalId is not (0x20006 or 0x20007)))
                        TestSuite.Assert(JsonSerializer.Serialize(part) == JsonSerializer.Serialize(after.Parts.Single(p => p.LocalId == part.LocalId)), "Unrelated B20 topology control changed.");
                    foreach (var e in endpoints) TestSuite.Assert(audio.Read(e.Id) == levelsBefore[e.Id], "Endpoint level or mute changed during sidetone test.");
                } catch (Exception ex) { failure = ex; }
                finally {
                    try { restored = backend.Apply(endpoint, changed ?? before, target); }
                    catch (Exception ex) { failure = failure is null ? ex : new AggregateException(failure, ex); }
                }
                var passed = failure is null && restored is not null && SidetoneBackend.Matches(restored.Settings, target, .00001f);
                result = new { Passed = passed, endpoint.Name, Before = before, Changed = changed, RestoreTarget = target, Restored = restored, UnrelatedControlsChecked = true,
                    Error = failure?.ToString(), Scope = "Hardware sidetone level/mute readback and isolation; headphone listening still required" };
                Console.WriteLine($"B20 sidetone hardware {(passed ? "PASS" : "FAIL")}. Starting capture settings restored: {passed}"); exit = passed ? 0 : 1;
            } catch (Exception ex) { result = new { Passed = false, Error = ex.ToString() }; Console.Error.WriteLine(ex.Message); }
            try {
                var path = Path.GetFullPath(Value("--report")); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            } catch (Exception ex) { Console.Error.WriteLine(ex.Message); exit = 1; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return exit;
    }
    private sealed record Capture(AudioEndpoint Endpoint, AudioTopologySnapshot Topology);
}
