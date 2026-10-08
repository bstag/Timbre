using System.Text.Json.Serialization;

namespace EposControl.Core;

public sealed record GsxSidetoneSettings([property: JsonRequired] int RawValue)
{
    public void Validate() { if (RawValue != 0 && RawValue is < 199 or > 255) throw new InvalidDataException("Unknown GSX sidetone value."); }
    [JsonIgnore] public double Percent { get { Validate(); return GsxSidetoneMap.Position(RawValue) * 100.0 / 255; } }
    public static GsxSidetoneSettings FromPercent(double percent)
    {
        if (!double.IsFinite(percent) || percent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        return new(GsxSidetoneMap.Value((int)Math.Round(percent * 255 / 100, MidpointRounding.AwayFromZero)));
    }
}
public sealed record GsxSidetoneState(string ControlIdentity, GsxSidetoneSettings Settings)
{
    public void Validate() { if (string.IsNullOrWhiteSpace(ControlIdentity) || Settings is null) throw new InvalidDataException("GSX sidetone identity/settings are missing."); Settings.Validate(); }
}
public interface IGsxSidetoneBackend
{
    GsxSidetoneState Read(AudioEndpoint endpoint, CancellationToken cancellation = default);
    GsxSidetoneState Apply(AudioEndpoint endpoint, GsxSidetoneState expected, GsxSidetoneSettings desired, CancellationToken cancellation = default);
}
public interface IGsxSidetoneSession : IDisposable
{
    GsxSidetoneState Read(CancellationToken cancellation);
    void Write(GsxSidetoneSettings desired, CancellationToken cancellation);
}
public static class GsxSidetoneProtocol
{
    public static bool Supports(AudioEndpoint endpoint) => endpoint.Direction == AudioDirection.Microphone && endpoint.Usb is { } usb &&
        usb.VendorId.Equals("1395", StringComparison.OrdinalIgnoreCase) && usb.ProductId.Equals("0098", StringComparison.OrdinalIgnoreCase);
    public static void ValidateEndpoint(AudioEndpoint endpoint) { if (!Supports(endpoint)) throw new InvalidOperationException("GSX sidetone requires its microphone endpoint."); }
    public static byte[] Query { get { var report = new byte[39]; report[0] = 4; report[2] = 1; report[3] = 0x12; report[4] = 0x7B; return report; } }
    public static byte[] Set(GsxSidetoneSettings value) { value.Validate(); var report = Query; report[1] = 0x40; report[5] = (byte)value.RawValue; return report; }
    public static GsxSidetoneSettings Decode(ReadOnlySpan<byte> response)
    {
        if (response.Length != 35 || response[0] != 5 || response[2..].IndexOfAnyExcept((byte)0) >= 0) throw new InvalidDataException("Unknown GSX sidetone response.");
        var value = new GsxSidetoneSettings(response[1]); value.Validate(); return value;
    }
}
public sealed class GsxSidetoneBackend(Func<AudioEndpoint, IGsxSidetoneSession> open) : IGsxSidetoneBackend
{
    public GsxSidetoneState Read(AudioEndpoint endpoint, CancellationToken cancellation = default)
    {
        GsxSidetoneProtocol.ValidateEndpoint(endpoint); cancellation.ThrowIfCancellationRequested();
        using var session = open(endpoint); return Stable(session, cancellation);
    }
    private static GsxSidetoneState Stable(IGsxSidetoneSession session, CancellationToken cancellation)
    {
        var first = session.Read(cancellation); first.Validate(); var second = session.Read(cancellation); second.Validate();
        if (first != second) throw new IOException("GSX sidetone changed between status queries. Reload before adjusting it.");
        return second;
    }
    public GsxSidetoneState Apply(AudioEndpoint endpoint, GsxSidetoneState expected, GsxSidetoneSettings desired, CancellationToken cancellation = default)
    {
        GsxSidetoneProtocol.ValidateEndpoint(endpoint); expected.Validate(); desired.Validate(); cancellation.ThrowIfCancellationRequested();
        using var session = open(endpoint);
        var before = Stable(session, cancellation);
        if (before != expected) throw new InvalidOperationException("GSX sidetone changed. Reload before applying.");
        if (before.Settings == desired) return before;
        // A second fresh read immediately before the setting command reduces stale-draft races.
        if (Stable(session, cancellation) != before) throw new IOException("GSX sidetone changed before its update; newer settings were preserved.");
        var attempted = false;
        try {
            cancellation.ThrowIfCancellationRequested(); attempted = true; session.Write(desired, cancellation);
            var result = Stable(session, cancellation);
            if (result.ControlIdentity != before.ControlIdentity || result.Settings != desired) throw new IOException("GSX sidetone readback differs from the requested value.");
            return result;
        } catch (Exception failure) {
            if (!attempted) throw;
            try {
                // Restoration gets its own short deadline after a canceled or timed-out update.
                using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var current = Stable(session, recovery.Token);
                if (current.ControlIdentity != before.ControlIdentity || current.Settings != before.Settings && current.Settings != desired)
                    throw new IOException("GSX sidetone changed during failure; preserving the newer value.");
                if (current.Settings != before.Settings) session.Write(before.Settings, recovery.Token);
                if (Stable(session, recovery.Token) != before) throw new IOException("GSX sidetone restoration did not verify.");
            } catch (Exception rollback) { throw new AggregateException("GSX sidetone update failed and restoration was not completed.", failure, rollback); }
            throw;
        }
    }
}
public sealed class DemoGsxSidetoneBackend : IGsxSidetoneBackend
{
    private readonly Dictionary<string, GsxSidetoneState> states = [];
    public GsxSidetoneState Read(AudioEndpoint endpoint, CancellationToken cancellation = default)
    {
        GsxSidetoneProtocol.ValidateEndpoint(endpoint); cancellation.ThrowIfCancellationRequested();
        return states.GetValueOrDefault(endpoint.ProfileIdentity, new("demo-gsx-" + endpoint.ProfileIdentity, new(226)));
    }
    public GsxSidetoneState Apply(AudioEndpoint endpoint, GsxSidetoneState expected, GsxSidetoneSettings desired, CancellationToken cancellation = default)
    {
        desired.Validate(); if (Read(endpoint, cancellation) != expected) throw new InvalidOperationException("GSX sidetone changed.");
        return states[endpoint.ProfileIdentity] = expected with { Settings = desired };
    }
}
