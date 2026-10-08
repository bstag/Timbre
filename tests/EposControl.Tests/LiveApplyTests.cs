using EposControl.Core;

internal static class LiveApplyTests
{
    public static void Run(TestSuite suite)
    {
        var first = new ControlTarget("output", "device"); var second = new ControlTarget("input", "device");
        LiveApplyQueue Queue() => new(TimeSpan.FromMilliseconds(150));
        suite.Case("Live queue starts empty and reads do not create work", () => {
            var queue = Queue(); TestSuite.Assert(!queue.HasPending && queue.Take(first, TimeSpan.FromSeconds(1)).Count == 0);
        });
        suite.Case("Live changes wait for the debounce interval", () => {
            var queue = Queue(); queue.Schedule(first, ControlArea.Playback, TimeSpan.Zero);
            TestSuite.Assert(queue.Take(first, TimeSpan.FromMilliseconds(149)).Count == 0 && queue.HasPending);
            TestSuite.Assert(queue.Take(first, TimeSpan.FromMilliseconds(150)).SequenceEqual([ControlArea.Playback]) && !queue.HasPending);
        });
        suite.Case("Repeated slider movements coalesce into one latest update", () => {
            var queue = Queue(); queue.Schedule(first, ControlArea.Playback, TimeSpan.Zero);
            queue.Schedule(first, ControlArea.Playback, TimeSpan.FromMilliseconds(100));
            TestSuite.Assert(queue.Take(first, TimeSpan.FromMilliseconds(150)).Count == 0);
            TestSuite.Assert(queue.Take(first, TimeSpan.FromMilliseconds(250)).Count == 1);
            TestSuite.Assert(queue.Take(first, TimeSpan.FromMilliseconds(500)).Count == 0);
        });
        suite.Case("Independent control sections are each queued once", () => {
            var queue = Queue(); queue.Schedule(first, ControlArea.Sidetone, TimeSpan.Zero);
            queue.Schedule(first, ControlArea.Volume, TimeSpan.Zero); queue.Schedule(first, ControlArea.Volume, TimeSpan.Zero);
            TestSuite.Assert(queue.Take(first, TimeSpan.FromSeconds(1)).SequenceEqual([ControlArea.Volume, ControlArea.Sidetone]));
        });
        suite.Case("Continuous slider movement cannot postpone live updates indefinitely", () => {
            var queue = Queue();
            foreach (var time in new[] { 0, 100, 200, 290 }) queue.Schedule(first, ControlArea.Volume, TimeSpan.FromMilliseconds(time));
            TestSuite.Assert(queue.Take(first, TimeSpan.FromMilliseconds(299)).Count == 0);
            TestSuite.Assert(queue.Take(first, TimeSpan.FromMilliseconds(300)).SequenceEqual([ControlArea.Volume]));
        });
        suite.Case("Queued edits cannot follow a device/page selection", () => {
            var queue = Queue(); queue.Schedule(first, ControlArea.Playback, TimeSpan.Zero);
            TestSuite.Assert(queue.Take(second, TimeSpan.FromSeconds(1)).Count == 0 && !queue.HasPending);
        });
        suite.Case("Reconnect with changed endpoint or identity cancels old work", () => {
            foreach (var target in new[] { first with { EndpointId = "replacement" }, first with { DeviceIdentity = "other-unit" } }) {
                var queue = Queue(); queue.Schedule(first, ControlArea.Volume, TimeSpan.Zero);
                TestSuite.Assert(queue.Take(target, TimeSpan.FromSeconds(1)).Count == 0 && !queue.HasPending);
            }
        });
        suite.Case("Disconnected endpoint discards queued live edits", () => {
            var queue = Queue(); queue.Schedule(first, ControlArea.Volume, TimeSpan.Zero);
            TestSuite.Assert(queue.Take(null, TimeSpan.FromSeconds(1)).Count == 0 && !queue.HasPending);
        });
        suite.Case("Cancel covers manual mode, discard, profile load and shutdown", () => {
            var queue = Queue(); queue.Schedule(first, ControlArea.MicrophoneProcessing, TimeSpan.Zero); queue.Cancel();
            TestSuite.Assert(!queue.HasPending && queue.Take(first, TimeSpan.FromSeconds(1), true).Count == 0);
        });
        suite.Case("Saving can flush the latest live edits immediately and once", () => {
            var queue = Queue(); queue.Schedule(first, ControlArea.Playback, TimeSpan.Zero);
            TestSuite.Assert(queue.Take(first, TimeSpan.FromMilliseconds(1), true).SequenceEqual([ControlArea.Playback]));
            TestSuite.Assert(queue.Take(first, TimeSpan.FromSeconds(1)).Count == 0);
        });
        suite.Case("A new target edit replaces the old target's pending work", () => {
            var queue = Queue(); queue.Schedule(first, ControlArea.Playback, TimeSpan.Zero);
            queue.Schedule(second, ControlArea.MicrophoneProcessing, TimeSpan.Zero);
            TestSuite.Assert(queue.Take(second, TimeSpan.FromSeconds(1)).SequenceEqual([ControlArea.MicrophoneProcessing]));
        });
    }
}
