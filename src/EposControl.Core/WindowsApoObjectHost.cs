using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace EposControl.Core;

// All methods, including Dispose, are used on the host's one control thread.
[SupportedOSPlatform("windows")]
public sealed class WindowsApoObjectHost : IDisposable
{
    // This is the installed service's descriptor, not a default/world-access ACL.
    internal const string SecurityDescriptor = "D:(A;OICI;FA;;;BU)(A;OICI;FA;;;LS)";
    private SafeWaitHandle? mutex, changed;
    private SafeMemoryMappedFileHandle? map;
    private IntPtr view;
    private bool owned, disposed, validatePlayback;
    public bool CreatedFresh { get; private set; }
    internal static void RequireSuiteStopped() => Native.RequireSuiteStopped();
    internal static void RequireAudioStopped() => Native.RequireServiceStopped("Audiosrv", false);
    internal static uint AudioServiceState => Native.ServiceState("Audiosrv", false);

    public static WindowsApoObjectHost StartFreshB20(AudioEndpoint endpoint, IAudioBackend audio, byte[] diagnosticSeed)
    {
        WindowsApoMemory.ValidateIdentity(endpoint, audio.Discover());
        var name = ApoSharedObjects.B20Name(endpoint.Usb!);
        Native.RequireSuiteStopped();
        ArgumentNullException.ThrowIfNull(diagnosticSeed);
        return Open(name, true, diagnosticSeed);
    }

    public static WindowsApoObjectHost StartFreshGsx(AudioEndpoint microphone, AudioEndpoint playback,
        IAudioBackend audio, GsxApoStartupState startup)
    {
        ArgumentNullException.ThrowIfNull(startup);
        startup.Validate(microphone, playback);
        var discovered = audio.Discover();
        WindowsApoMemory.ValidateIdentity(microphone, discovered);
        WindowsApoMemory.ValidatePlaybackIdentity(playback, discovered);
        Native.RequireSuiteStopped();
        if (audio is CoreAudioBackend { UsesPausedGsxDiscovery: true }) RequireAudioStopped();
        return Open(WindowsApoMemory.MicrophoneObjectName(microphone), true, startup.DiagnosticSeed, true);
    }

    internal static WindowsApoObjectHost OpenPrivate(string name, bool requireFresh = false, byte[]? diagnosticSeed = null,
        bool validatePlayback = false)
    {
        if (!Regex.IsMatch(name, @"\ALocal\\EposControl\.Tests\.[a-f0-9]{32}\z"))
            throw new ArgumentException("Isolated initialization requires a private test namespace.");
        return Open(name, requireFresh, diagnosticSeed, validatePlayback);
    }

    private static WindowsApoObjectHost Open(string name, bool requireFresh, byte[]? diagnosticSeed, bool validatePlayback = false)
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("The experimental initializer requires Windows x64.");
        // Diagnostic replay is allowed only into newly created memory. It preserves
        // unowned configuration during the live pilot; it is not a persistence format.
        var seed = diagnosticSeed?.ToArray();
        if (seed is not null) ApoMicrophoneCodec.Read(seed);
        if (validatePlayback) {
            if (seed is null) throw new InvalidDataException("GSX initialization requires a diagnostic snapshot.");
            ApoPlaybackCodec.Read(seed);
            if (seed.AsSpan(ApoInitialState.StructureSize).ContainsAnyExcept((byte)0))
                throw new InvalidDataException("Unknown GSX diagnostic padding.");
        }
        var host = new WindowsApoObjectHost { validatePlayback = validatePlayback };
        IntPtr descriptor = IntPtr.Zero;
        try {
            if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptorW(SecurityDescriptor, 1, out descriptor, out _))
                throw Native.Error("Parse EPOS object security descriptor");
            var security = new Native.SecurityAttributes { Length = (uint)Marshal.SizeOf<Native.SecurityAttributes>(), Descriptor = descriptor };
            host.mutex = Native.CreateMutexExW(ref security, name + "_mutex", 1, 0x1F0001);
            var mutexError = Marshal.GetLastPInvokeError();
            if (host.mutex.IsInvalid) throw Native.Error("Create/open effects mutex", mutexError);
            var freshMutex = mutexError != 183;
            host.owned = freshMutex;
            if (!freshMutex) host.Enter();
            if (requireFresh && !freshMutex) throw new InvalidOperationException("Existing effects objects are still present; fresh initialization was not attempted.");

            if (freshMutex) {
                using var priorMap = Native.OpenFileMappingW(4, false, name + "_memory");
                var mapError = Marshal.GetLastPInvokeError();
                if (!priorMap.IsInvalid) throw new InvalidOperationException("Partial effects objects already exist; existing memory was preserved.");
                if (mapError != 2) throw Native.Error("Check for existing effects memory", mapError);
                using var priorEvent = Native.OpenEventW(0x100000, false, name + "_event");
                var eventError = Marshal.GetLastPInvokeError();
                if (!priorEvent.IsInvalid) throw new InvalidOperationException("Partial effects objects already exist; existing event was preserved.");
                if (eventError != 2) throw Native.Error("Check for existing effects event", eventError);

                host.map = Native.CreateFileMappingW(new IntPtr(-1), ref security, 0x08000004, 0, ApoInitialState.StructureSize, name + "_memory");
                mapError = Marshal.GetLastPInvokeError();
                if (host.map.IsInvalid) throw Native.Error("Create effects memory", mapError);
                if (mapError == 183) throw new InvalidOperationException("Effects memory appeared during startup; it was not initialized.");
                host.changed = Native.CreateEventExW(ref security, name + "_event", 1, 0x1F0003);
                eventError = Marshal.GetLastPInvokeError();
                if (host.changed.IsInvalid) throw Native.Error("Create effects event", eventError);
                if (eventError == 183) throw new InvalidOperationException("Effects event appeared during startup; existing state was preserved.");
            } else {
                // Do not create or repair missing members of an existing object set.
                host.map = Native.OpenFileMappingW(6, false, name + "_memory");
                if (host.map.IsInvalid) throw Native.Error("Open existing effects memory; partial sets cannot be repaired");
                host.changed = Native.OpenEventW(0x100000, false, name + "_event");
                if (host.changed.IsInvalid) throw Native.Error("Open existing effects event; partial sets cannot be repaired");
            }
            host.view = Native.MapViewOfFile(host.map, 6, 0, 0, UIntPtr.Zero);
            if (host.view == IntPtr.Zero) throw Native.Error("Map effects memory");
            if (Native.VirtualQuery(host.view, out var region, (UIntPtr)Marshal.SizeOf<Native.MemoryBasicInformation>()) == UIntPtr.Zero)
                throw Native.Error("Query effects view capacity");
            if (region.RegionSize.ToUInt64() != ApoMicrophoneCodec.MemorySize || region.State != 0x1000 || region.Type != 0x40000)
                throw new InvalidDataException("Unknown EPOS effects memory size or mapping type.");
            if (freshMutex) {
                var initial = ApoInitialState.Create();
                Marshal.Copy(initial, 0, host.view, initial.Length);
                if (seed is not null) Marshal.Copy(seed, 0, host.view, ApoInitialState.StructureSize);
                host.CreatedFresh = true;
            }
            host.ReadLocked();
            if (freshMutex && seed is not null && !Native.SetEvent(host.changed!)) throw Native.Error("Notify initial diagnostic state");
            host.Leave();
            return host;
        } catch { host.Dispose(); throw; }
        finally { if (descriptor != IntPtr.Zero) Native.LocalFree(descriptor); }
    }

    private WindowsApoObjectHost() { }
    public MicrophoneEffects Read()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Enter();
        try { return ReadLocked(); } finally { Leave(); }
    }
    private MicrophoneEffects ReadLocked()
    {
        return ApoMicrophoneCodec.Read(ReadMemoryLocked());
    }
    public PlaybackEffects ReadPlayback()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!validatePlayback) throw new InvalidOperationException("This host does not validate GSX playback state.");
        Enter();
        try { return ApoPlaybackCodec.Read(ReadMemoryLocked()); } finally { Leave(); }
    }
    private byte[] ReadMemoryLocked()
    {
        var memory = new byte[ApoMicrophoneCodec.MemorySize];
        Marshal.Copy(view, memory, 0, memory.Length);
        ApoMicrophoneCodec.Read(memory);
        if (validatePlayback) ApoPlaybackCodec.Read(memory);
        return memory;
    }
    private void Enter()
    {
        var result = Native.WaitForSingleObject(mutex!, 1000);
        if (result == 0) { owned = true; return; }
        if (result == 0x80) {
            owned = true; Leave();
            throw new IOException("EPOS effects mutex was abandoned. Restart the Suite before initializing effects.");
        }
        if (result == 0x102) throw new TimeoutException("EPOS effects are busy. Try again.");
        throw Native.Error("Wait for effects mutex");
    }
    private void Leave()
    {
        if (!owned) return;
        if (!Native.ReleaseMutex(mutex!)) throw Native.Error("Release effects mutex");
        owned = false;
    }
    public void Dispose()
    {
        if (disposed) return;
        try { Leave(); }
        finally {
            disposed = true;
            if (view != IntPtr.Zero) { Native.UnmapViewOfFile(view); view = IntPtr.Zero; }
            changed?.Dispose(); map?.Dispose(); mutex?.Dispose();
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes { internal uint Length; internal IntPtr Descriptor; internal int InheritHandle; }
        [StructLayout(LayoutKind.Sequential)] internal struct MemoryBasicInformation {
            internal IntPtr BaseAddress, AllocationBase;
            internal uint AllocationProtect;
            internal UIntPtr RegionSize;
            internal uint State, Protect, Type;
        }
        [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { internal uint Type, State, Controls, ExitCode, SpecificExitCode, CheckPoint, WaitHint; }
        internal static Exception Error(string operation, int? code = null) => new Win32Exception(code ?? Marshal.GetLastPInvokeError(), operation);
        internal static void RequireSuiteStopped()
            => RequireServiceStopped("EPOSGamingSuiteService", true);
        internal static void RequireServiceStopped(string name, bool allowMissing)
        {
            if (ServiceState(name, allowMissing) != 1)
                throw new InvalidOperationException("Service " + name + " must be stopped before explicit fresh initialization.");
        }
        internal static uint ServiceState(string name, bool allowMissing)
        {
            var manager = OpenSCManagerW(null, null, 1);
            if (manager == IntPtr.Zero) throw Error("Open service manager");
            try {
                var service = OpenServiceW(manager, name, 4);
                if (service == IntPtr.Zero) {
                    var code = Marshal.GetLastPInvokeError();
                    if (code == 1060 && allowMissing) return 1;
                    throw Error("Check EPOS service status", code);
                }
                try {
                    if (!QueryServiceStatus(service, out var status)) throw Error("Read EPOS service status");
                    return status.State;
                } finally { CloseServiceHandle(service); }
            } finally { CloseServiceHandle(manager); }
        }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out IntPtr descriptor, out uint size);
        [DllImport("kernel32.dll")] internal static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeWaitHandle CreateMutexExW(ref SecurityAttributes attributes, string name, uint flags, uint access);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeMemoryMappedFileHandle CreateFileMappingW(IntPtr file, ref SecurityAttributes attributes, uint protection, uint high, uint low, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeWaitHandle CreateEventExW(ref SecurityAttributes attributes, string name, uint flags, uint access);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeMemoryMappedFileHandle OpenFileMappingW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeWaitHandle OpenEventW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr MapViewOfFile(SafeMemoryMappedFileHandle map, uint access, uint high, uint low, UIntPtr count);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern UIntPtr VirtualQuery(IntPtr address, out MemoryBasicInformation region, UIntPtr length);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ReleaseMutex(SafeWaitHandle mutex);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetEvent(SafeWaitHandle changed);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnmapViewOfFile(IntPtr address);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
        [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
    }
}
