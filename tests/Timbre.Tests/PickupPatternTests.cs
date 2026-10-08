using Timbre.Core;

internal static class PickupPatternTests
{
    private static readonly AudioEndpoint Mic = new DemoAudioBackend().Discover().First();
    public static void Run(TestSuite suite)
    {
        void Case(string name, Action action) => suite.Case(name, action);
        Case("Live cardioid HID golden decodes to Suite enum value 2", () => {
            var report = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "b20-pattern-cardioid.bin"));
            TestSuite.Assert(B20PatternProtocol.TryDecode(report, out var pattern) && pattern == PickupPattern.Cardioid);
        });
        Case("Pickup-pattern query is fixed and immutable to callers", () => {
            var query = B20PatternProtocol.StatusQuery; TestSuite.Assert(query.SequenceEqual(new byte[] { 0xF0, 5, 0, 0xFF }));
            query[1] = 0xFF; TestSuite.Assert(B20PatternProtocol.StatusQuery[1] == 5);
        });
        Case("All four Suite enum names decode without inventing patterns", () => {
            var names = new[] { "Bidirectional", "Cardioid", "Omnidirectional", "Stereo" };
            for (byte i = 1; i <= 4; i++) {
                TestSuite.Assert(B20PatternProtocol.TryDecode([0x80, 3, i, 0xFF], out var pattern));
                TestSuite.Assert(new PickupPatternState(pattern, default).Label == names[i - 1]);
            }
        });
        Case("Gain and mute events cannot become pickup-pattern status", () => {
            TestSuite.Assert(!B20PatternProtocol.TryDecode([0x80, 1, 0x13, 0xFF], out _));
            TestSuite.Assert(!B20PatternProtocol.TryDecode([0x80, 2, 1, 0xFF], out _));
        });
        Case("Malformed and unknown pattern reports fail closed", () => {
            foreach (var report in new byte[][] { [], [0x80, 3, 2], [0x81, 3, 2, 0xFF], [0x80, 3, 2, 0], [0x80, 3, 0, 0xFF], [0x80, 3, 5, 0xFF] })
                TestSuite.Throws<InvalidDataException>(() => B20PatternProtocol.TryDecode(report, out _));
        });
        Case("Pattern getter skips unrelated events and sends only one status query", () => {
            var session = new FakeSession([0x80, 1, 19, 0xFF], [0x80, 3, 2, 0xFF]);
            var result = new PickupPatternBackend(_ => session).ReadAsync(Mic).GetAwaiter().GetResult();
            TestSuite.Assert(result.Pattern == PickupPattern.Cardioid && session.Queries == 1 && session.Disposed);
        });
        Case("Unsupported pattern devices reject before opening transport", () => {
            foreach (var endpoint in new[] { Mic with { Direction = AudioDirection.Playback }, Mic with { Usb = null }, Mic with { Usb = Mic.Usb! with { ProductId = "0098" } }, Mic with { Usb = Mic.Usb! with { VendorId = "1234" } } }) {
                var opened = false; var backend = new PickupPatternBackend(_ => { opened = true; throw new Exception(); });
                TestSuite.Throws<InvalidOperationException>(() => backend.ReadAsync(endpoint).GetAwaiter().GetResult()); TestSuite.Assert(!opened);
            }
        });
        Case("Canceled pattern read opens no transport", () => {
            using var cancel = new CancellationTokenSource(); cancel.Cancel(); var opened = false;
            var backend = new PickupPatternBackend(_ => { opened = true; throw new Exception(); });
            TestSuite.Throws<OperationCanceledException>(() => backend.ReadAsync(Mic, cancel.Token).GetAwaiter().GetResult()); TestSuite.Assert(!opened);
        });
        Case("Pattern read failure disposes the native session", () => {
            var session = new FakeSession() { Failure = new IOException("Device removed") };
            TestSuite.Throws<IOException>(() => new PickupPatternBackend(_ => session).ReadAsync(Mic).GetAwaiter().GetResult()); TestSuite.Assert(session.Disposed);
        });
        Case("Unresponsive pattern transport times out and disposes the session", () => {
            var session = new WaitingSession();
            TestSuite.Throws<TimeoutException>(() => new PickupPatternBackend(_ => session).ReadAsync(Mic).GetAwaiter().GetResult());
            TestSuite.Assert(session.Disposed && session.Queries == 1);
        });
        Case("Canceling an active pattern read disposes it without returning cached status", () => {
            var session = new WaitingSession(); using var cancel = new CancellationTokenSource();
            var task = new PickupPatternBackend(_ => session).ReadAsync(Mic, cancel.Token); cancel.Cancel();
            TestSuite.Throws<OperationCanceledException>(() => task.GetAwaiter().GetResult());
            TestSuite.Assert(session.Disposed && session.Queries == 1);
        });
        Case("Unrelated-event flood is bounded and cannot return stale status", () => {
            var session = new FakeSession(Enumerable.Range(0, 32).Select(_ => new byte[] { 0x80, 1, 19, 0xFF }).ToArray());
            TestSuite.Throws<IOException>(() => new PickupPatternBackend(_ => session).ReadAsync(Mic).GetAwaiter().GetResult()); TestSuite.Assert(session.Disposed);
        });
        Case("HID pickup status binds to the selected physical USB instance", () => {
            var good = Device(Mic.Usb!); var other = Device(Mic.Usb! with { InstanceId = "other-b20" });
            TestSuite.Assert(B20PatternDeviceGuard.Select(Mic, [Mic], [other, good]) == good);
            TestSuite.Throws<InvalidOperationException>(() => B20PatternDeviceGuard.Select(Mic, [Mic], [other]));
        });
        Case("Pattern device guard rejects disconnected and replaced endpoints", () => {
            var device = Device(Mic.Usb!);
            TestSuite.Throws<InvalidOperationException>(() => B20PatternDeviceGuard.Select(Mic, [], [device]));
            TestSuite.Throws<InvalidOperationException>(() => B20PatternDeviceGuard.Select(Mic, [Mic with { Usb = Mic.Usb! with { InstanceId = "replacement" } }], [device]));
        });
        Case("Pattern guard rejects ambiguous endpoints and incomplete physical identities", () => {
            var device = Device(Mic.Usb!);
            TestSuite.Throws<InvalidOperationException>(() => B20PatternDeviceGuard.Select(Mic, [Mic, Mic], [device]));
            foreach (var instance in new[] { "", "other-device", "USB\\VID_1395&PID_0098\\OTHER" }) {
                var invalid = Mic with { Usb = Mic.Usb! with { InstanceId = instance } };
                TestSuite.Throws<InvalidDataException>(() => B20PatternDeviceGuard.Select(invalid, [invalid], [Device(invalid.Usb!)]));
            }
        });
        Case("Reconnect uses a new endpoint with the same physical identity", () => {
            var reconnected = Mic with { Id = "new-windows-endpoint" }; var device = Device(Mic.Usb!);
            TestSuite.Assert(B20PatternDeviceGuard.Select(reconnected, [reconnected], [device]) == device);
            TestSuite.Throws<InvalidOperationException>(() => B20PatternDeviceGuard.Select(Mic, [reconnected], [device]));
        });
        Case("Unknown HID descriptor and duplicate interfaces reject before query", () => {
            var good = Device(Mic.Usb!);
            foreach (var bad in new[] { good with { InputBytes = 5 }, good with { OutputBytes = 0 }, good with { FeatureBytes = 1 } })
                TestSuite.Throws<InvalidDataException>(() => B20PatternDeviceGuard.Select(Mic, [Mic], [bad]));
            TestSuite.Throws<InvalidOperationException>(() => B20PatternDeviceGuard.Select(Mic, [Mic], [good, good]));
            TestSuite.Throws<InvalidOperationException>(() => B20PatternDeviceGuard.Select(Mic, [Mic], [good with { UsagePage = 0xC }]));
        });
        Case("Demo physical pattern changes are per-device and independent of processing", () => {
            var backend = new DemoPickupPatternBackend(); backend.SetPhysicalSwitchForDemo(Mic, PickupPattern.Stereo);
            TestSuite.Assert(backend.ReadAsync(Mic).Result.Pattern == PickupPattern.Stereo);
            TestSuite.Assert(backend.ReadAsync(Mic with { Usb = Mic.Usb! with { InstanceId = "other" } }).Result.Pattern == PickupPattern.Cardioid);
        });
    }
    private static B20PatternHidDevice Device(UsbIdentity usb) => new("fixture-hid", usb, 0xFFCD, 1, 4, 4, 0);
    private sealed class WaitingSession : IPickupPatternSession
    {
        public bool Disposed; public int Queries;
        public Task SendStatusQueryAsync(ReadOnlyMemory<byte> query, CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); Queries++; return Task.CompletedTask; }
        public async Task<byte[]> ReadReportAsync(CancellationToken cancellation) { await Task.Delay(Timeout.InfiniteTimeSpan, cancellation); throw new InvalidOperationException(); }
        public void Dispose() => Disposed = true;
    }
    private sealed class FakeSession(params byte[][] reports) : IPickupPatternSession
    {
        private readonly Queue<byte[]> pending = new(reports);
        public int Queries; public bool Disposed; public Exception? Failure;
        public Task SendStatusQueryAsync(ReadOnlyMemory<byte> query, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); TestSuite.Assert(query.Span.SequenceEqual(B20PatternProtocol.StatusQuery)); Queries++;
            return Task.CompletedTask;
        }
        public Task<byte[]> ReadReportAsync(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); if (Failure is not null) throw Failure;
            return Task.FromResult(pending.Dequeue());
        }
        public void Dispose() => Disposed = true;
    }
}
