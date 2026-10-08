using System.Runtime.Versioning;
using System.Text.Json;
using EposControl.Core;

internal static class AudioRecoveryCommand
{
    [SupportedOSPlatform("windows")]
    public static int Run(string[] args)
    {
        var rows = new List<object>(); string? error = null;
        string Value(string name) { var i = Array.IndexOf(args, name); if (i < 0 || i + 1 >= args.Length) throw new ArgumentException("Missing " + name); return args[i + 1]; }
        try {
            var baseline = Load(Value("--restore-audio-state")); var expected = Load(Value("--expected-audio-state"));
            Restore(baseline, expected, new CoreAudioBackend(false), rows);
        } catch (Exception ex) { error = ex.ToString(); }
        var path = Path.GetFullPath(Value("--report"));
        File.WriteAllText(path, JsonSerializer.Serialize(new { Passed = error is null, Rows = rows, Error = error,
            Scope = "Restore captured Windows level/mute only; refuses edits after the expected-state capture" }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(error is null ? "Captured Windows audio levels/mutes restored and verified." : error);
        return error is null ? 0 : 1;
    }

    internal static IReadOnlyList<object> Restore(ControlDiagnosticReport baseline, ControlDiagnosticReport expected, IAudioBackend audio, List<object>? rows = null)
    {
            Validate(baseline); Validate(expected);
            rows ??= [];
            var endpoints = audio.Discover();
            // Validate every identity and expected value before the first write.
            var plan = baseline.Controls.Where(c => c.Audio.Status == "Available").Select(prior => {
                var endpoint = endpoints.Single(e => ProfileStore.DeviceIdentity(e) == prior.DeviceIdentity);
                var captured = expected.Controls.Single(c => c.DeviceIdentity == prior.DeviceIdentity);
                if (captured.EndpointId != endpoint.Id || captured.Audio.Status != "Available" || captured.Audio.Value is null || prior.Audio.Value is null)
                    throw new InvalidDataException("Audio recovery identity or readable state is missing.");
                foreach (var state in new[] { captured.Audio.Value, prior.Audio.Value })
                    if (!float.IsFinite(state.Level) || state.Level is < 0 or > 1 || !float.IsFinite(state.Decibels))
                        throw new InvalidDataException("Invalid captured Windows audio level.");
                if (captured.Audio.Value.HardwareSupport != prior.Audio.Value.HardwareSupport)
                    throw new InvalidDataException("Audio hardware capabilities changed; recovery refused.");
                if (audio.Read(endpoint.Id) != captured.Audio.Value) throw new InvalidOperationException("Audio changed since the recovery capture; restoration refused.");
                return (endpoint, prior.Audio.Value, Expected: captured.Audio.Value);
            }).ToArray();
            foreach (var (endpoint, target, captured) in plan) {
                if (audio.Read(endpoint.Id) != captured) throw new InvalidOperationException("Concurrent audio change; restoration refused.");
                var changed = captured.Level != target.Level || captured.Muted != target.Muted;
                var restored = changed ? audio.Apply(endpoint.Id, target.Level, target.Muted) : captured;
                rows.Add(new { Endpoint = endpoint.Id, DeviceIdentity = ProfileStore.DeviceIdentity(endpoint), Before = captured, Target = target, Restored = restored, SettingsWrites = changed });
                if (Math.Abs(restored.Level - target.Level) > .00001f || restored.Muted != target.Muted || Math.Abs(restored.Decibels - target.Decibels) > .01f)
                    throw new IOException("Windows audio state did not restore to its captured value.");
            }
            return rows;
    }
    private static ControlDiagnosticReport Load(string path)
    {
        var report = JsonSerializer.Deserialize<ControlDiagnosticReport>(File.ReadAllText(path)) ?? throw new InvalidDataException("Missing diagnostic report.");
        Validate(report);
        return report;
    }
    private static void Validate(ControlDiagnosticReport report)
    {
        if (report.SchemaVersion != 1 || report.Demo || report.SettingsWrites || report.Controls.Count == 0 ||
            report.Controls.Select(c => c.DeviceIdentity).Distinct().Count() != report.Controls.Count)
            throw new InvalidDataException("Recovery requires a real read-only diagnostic capture.");
    }
}
