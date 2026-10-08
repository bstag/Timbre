namespace EposControl.Core;

// Values recovered from GamingSuite.UI.Services.MicDirections; cardioid captured live.
public enum PickupPattern { Bidirectional = 1, Cardioid = 2, Omnidirectional = 3, Stereo = 4 }
public sealed record PickupPatternState(PickupPattern Pattern, DateTimeOffset ObservedAt)
{
    public string Label => Pattern switch {
        PickupPattern.Bidirectional => "Bidirectional", PickupPattern.Cardioid => "Cardioid",
        PickupPattern.Omnidirectional => "Omnidirectional", PickupPattern.Stereo => "Stereo",
        _ => throw new InvalidDataException("Unknown B20 pickup pattern.")
    };
}
public interface IPickupPatternBackend
{
    Task<PickupPatternState> ReadAsync(AudioEndpoint endpoint, CancellationToken cancellation = default);
}
public interface IPickupPatternSession : IDisposable
{
    Task SendStatusQueryAsync(ReadOnlyMemory<byte> query, CancellationToken cancellation);
    Task<byte[]> ReadReportAsync(CancellationToken cancellation);
}
public static class B20PatternProtocol
{
    // Static getMicDirection query, not a setter. Return a copy to prevent mutation.
    public static byte[] StatusQuery => [0xF0, 0x05, 0, 0xFF];
    public static void ValidateEndpoint(AudioEndpoint endpoint)
    {
        if (endpoint.Direction != AudioDirection.Microphone || endpoint.Usb is not { VendorId: "1395", ProductId: "009F" })
            throw new InvalidOperationException("Pickup-pattern status is currently mapped only for the B20 microphone.");
    }
    public static bool TryDecode(ReadOnlySpan<byte> report, out PickupPattern pattern)
    {
        pattern = default;
        if (report.Length != 4 || report[0] != 0x80 || report[3] != 0xFF)
            throw new InvalidDataException("Unknown B20 status report layout.");
        // Gain, mute and other events are not pattern reports.
        if (report[1] != 3) return false;
        if (report[2] is < 1 or > 4) throw new InvalidDataException("Unknown B20 pickup-pattern value.");
        pattern = (PickupPattern)report[2]; return true;
    }
}
public sealed class PickupPatternBackend(Func<AudioEndpoint, IPickupPatternSession> open) : IPickupPatternBackend
{
    public async Task<PickupPatternState> ReadAsync(AudioEndpoint endpoint, CancellationToken cancellation = default)
    {
        B20PatternProtocol.ValidateEndpoint(endpoint); cancellation.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(1500));
        try {
            using var session = open(endpoint);
            await session.SendStatusQueryAsync(B20PatternProtocol.StatusQuery, deadline.Token).ConfigureAwait(false);
            // Bound unrelated-event traffic as well as wall-clock I/O.
            for (var i = 0; i < 32; i++) {
                var report = await session.ReadReportAsync(deadline.Token).ConfigureAwait(false);
                if (B20PatternProtocol.TryDecode(report, out var pattern)) return new(pattern, DateTimeOffset.UtcNow);
            }
            throw new IOException("B20 pattern query received too many unrelated status reports.");
        } catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) {
            throw new TimeoutException("B20 pickup-pattern status did not respond in time.");
        }
    }
}
public sealed class DemoPickupPatternBackend : IPickupPatternBackend
{
    private readonly Dictionary<string, PickupPattern> patterns = [];
    public Task<PickupPatternState> ReadAsync(AudioEndpoint endpoint, CancellationToken cancellation = default)
    {
        B20PatternProtocol.ValidateEndpoint(endpoint); cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(new PickupPatternState(patterns.GetValueOrDefault(endpoint.ProfileIdentity, PickupPattern.Cardioid), DateTimeOffset.UtcNow));
    }
    public void SetPhysicalSwitchForDemo(AudioEndpoint endpoint, PickupPattern pattern)
    {
        B20PatternProtocol.ValidateEndpoint(endpoint); _ = new PickupPatternState(pattern, DateTimeOffset.UtcNow).Label;
        patterns[endpoint.ProfileIdentity] = pattern;
    }
}
