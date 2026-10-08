namespace Timbre.Core;

public enum ControlArea { Volume, MicrophoneProcessing, Sidetone, Playback }
public sealed record ControlTarget(string EndpointId, string DeviceIdentity);

// Queued edits belong to one endpoint incarnation. Reads never enqueue work.
public sealed class LiveApplyQueue(TimeSpan delay)
{
    private ControlTarget? target;
    private readonly HashSet<ControlArea> areas = [];
    private TimeSpan due;
    private TimeSpan started;
    public bool HasPending => areas.Count != 0;
    public void Schedule(ControlTarget current, ControlArea area, TimeSpan now)
    {
        if (current != target) Cancel();
        if (!HasPending) started = now;
        target = current; areas.Add(area);
        // Long drags still reach the device at least twice per second with the app's delay.
        var quietDue = now + delay; var maximumDue = started + delay + delay;
        due = quietDue < maximumDue ? quietDue : maximumDue;
    }
    public IReadOnlyList<ControlArea> Take(ControlTarget? current, TimeSpan now, bool force = false)
    {
        if (current != target) { Cancel(); return []; }
        if (!force && now < due) return [];
        var result = areas.Order().ToArray(); Cancel(); return result;
    }
    public void Cancel() { areas.Clear(); target = null; }
}
