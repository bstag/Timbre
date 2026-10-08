using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;

namespace Timbre.Core;

public static class WindowsApoMemory
{
    [SupportedOSPlatform("windows")]
    public static IMicrophoneEffectsBackend Create(IAudioBackend audio) => new ApoMicrophoneEffectsBackend(endpoint => {
        ValidateIdentity(endpoint, audio.Discover());
        return new Session(MicrophoneObjectName(endpoint), (offset, length) => ValidateMicrophonePatch(endpoint, offset, length));
    });

    public static string MicrophoneObjectName(AudioEndpoint endpoint)
    {
        DemoMicrophoneEffectsBackend.ValidateEndpoint(endpoint);
        return "Global\\CF4B411F-BE2B-4D84-8106-EC27CA0F8F05_1395_" + endpoint.Usb!.ProductId.ToLowerInvariant();
    }

    public static void ValidateMicrophonePatch(AudioEndpoint endpoint, int offset, int length)
    {
        DemoMicrophoneEffectsBackend.ValidateEndpoint(endpoint);
        ApoMicrophoneCodec.ValidatePatch(offset, length);
        if (endpoint.Usb!.ProductId.Equals("0098", StringComparison.OrdinalIgnoreCase) && offset == ApoMicrophoneCodec.HighPassEnabledOffset)
            throw new InvalidOperationException("GSX 300 high-pass writes are not validated.");
    }

    [SupportedOSPlatform("windows")]
    public static IPlaybackEffectsBackend CreatePlayback(IAudioBackend audio) => new ApoPlaybackEffectsBackend(endpoint => {
        ValidatePlaybackIdentity(endpoint, audio.Discover());
        return new Session("Global\\CF4B411F-BE2B-4D84-8106-EC27CA0F8F05_1395_0098", ApoPlaybackCodec.ValidatePatch);
    });

    [SupportedOSPlatform("windows")]
    public static IGsxProcessingBackend CreateGsxProcessing(IAudioBackend audio) => new ApoGsxProcessingBackend((microphone, playback) => {
        GsxApoStartupState.ValidateEndpoints(microphone, playback);
        var discovered = audio.Discover();
        ValidateIdentity(microphone, discovered); ValidatePlaybackIdentity(playback, discovered);
        return new Session(MicrophoneObjectName(microphone), (offset, length) => ValidateGsxPatch(microphone, offset, length));
    });

    internal static void ValidateGsxPatch(AudioEndpoint microphone, int offset, int length)
    {
        // Playback and microphone fields are disjoint in the recovered structure.
        if (offset is ApoPlaybackCodec.SurroundOffset or ApoPlaybackCodec.ReverbEnabledOffset or ApoPlaybackCodec.ReverbLevelOffset ||
            offset >= ApoPlaybackCodec.EqualizerOffset && offset < ApoPlaybackCodec.EqualizerOffset + 36)
            ApoPlaybackCodec.ValidatePatch(offset, length);
        else ValidateMicrophonePatch(microphone, offset, length);
    }

    public static void ValidatePlaybackIdentity(AudioEndpoint endpoint, IReadOnlyList<AudioEndpoint> discovered)
    {
        ApoPlaybackCodec.ValidateEndpoint(endpoint);
        ValidateConnectedIdentity(endpoint, discovered);
    }

    public static void ValidateIdentity(AudioEndpoint endpoint, IReadOnlyList<AudioEndpoint> discovered)
    {
        DemoMicrophoneEffectsBackend.ValidateEndpoint(endpoint);
        ValidateConnectedIdentity(endpoint, discovered);
    }
    private static void ValidateConnectedIdentity(AudioEndpoint endpoint, IReadOnlyList<AudioEndpoint> discovered)
    {
        if (!discovered.Any(e => e.Id == endpoint.Id && e.ProfileIdentity == endpoint.ProfileIdentity && e.Direction == endpoint.Direction))
            throw new InvalidOperationException("The selected audio endpoint is disconnected or its identity changed.");
        var usb = endpoint.Usb!;
        // Vendor shared objects use VID/PID without a serial. Refuse ambiguity.
        var instances = discovered.Where(e => e.Usb is { } u && u.VendorId.Equals(usb.VendorId, StringComparison.OrdinalIgnoreCase)
            && u.ProductId.Equals(usb.ProductId, StringComparison.OrdinalIgnoreCase)).Select(e => e.Usb!.InstanceId).Distinct(StringComparer.OrdinalIgnoreCase);
        if (instances.Count() != 1) throw new InvalidOperationException("EPOS shared effects cannot isolate multiple connected devices of this model.");
    }

    [SupportedOSPlatform("windows")]
    internal sealed class Session : IEffectMemorySession
    {
        private readonly Mutex mutex;
        private MemoryMappedFile? map;
        private MemoryMappedViewAccessor? view;
        private EventWaitHandle? changed;
        private bool owned;
        private readonly Action<int, int> validatePatch;
        public Session(UsbIdentity usb) : this(ApoSharedObjects.B20Name(usb)) { }
        internal Session(string name) : this(name, ApoMicrophoneCodec.ValidatePatch) { }
        internal Session(string name, Action<int, int> validatePatch)
        {
            this.validatePatch = validatePatch;
            mutex = Mutex.OpenExisting(name + "_mutex");
            try {
                ApoSharedObjects.Enter(mutex); owned = true;
                map = MemoryMappedFile.OpenExisting(name + "_memory", MemoryMappedFileRights.ReadWrite);
                view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
                if (view.Capacity != ApoMicrophoneCodec.MemorySize) throw new InvalidDataException("Unknown EPOS effects memory size.");
                changed = EventWaitHandle.OpenExisting(name + "_event");
            } catch { Dispose(); throw; }
        }
        public byte[] Read()
        {
            var bytes = new byte[ApoMicrophoneCodec.MemorySize];
            if (view!.ReadArray(0, bytes, 0, bytes.Length) != bytes.Length) throw new IOException("Incomplete EPOS effects snapshot.");
            return bytes;
        }
        public void Write(int offset, byte[] data)
        {
            validatePatch(offset, data.Length);
            view!.WriteArray(offset, data, 0, data.Length);
        }
        public void Notify() { if (!changed!.Set()) throw new IOException("EPOS effects notification failed."); }
        public void Dispose()
        {
            changed?.Dispose(); view?.Dispose(); map?.Dispose();
            if (owned) { mutex.ReleaseMutex(); owned = false; }
            mutex.Dispose();
        }
    }
}
