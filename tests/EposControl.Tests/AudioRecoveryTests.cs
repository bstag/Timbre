using EposControl.Core;

internal static class AudioRecoveryTests
{
    public static void Run(TestSuite suite)
    {
        var endpoints = new DemoAudioBackend().Discover();
        ControlDiagnosticReport Report(IReadOnlyList<AudioEndpoint> items) => new(1, DateTimeOffset.UtcNow, false, false, false, false, items,
            items.Select(e => new EndpointControlDiagnostic(e.Id, ProfileStore.DeviceIdentity(e), new("Available", e.State, null, null),
                new("NotSupported", null, null, null), new("NotSupported", null, null, null), new("NotSupported", null, null, null), [])).ToArray());
        var baseline = Report(endpoints);
        var reset = endpoints.Select(e => e.Usb!.ProductId == "009F" ? e with { State = e.State! with { Level = 0, Decibels = -24 } } : e).ToArray();
        var expected = Report(reset);
        suite.Case("Audio restart recovery restores reset gain and leaves unchanged devices untouched", () => {
            var backend = new RecoveryBackend(reset, endpoints);
            AudioRecoveryCommand.Restore(baseline, expected, backend);
            TestSuite.Assert(backend.Writes == 1 && endpoints.All(e => backend.Read(e.Id) == e.State));
        });
        suite.Case("Audio recovery refuses stale expected values before any writes", () => {
            var backend = new RecoveryBackend(endpoints, endpoints);
            TestSuite.Reject(() => AudioRecoveryCommand.Restore(baseline, expected, backend));
            TestSuite.Assert(backend.Writes == 0);
        });
        suite.Case("Audio recovery refuses concurrent gain edits between planning and applying", () => {
            var backend = new RecoveryBackend(reset, endpoints) { EditOnRead = endpoints.Count + 1 };
            TestSuite.Reject(() => AudioRecoveryCommand.Restore(baseline, expected, backend));
            TestSuite.Assert(backend.Writes == 0);
        });
        suite.Case("Audio recovery refuses missing or duplicate physical identities before any writes", () => {
            foreach (var items in new IReadOnlyList<AudioEndpoint>[] { reset.Skip(1).ToArray(), [.. reset, reset[0]] }) {
                var backend = new RecoveryBackend(items, endpoints);
                TestSuite.Reject(() => AudioRecoveryCommand.Restore(baseline, expected, backend));
                TestSuite.Assert(backend.Writes == 0);
            }
        });
        suite.Case("Audio recovery refuses demo, write-bearing and ambiguous captures", () => {
            var backend = new RecoveryBackend(reset, endpoints);
            foreach (var bad in new[] { baseline with { Demo = true }, baseline with { SettingsWrites = true },
                baseline with { Controls = [.. baseline.Controls, baseline.Controls[0]] },
                baseline with { Controls = baseline.Controls.Select((c, i) => i == 0 ? c with { Audio = c.Audio with { Value = c.Audio.Value! with { Level = float.NaN } } } : c).ToArray() } })
                TestSuite.Reject(() => AudioRecoveryCommand.Restore(bad, expected, backend));
            TestSuite.Assert(backend.Writes == 0);
        });
    }

    private sealed class RecoveryBackend(IReadOnlyList<AudioEndpoint> current, IReadOnlyList<AudioEndpoint> targets) : IAudioBackend
    {
        private readonly Dictionary<string, AudioState> states = current.DistinctBy(e => e.Id).ToDictionary(e => e.Id, e => e.State!);
        private int reads;
        public int Writes { get; private set; }
        public int EditOnRead { get; init; }
        public IReadOnlyList<AudioEndpoint> Discover() => current;
        public AudioState Read(string id)
        {
            if (++reads == EditOnRead) states[id] = states[id] with { Level = .17f };
            return states[id];
        }
        public AudioState Apply(string id, float level, bool muted)
        {
            Writes++;
            return states[id] = targets.Single(e => e.Id == id).State! with { Level = level, Muted = muted };
        }
    }
}
