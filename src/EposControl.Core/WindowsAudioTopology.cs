using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Native = EposControl.Core.CoreAudioBackend.Native;

namespace EposControl.Core;

public sealed record TopologyLevel(float Decibels, float Minimum, float Maximum, float Step);
public sealed record AudioTopologyPart(uint LocalId, string GlobalId, string Name, Guid Subtype,
    IReadOnlyList<uint> Incoming, IReadOnlyList<uint> Outgoing, IReadOnlyList<TopologyLevel>? Levels, bool? Muted);
public sealed record AudioTopologySnapshot(string EndpointId, string DeviceId, IReadOnlyList<AudioTopologyPart> Parts);

[SupportedOSPlatform("windows")]
public static class WindowsAudioTopology
{
    internal static readonly Guid VolumeIid = new("7FB7B48F-531D-44A2-BCB3-5AD5A134B3DC");
    private static readonly Guid MuteIid = new("DF45AEEA-B74A-4B6B-AFAD-2366B6AA012E");
    public static AudioTopologySnapshot Read(AudioEndpoint endpoint) => WithTopology(endpoint, topology => {
        Native.Check(topology.GetDeviceId(out var deviceId)); Native.Check(topology.GetSubunitCount(out var count));
        if (count > 256) throw new InvalidDataException("Unexpected number of audio subunits.");
        var parts = new List<AudioTopologyPart>();
        for (uint index = 0; index < count; index++) {
            Native.Check(topology.GetSubunit(index, out var obj));
            try { parts.Add(ReadPart((Part)obj)); } finally { Native.Release(obj); }
        }
        return new AudioTopologySnapshot(endpoint.Id, deviceId, parts);
    });
    internal static T WithTopology<T>(AudioEndpoint endpoint, Func<Topology, T> action)
    {
        var enumerator = Native.CreateEnumerator();
        try {
            Native.Check(enumerator.GetDevice(endpoint.Id, out var device));
            try {
                Native.Check(device.GetState(out var state));
                if ((state & 1) == 0) throw new InvalidOperationException("The audio endpoint is disconnected.");
                var iid = typeof(Topology).GUID; Native.Check(device.Activate(ref iid, 23, IntPtr.Zero, out var obj));
                try {
                    var endpointTopology = (Topology)obj;
                    Native.Check(endpointTopology.GetConnector(0, out var connector));
                    try {
                        Native.Check(connector.GetConnectedTo(out var hardwareConnector));
                        try {
                            Native.Check(((Part)hardwareConnector).GetTopologyObject(out var hardware));
                            try { return action(hardware); } finally { Native.Release(hardware); }
                        } finally { Native.Release(hardwareConnector); }
                    } finally { Native.Release(connector); }
                } finally { Native.Release(obj); }
            } finally { Native.Release(device); }
        } finally { Native.Release(enumerator); }
    }
    private static AudioTopologyPart ReadPart(Part part)
    {
        Native.Check(part.GetLocalId(out var id)); Native.Check(part.GetGlobalId(out var global)); Native.Check(part.GetName(out var name));
        Native.Check(part.GetSubType(out var subtype));
        var incoming = Links(part, true); var outgoing = Links(part, false);
        List<TopologyLevel>? levels = null; bool? muted = null;
        var iid = VolumeIid;
        if (part.Activate(23, ref iid, out var obj) >= 0) {
            try {
                var volume = (Volume)obj; Native.Check(volume.GetChannelCount(out var channels));
                if (channels > 32) throw new InvalidDataException("Unexpected number of topology channels.");
                levels = [];
                for (uint channel = 0; channel < channels; channel++) {
                    Native.Check(volume.GetLevel(channel, out var db)); Native.Check(volume.GetLevelRange(channel, out var min, out var max, out var step));
                    levels.Add(new(db, min, max, step));
                }
            } finally { Native.Release(obj); }
        }
        iid = MuteIid;
        if (part.Activate(23, ref iid, out obj) >= 0) {
            try { Native.Check(((Mute)obj).GetMute(out var value)); muted = value; } finally { Native.Release(obj); }
        }
        return new(id, global, name, subtype, incoming, outgoing, levels, muted);
    }
    private static IReadOnlyList<uint> Links(Part part, bool incoming)
    {
        var hr = incoming ? part.EnumPartsIncoming(out var list) : part.EnumPartsOutgoing(out list);
        if (hr == unchecked((int)0x80070490)) return []; // No adjacent parts.
        Native.Check(hr);
        try {
            Native.Check(list.GetCount(out var count)); if (count > 256) throw new InvalidDataException("Unexpected topology links.");
            var result = new List<uint>();
            for (uint i = 0; i < count; i++) {
                Native.Check(list.GetPart(i, out var linked));
                try { Native.Check(linked.GetLocalId(out var id)); result.Add(id); } finally { Native.Release(linked); }
            }
            return result;
        } finally { Native.Release(list); }
    }
    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface Topology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, out Connector connector);
        [PreserveSig] int GetSubunitCount(out uint count);
        [PreserveSig] int GetSubunit(uint index, [MarshalAs(UnmanagedType.IUnknown)] out object subunit);
        [PreserveSig] int GetPartById(uint id, out Part part);
        [PreserveSig] int GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }
    [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface Connector
    {
        [PreserveSig] int GetType(out int type); [PreserveSig] int GetDataFlow(out int flow);
        [PreserveSig] int ConnectTo(Connector connector); [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
        [PreserveSig] int GetConnectedTo(out Connector connector);
    }
    [ComImport, Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface Part
    {
        [PreserveSig] int GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int GetLocalId(out uint id);
        [PreserveSig] int GetGlobalId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetPartType(out int type); [PreserveSig] int GetSubType(out Guid subtype);
        [PreserveSig] int GetControlInterfaceCount(out uint count);
        [PreserveSig] int GetControlInterface(uint index, out IntPtr descriptor);
        [PreserveSig] int EnumPartsIncoming(out PartsList parts); [PreserveSig] int EnumPartsOutgoing(out PartsList parts);
        [PreserveSig] int GetTopologyObject(out Topology topology);
        [PreserveSig] int Activate(uint context, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
    }
    [ComImport, Guid("6DAA848C-5EB0-45CC-AEA5-998A2CDA1FFB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface PartsList
    {
        [PreserveSig] int GetCount(out uint count); [PreserveSig] int GetPart(uint index, out Part part);
    }
    [ComImport, Guid("7FB7B48F-531D-44A2-BCB3-5AD5A134B3DC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface Volume
    {
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int GetLevelRange(uint channel, out float min, out float max, out float step);
        [PreserveSig] int GetLevel(uint channel, out float level);
        [PreserveSig] int SetLevel(uint channel, float level, IntPtr context);
        [PreserveSig] int SetLevelUniform(float level, IntPtr context);
        [PreserveSig] int SetLevelAllChannels([MarshalAs(UnmanagedType.LPArray)] float[] levels, uint channels, IntPtr context);
    }
    [ComImport, Guid("DF45AEEA-B74A-4B6B-AFAD-2366B6AA012E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface Mute
    {
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, IntPtr context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }
}
