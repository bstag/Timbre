using Timbre.Core;

internal static class PlaybackAudioContextTests
{
    private static readonly AudioEndpoint Output = new DemoAudioBackend().Discover().Single(e => e.Direction == AudioDirection.Playback);
    private static string[] Required(string? identity = null) => ["--require-suite-stopped", "--expected-device-identity", identity ?? ProfileStore.DeviceIdentity(Output)];
    public static void Run(TestSuite suite)
    {
        suite.Case("Ordinary playback diagnostic does not query or require vendor service state", () => {
            var calls = 0; var context = PlaybackAudioContext.FromArguments([], () => calls++); context.Validate(Output);
            TestSuite.Assert(!context.SuiteStopRequired && context.SuiteStoppedChecks == 0 && calls == 0);
        });
        suite.Case("Fresh playback context binds the captured physical device before service checks", () => {
            var calls = 0; var context = PlaybackAudioContext.FromArguments(Required(), () => calls++);
            TestSuite.Reject(() => context.Validate(Output with { Usb = Output.Usb! with { InstanceId = "replacement" } }));
            TestSuite.Assert(calls == 0 && context.SuiteStoppedChecks == 0);
            context.Validate(Output); TestSuite.Assert(calls == 1 && context.SuiteStoppedChecks == 1);
        });
        suite.Case("Stopped-service diagnostic refuses missing or ambiguous argument values", () => {
            foreach (var args in new[] { new[] { "--require-suite-stopped" }, new[] { "--expected-device-identity" },
                new[] { "--expected-device-identity", "--report", "file.json" }, new[] { "--expected-device-identity", " " },
                new[] { "--expected-device-identity", "first", "--expected-device-identity", "second" } })
                TestSuite.Throws<ArgumentException>(() => PlaybackAudioContext.FromArguments(args, () => throw new Exception("No service query expected.")));
        });
        suite.Case("Fresh playback context repeatedly checks stopped support and detects restart", () => {
            var calls = 0; var context = PlaybackAudioContext.FromArguments(Required(), () => { if (++calls == 3) throw new InvalidOperationException("Vendor support restarted."); });
            context.Validate(Output); context.Validate(Output); TestSuite.Reject(() => context.Validate(Output));
            TestSuite.Assert(context.SuiteStoppedChecks == 2 && calls == 3);
        });
        suite.Case("Playback context rejects microphone and other-model routing before service query", () => {
            var calls = 0; var context = PlaybackAudioContext.FromArguments(Required(), () => calls++);
            TestSuite.Reject(() => context.Validate(Output with { Direction = AudioDirection.Microphone }));
            TestSuite.Reject(() => context.Validate(Output with { Usb = Output.Usb! with { ProductId = "009F" } }));
            TestSuite.Assert(calls == 0);
        });
        suite.Case("Explicit device binding also protects ordinary playback diagnostics", () => {
            var context = PlaybackAudioContext.FromArguments(["--expected-device-identity", ProfileStore.DeviceIdentity(Output)], () => throw new Exception("No service query expected."));
            context.Validate(Output); TestSuite.Assert(!context.SuiteStopRequired);
            TestSuite.Reject(() => context.Validate(Output with { Usb = Output.Usb! with { InstanceId = "OTHER" } }));
        });
    }
}
