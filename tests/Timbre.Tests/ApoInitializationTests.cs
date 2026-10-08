using System.ComponentModel;
using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text.Json;
using Timbre.Core;

internal static class ApoInitializationTests
{
    private static byte[] Golden() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "apo-cold-initializer-009f.bin"));
    public static void Run(TestSuite suite)
    {
        suite.Case("Cold initializer matches independently recovered native instructions", () => {
            var bytes = ApoInitialState.Create(); var golden = Golden();
            TestSuite.Assert(bytes.Length == 2112 && bytes.AsSpan().SequenceEqual(golden.AsSpan(0, 2112)));
            using var evidence = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "apo-cold-initializer-009f.json")));
            TestSuite.Assert(evidence.RootElement.GetProperty("Writes").GetArrayLength() == 60);
            TestSuite.Assert(evidence.RootElement.GetProperty("SourceSha256").GetString() == "350D5EC4DEA1F6440C83DA82A0155AF1D7ACA02250E3F31EB7E3EBF472B8DD44");
        });
        if (OperatingSystem.IsWindows()) RunWindows(suite);
    }

    [SupportedOSPlatform("windows")]
    private static void RunWindows(TestSuite suite)
    {
        suite.Case("Private initializer rejects real and unscoped object names", () => {
            foreach (var name in new[] { "Global\\CF4B411F-BE2B-4D84-8106-EC27CA0F8F05_1395_009f", "unscoped", "Local\\other" })
                TestSuite.Throws<ArgumentException>(() => WindowsApoObjectHost.OpenPrivate(name));
        });
        suite.Case("Fresh native object set matches every golden byte and releases on shutdown", () => {
            var name = NewName(); using var host = WindowsApoObjectHost.OpenPrivate(name, true);
            TestSuite.Assert(host.CreatedFresh);
            using (var session = new WindowsApoMemory.Session(name)) TestSuite.Assert(session.Read().SequenceEqual(Golden()));
            host.Dispose(); AssertMissing(name);
        });
        suite.Case("Fresh native notification event is manual reset and initially clear", () => {
            var name = NewName(); using var host = WindowsApoObjectHost.OpenPrivate(name, true);
            using var changed = EventWaitHandle.OpenExisting(name + "_event");
            TestSuite.Assert(!changed.WaitOne(0)); changed.Set();
            TestSuite.Assert(changed.WaitOne(0) && changed.WaitOne(0));
        });
        suite.Case("Native objects expose the recovered users/service ACL without world access", () => {
            var name = NewName(); using var host = WindowsApoObjectHost.OpenPrivate(name, true);
            using var mutex = Mutex.OpenExisting(name + "_mutex");
            using var changed = EventWaitHandle.OpenExisting(name + "_event");
            using var map = MemoryMappedFile.OpenExisting(name + "_memory", MemoryMappedFileRights.ReadWrite | MemoryMappedFileRights.ReadPermissions);
            foreach (var (handle, access) in new (SafeHandle, int)[] { (mutex.SafeWaitHandle, 0x1F0001), (changed.SafeWaitHandle, 0x1F0003), (map.SafeMemoryMappedFileHandle, 0xF001F) }) {
                var bytes = new byte[1024];
                if (!GetKernelObjectSecurity(handle, 4, bytes, (uint)bytes.Length, out var length)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                TestSuite.Assert(length <= bytes.Length);
                var descriptor = new RawSecurityDescriptor(bytes, 0);
                var acl = descriptor.DiscretionaryAcl!;
                TestSuite.Assert(acl.Count == 2, "Unexpected ACL: " + descriptor.GetSddlForm(AccessControlSections.Access));
                var sids = acl.Cast<CommonAce>().Select(ace => ace.SecurityIdentifier.Value).Order().ToArray();
                TestSuite.Assert(sids.SequenceEqual(new[] { "S-1-5-19", "S-1-5-32-545" }), "Unexpected trustees: " + descriptor.GetSddlForm(AccessControlSections.Access));
                // Windows limits the legacy FA mask to the rights of each object type.
                TestSuite.Assert(acl.Cast<CommonAce>().All(ace => ace.AceQualifier == AceQualifier.AccessAllowed && ace.AccessMask == access), "Unexpected access: " + descriptor.GetSddlForm(AccessControlSections.Access));
            }
        });
        suite.Case("Existing compatible state, reserved bytes and signalled event are preserved", () => {
            using var rig = new Rig(7); rig.Changed!.Set(); var before = rig.Bytes();
            using var host = WindowsApoObjectHost.OpenPrivate(rig.Name);
            TestSuite.Assert(!host.CreatedFresh && rig.Bytes().SequenceEqual(before) && rig.Changed.WaitOne(0));
        });
        suite.Case("Fresh-only startup refuses complete existing objects without writes", () => {
            using var rig = new Rig(7); var before = rig.Bytes();
            TestSuite.Throws<InvalidOperationException>(() => WindowsApoObjectHost.OpenPrivate(rig.Name, true));
            TestSuite.Assert(rig.Bytes().SequenceEqual(before));
        });
        foreach (var mask in new[] { 1, 2, 3, 4, 5, 6 }) suite.Case($"Partial object set {mask} is rejected without repair or state changes", () => {
            using var rig = new Rig(mask); var before = rig.View is null ? null : rig.Bytes();
            if (rig.Changed is not null) rig.Changed.Set();
            if ((mask & 1) == 0) TestSuite.Throws<InvalidOperationException>(() => WindowsApoObjectHost.OpenPrivate(rig.Name));
            else TestSuite.Throws<Win32Exception>(() => WindowsApoObjectHost.OpenPrivate(rig.Name));
            if (before is not null) TestSuite.Assert(rig.Bytes().SequenceEqual(before));
            if (rig.Changed is not null) TestSuite.Assert(rig.Changed.WaitOne(0));
            rig.Dispose(); AssertMissing(rig.Name);
        });
        suite.Case("Corrupt reused memory rejects without resetting it or leaking ownership", () => {
            using var rig = new Rig(7); rig.View!.Write(0, 99); var before = rig.Bytes();
            TestSuite.Throws<InvalidDataException>(() => WindowsApoObjectHost.OpenPrivate(rig.Name));
            TestSuite.Assert(rig.Bytes().SequenceEqual(before)); rig.Dispose(); AssertMissing(rig.Name);
        });
        suite.Case("Unknown existing mapping capacity rejects before any initialization", () => {
            using var rig = new Rig(7, 8192); var before = rig.Bytes();
            TestSuite.Throws<InvalidDataException>(() => WindowsApoObjectHost.OpenPrivate(rig.Name));
            TestSuite.Assert(rig.Bytes().SequenceEqual(before)); rig.Dispose(); AssertMissing(rig.Name);
        });
        suite.Case("Kernel object type collision leaves the preexisting object untouched", () => {
            var name = NewName(); using var collision = new EventWaitHandle(true, EventResetMode.ManualReset, name + "_memory");
            TestSuite.Throws<Win32Exception>(() => WindowsApoObjectHost.OpenPrivate(name));
            TestSuite.Assert(collision.WaitOne(0));
            TestSuite.Throws<WaitHandleCannotBeOpenedException>(() => { using var mutex = Mutex.OpenExisting(name + "_mutex"); });
        });
        suite.Case("Native initializer allows real transport apply/restore and preserves all unowned bytes", () => {
            var name = NewName(); using var host = WindowsApoObjectHost.OpenPrivate(name, true);
            var mic = new DemoAudioBackend().Discover().First();
            var backend = new ApoMicrophoneEffectsBackend(_ => new WindowsApoMemory.Session(name));
            var before = backend.Read(mic);
            var desired = new MicrophoneEffects(50, 2, new(true, true, true, true, MicrophoneEqPresets.Warm));
            backend.Apply(mic, before, desired); TestSuite.Assert(ApoMicrophoneCodec.Matches(host.Read(), desired));
            backend.Apply(mic, desired, before);
            using var session = new WindowsApoMemory.Session(name); TestSuite.Assert(session.Read().SequenceEqual(Golden()));
        });
        suite.Case("Diagnostic replay preserves unmapped configuration only in fresh objects", () => {
            var name = NewName(); var seed = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "apo-memory-009f.bin"));
            seed[58] = 77; var expected = seed.ToArray();
            using var host = WindowsApoObjectHost.OpenPrivate(name, true, seed);
            seed[58] = 99;
            using var session = new WindowsApoMemory.Session(name);
            TestSuite.Assert(host.CreatedFresh && session.Read().SequenceEqual(expected));
            using var changed = EventWaitHandle.OpenExisting(name + "_event"); TestSuite.Assert(changed.WaitOne(0));
        });
        suite.Case("Diagnostic replay never overwrites a reused compatible object set", () => {
            using var rig = new Rig(7); var before = rig.Bytes();
            using var host = WindowsApoObjectHost.OpenPrivate(rig.Name, diagnosticSeed: Golden());
            TestSuite.Assert(!host.CreatedFresh && rig.Bytes().SequenceEqual(before) && !rig.Changed!.WaitOne(0));
        });
        suite.Case("Invalid diagnostic replay rejects before creating named objects", () => {
            var name = NewName(); TestSuite.Throws<InvalidDataException>(() => WindowsApoObjectHost.OpenPrivate(name, true, new byte[4095]));
            AssertMissing(name);
        });
        suite.Case("Native host releases its lock so a peer thread can reuse the complete set", () => {
            var name = NewName(); using var first = WindowsApoObjectHost.OpenPrivate(name, true); Exception? error = null;
            var peer = new Thread(() => { try { using var second = WindowsApoObjectHost.OpenPrivate(name); TestSuite.Assert(!second.CreatedFresh); second.Read(); } catch (Exception ex) { error = ex; } });
            peer.Start(); TestSuite.Assert(peer.Join(3000)); if (error is not null) throw error;
        });
        suite.Case("Native host closes independently while a retained reader keeps objects alive", () => {
            var name = NewName(); using var host = WindowsApoObjectHost.OpenPrivate(name, true);
            using var lease = WindowsApoObjectLease.OpenExisting(name);
            host.Dispose(); lease.Read(); lease.Dispose(); AssertMissing(name);
        });
        suite.Case("Native host disposal is idempotent and disposed reads reject", () => {
            using var host = WindowsApoObjectHost.OpenPrivate(NewName(), true);
            host.Dispose(); host.Dispose(); TestSuite.Throws<ObjectDisposedException>(() => host.Read());
        });
        suite.Case("Native host times out on a busy existing mutex without damaging its state", () => {
            using var rig = new Rig(7); var before = rig.Bytes();
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var peer = new Thread(() => { rig.Mutex!.WaitOne(); entered.Set(); release.Wait(5000); rig.Mutex.ReleaseMutex(); }); peer.Start();
            try { TestSuite.Assert(entered.Wait(2000)); TestSuite.Throws<TimeoutException>(() => WindowsApoObjectHost.OpenPrivate(rig.Name)); }
            finally { release.Set(); TestSuite.Assert(peer.Join(5000)); }
            TestSuite.Assert(rig.Bytes().SequenceEqual(before));
        });
        suite.Case("Native host rejects an abandoned existing mutex and releases its ownership", () => {
            using var rig = new Rig(7); var before = rig.Bytes(); var peer = new Thread(() => rig.Mutex!.WaitOne());
            peer.Start(); TestSuite.Assert(peer.Join(2000));
            TestSuite.Throws<IOException>(() => WindowsApoObjectHost.OpenPrivate(rig.Name));
            using var reader = WindowsApoObjectLease.OpenExisting(rig.Name);
            TestSuite.Assert(rig.Bytes().SequenceEqual(before));
        });
    }
    private static string NewName() => "Local\\Timbre.Tests." + Guid.NewGuid().ToString("N");
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(SafeHandle handle, uint information, byte[] descriptor, uint length, out uint needed);
    [SupportedOSPlatform("windows")]
    private static void AssertMissing(string name)
    {
        TestSuite.Throws<WaitHandleCannotBeOpenedException>(() => { using var value = Mutex.OpenExisting(name + "_mutex"); });
        TestSuite.Throws<FileNotFoundException>(() => { using var value = MemoryMappedFile.OpenExisting(name + "_memory"); });
        TestSuite.Throws<WaitHandleCannotBeOpenedException>(() => { using var value = EventWaitHandle.OpenExisting(name + "_event"); });
    }
    [SupportedOSPlatform("windows")]
    private sealed class Rig : IDisposable
    {
        internal string Name { get; } = NewName();
        internal Mutex? Mutex; internal MemoryMappedFile? Map; internal MemoryMappedViewAccessor? View; internal EventWaitHandle? Changed;
        internal Rig(int mask, int size = 4096)
        {
            try {
                if ((mask & 1) != 0) Mutex = new(false, Name + "_mutex");
                if ((mask & 2) != 0) {
                    Map = MemoryMappedFile.CreateNew(Name + "_memory", size); View = Map.CreateViewAccessor(0, 0);
                    var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "apo-memory-009f.bin")); bytes[58] = 42;
                    View.WriteArray(0, bytes, 0, bytes.Length);
                }
                if ((mask & 4) != 0) Changed = new(false, EventResetMode.ManualReset, Name + "_event");
            } catch { Dispose(); throw; }
        }
        internal byte[] Bytes() { var bytes = new byte[(int)View!.Capacity]; View.ReadArray(0, bytes, 0, bytes.Length); return bytes; }
        public void Dispose() { Changed?.Dispose(); View?.Dispose(); Map?.Dispose(); Mutex?.Dispose(); Changed = null; View = null; Map = null; Mutex = null; }
    }
}
