using System.Runtime.Versioning;
using Native = Timbre.Core.CoreAudioBackend.Native;

namespace Timbre.Core;

[SupportedOSPlatform("windows")]
public static class WindowsSidetone
{
    public static ISidetoneBackend Create(IAudioBackend audio) => new SidetoneBackend(endpoint => {
        SidetoneBackend.ValidateEndpoint(endpoint);
        if (!audio.Discover().Any(e => e.Id == endpoint.Id && e.ProfileIdentity == endpoint.ProfileIdentity))
            throw new InvalidOperationException("The selected B20 microphone is disconnected or its identity changed.");
        return new Session(endpoint);
    });
    private sealed class Session : ISidetoneSession
    {
        private WindowsAudioTopology.Topology? topology;
        private WindowsAudioTopology.Volume? volume;
        private WindowsAudioTopology.Mute? mute;
        private readonly int threadId = Environment.CurrentManagedThreadId;
        private readonly string identity;
        internal Session(AudioEndpoint endpoint)
        {
            var snapshot = WindowsAudioTopology.Read(endpoint); var part = B20SidetoneLayout.ValidateLayout(snapshot); identity = part.GlobalId;
            WindowsAudioTopology.WithTopology(endpoint, value => {
                Native.Check(value.GetPartById(0x20007, out var volumePart));
                try {
                    Native.Check(volumePart.GetGlobalId(out var actual)); if (actual != identity) throw new InvalidOperationException("B20 topology changed during discovery.");
                    Native.Check(volumePart.GetTopologyObject(out topology));
                    var iid = WindowsAudioTopology.VolumeIid; Native.Check(volumePart.Activate(23, ref iid, out var obj)); volume = (WindowsAudioTopology.Volume)obj;
                    Native.Check(value.GetPartById(0x20006, out var mutePart));
                    try { iid = typeof(WindowsAudioTopology.Mute).GUID; Native.Check(mutePart.Activate(23, ref iid, out obj)); mute = (WindowsAudioTopology.Mute)obj; }
                    finally { Native.Release(mutePart); }
                } catch { Dispose(); throw; }
                finally { Native.Release(volumePart); }
                return 0;
            });
        }
        public SidetoneState Read()
        {
            Check(); Native.Check(volume!.GetChannelCount(out var channels)); if (channels != 2) throw new InvalidDataException("B20 sidetone channels changed.");
            Native.Check(volume.GetLevel(0, out var left)); Native.Check(volume.GetLevel(1, out var right));
            Native.Check(volume.GetLevelRange(0, out var min, out var max, out var step));
            Native.Check(volume.GetLevelRange(1, out var min2, out var max2, out var step2));
            if (min != min2 || max != max2 || step != step2) throw new InvalidDataException("B20 sidetone channel ranges differ.");
            Native.Check(mute!.GetMute(out var muted)); return new(identity, min, max, step, new(left, right, muted));
        }
        public void WriteLevels(float leftDb, float rightDb) { Check(); Native.Check(volume!.SetLevelAllChannels([leftDb, rightDb], 2, IntPtr.Zero)); }
        public void WriteMute(bool muted) { Check(); Native.Check(mute!.SetMute(muted, IntPtr.Zero)); }
        private void Check() { if (Environment.CurrentManagedThreadId != threadId) throw new InvalidOperationException("Use sidetone handles on their owning thread."); ObjectDisposedException.ThrowIf(volume is null || mute is null, this); }
        public void Dispose() { Native.Release(mute); mute = null; Native.Release(volume); volume = null; Native.Release(topology); topology = null; }
    }
}

public static class B20SidetoneLayout
{
    public static AudioTopologyPart ValidateLayout(AudioTopologySnapshot snapshot)
    {
        var volume = snapshot.Parts.SingleOrDefault(p => p.LocalId == 0x20007) ?? throw new InvalidDataException("B20 sidetone volume is missing.");
        var mute = snapshot.Parts.SingleOrDefault(p => p.LocalId == 0x20006) ?? throw new InvalidDataException("B20 sidetone mute is missing.");
        var sum = snapshot.Parts.SingleOrDefault(p => p.LocalId == 0x20005) ?? throw new InvalidDataException("B20 sidetone mix is missing.");
        if (!snapshot.DeviceId.Contains("vid_1395&pid_009f&mi_00", StringComparison.OrdinalIgnoreCase) ||
            !volume.GlobalId.EndsWith("epostopology/00020007", StringComparison.OrdinalIgnoreCase) ||
            volume.Subtype != new Guid("3A5ACC00-C557-11D0-8A2B-00A0C9255AC1") ||
            mute.Subtype != new Guid("02B223C0-C557-11D0-8A2B-00A0C9255AC1") ||
            sum.Subtype != new Guid("DA441A60-C556-11D0-8A2B-00A0C9255AC1") ||
            !volume.Incoming.SequenceEqual(new uint[] { 0x20006 }) || !volume.Outgoing.SequenceEqual(new uint[] { 0x20005 }) ||
            !mute.Incoming.SequenceEqual(new uint[] { 0x10000 }) || !mute.Outgoing.SequenceEqual(new uint[] { 0x20007 }) ||
            !sum.Incoming.Order().SequenceEqual(new uint[] { 0x10002, 0x20007 }) || !sum.Outgoing.SequenceEqual(new uint[] { 0x20003 }) ||
            mute.Muted is null || volume.Levels is not { Count: 2 } ||
            volume.Levels.Any(l => l.Minimum != -34.5f || l.Maximum != 9 || l.Step != 1.5f))
            throw new InvalidDataException("Unrecognized B20 sidetone topology; controls are unavailable.");
        return volume;
    }
}
