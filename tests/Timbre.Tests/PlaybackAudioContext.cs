using Timbre.Core;

// Context required by the fresh-initialization runner, kept separate from sound
// measurement so ordinary diagnostics never change or depend on service state.
internal sealed class PlaybackAudioContext(string? expectedDeviceIdentity, Action? requireSuiteStopped)
{
    internal string? ExpectedDeviceIdentity { get; } = expectedDeviceIdentity;
    internal bool SuiteStopRequired => requireSuiteStopped is not null;
    internal int SuiteStoppedChecks { get; private set; }

    internal static PlaybackAudioContext FromArguments(string[] args, Action requireStopped)
    {
        var index = Array.IndexOf(args, "--expected-device-identity");
        if (args.Count(a => a == "--expected-device-identity") > 1)
            throw new ArgumentException("Specify --expected-device-identity only once.");
        string? expected = null;
        if (index >= 0) {
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--") || string.IsNullOrWhiteSpace(args[index + 1]))
                throw new ArgumentException("Missing value for --expected-device-identity.");
            expected = args[index + 1];
        }
        var stopped = args.Contains("--require-suite-stopped");
        if (stopped && expected is null) throw new ArgumentException("Stopped-service playback measurement requires --expected-device-identity.");
        return new(expected, stopped ? requireStopped : null);
    }

    internal void Validate(AudioEndpoint endpoint)
    {
        ApoPlaybackCodec.ValidateEndpoint(endpoint);
        if (ExpectedDeviceIdentity is not null && ProfileStore.DeviceIdentity(endpoint) != ExpectedDeviceIdentity)
            throw new InvalidOperationException("The selected GSX playback device differs from the captured physical identity; measurement refused.");
        if (requireSuiteStopped is not null) {
            requireSuiteStopped(); SuiteStoppedChecks++;
        }
    }
}
