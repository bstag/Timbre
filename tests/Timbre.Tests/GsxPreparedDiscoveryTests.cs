using Timbre.Core;

internal static class GsxPreparedDiscoveryTests
{
    public static void Run(TestSuite suite)
    {
        var devices = new DemoAudioBackend().Discover();
        var mic = devices.Single(e => e.Usb!.ProductId == "0098" && e.Direction == AudioDirection.Microphone);
        var output = devices.Single(e => e.Direction == AudioDirection.Playback);
        var seed = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "apo-memory-0098.bin"));
        var state = new GsxApoStartupState(1, mic.Usb!.InstanceId, ApoMicrophoneCodec.Read(seed), ApoPlaybackCodec.Read(seed), seed) {
            MicrophoneEndpointId = mic.Id, PlaybackEndpointId = output.Id };
        suite.Case("Prepared GSX discovery survives the captured empty metadata boundary while audio is stopped", () => {
            var paused = false; var audioCalls = 0;
            var discovery = new GsxInitializationDiscovery(state, () => { audioCalls++; return paused ? [] : devices; },
                () => paused, () => [state.DeviceInstance]);
            discovery.Prepare(); paused = true;
            var current = discovery.Discover();
            TestSuite.Assert(current.Count == 2 && current[0].Id == mic.Id && current[1].Id == output.Id && audioCalls == 1,
                "Paused discovery must use the live-prepared pair and current PnP presence without asking the unavailable audio service.");
        });
        suite.Case("Prepared GSX discovery returns to live endpoint validation when audio resumes", () => {
            var paused = false; IReadOnlyList<AudioEndpoint> current = devices;
            var discovery = new GsxInitializationDiscovery(state, () => current, () => paused, () => [state.DeviceInstance]);
            discovery.Prepare(); paused = true; current = []; discovery.Discover();
            paused = false; current = devices;
            TestSuite.Assert(discovery.Discover().Count == 2 && discovery.Method == "ActivePairAndCurrentPnp");
            current = [];
            TestSuite.Reject(() => discovery.Discover());
        });
        suite.Case("Prepared GSX discovery refuses USB removal, replacement or a second unit during the pause", () => {
            var paused = false; IReadOnlyList<string> usb = [state.DeviceInstance];
            var discovery = new GsxInitializationDiscovery(state, () => devices, () => paused, () => usb);
            discovery.Prepare(); paused = true;
            foreach (var changed in new IReadOnlyList<string>[] { [], ["USB\\VID_1395&PID_0098\\OTHER"],
                [state.DeviceInstance, "USB\\VID_1395&PID_0098\\OTHER"] }) {
                usb = changed; TestSuite.Reject(() => discovery.Discover());
            }
        });
        suite.Case("GSX preparation refuses an already stopped audio engine or an unprepared discovery call", () => {
            var discovery = new GsxInitializationDiscovery(state, () => devices, () => true, () => [state.DeviceInstance]);
            TestSuite.Reject(() => discovery.Prepare()); TestSuite.Reject(() => discovery.Discover());
        });
        suite.Case("GSX preparation refuses invalid live metadata and mismatched seed before the pause", () => {
            foreach (var bad in new IReadOnlyList<AudioEndpoint>[] { [], devices.Where(e => e.Id != mic.Id).ToArray(), [.. devices, mic] }) {
                var discovery = new GsxInitializationDiscovery(state, () => bad, () => false, () => [state.DeviceInstance]);
                TestSuite.Reject(() => discovery.Prepare()); TestSuite.Reject(() => discovery.Discover());
            }
            var wrong = state with { Microphone = state.Microphone with { GatePercent = 101 } };
            TestSuite.Reject(() => new GsxInitializationDiscovery(wrong, () => devices, () => false, () => [state.DeviceInstance]).Prepare());
        });
        suite.Case("Present GSX uniqueness ignores interface children and case aliases but includes other roots", () => {
            var roots = GsxInitializationDiscovery.PhysicalGsxInstances([state.DeviceInstance, state.DeviceInstance.ToLowerInvariant(),
                "USB\\VID_1395&PID_0098&MI_00\\CHILD", "USB\\VID_1395&PID_009F\\B20", "USB\\VID_1395&PID_0098\\OTHER"]);
            TestSuite.Assert(roots.Length == 2 && roots.Contains(state.DeviceInstance));
        });
    }
}
