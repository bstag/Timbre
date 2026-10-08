using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text.RegularExpressions;
using EposControl.Core;

internal static class ApoObjectLeaseTests
{
    public static void Run(TestSuite suite)
    {
        suite.Case("Retention names accept only the verified B20 VID/PID", () => {
            TestSuite.Assert(ApoSharedObjects.B20Name(new("1395", "009F", "unit")) == "Global\\CF4B411F-BE2B-4D84-8106-EC27CA0F8F05_1395_009f");
            TestSuite.Reject(() => ApoSharedObjects.B20Name(new("1395", "0098", "unit")));
            TestSuite.Reject(() => ApoSharedObjects.B20Name(new("1234", "009f", "unit")));
        });
        if (OperatingSystem.IsWindows()) RunWindows(suite);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RunWindows(TestSuite suite)
    {
        suite.Case("Retention keeps all three objects alive without changing memory or event", () => {
            using var rig = new Rig(); using var lease = WindowsApoObjectLease.OpenExisting(rig.Name);
            var original = rig.Bytes(); rig.Dispose();
            using var session = new WindowsApoMemory.Session(rig.Name);
            TestSuite.Assert(session.Read().SequenceEqual(original));
            using var changed = EventWaitHandle.OpenExisting(rig.Name + "_event");
            TestSuite.Assert(!changed.WaitOne(0));
        });
        suite.Case("Retention preserves an already signalled event", () => {
            using var rig = new Rig(); rig.Changed!.Set();
            using var lease = WindowsApoObjectLease.OpenExisting(rig.Name);
            TestSuite.Assert(rig.Changed.WaitOne(0));
        });
        suite.Case("Retention releases its mutex so another thread can apply effects", () => {
            using var rig = new Rig(); using var lease = WindowsApoObjectLease.OpenExisting(rig.Name);
            rig.Dispose(); Exception? failure = null;
            var peer = new Thread(() => {
                try {
                    var mic = new DemoAudioBackend().Discover().First();
                    var backend = new ApoMicrophoneEffectsBackend(_ => new WindowsApoMemory.Session(rig.Name));
                    backend.Apply(mic, backend.Read(mic), new(35, 1));
                } catch (Exception ex) { failure = ex; }
            });
            peer.Start(); TestSuite.Assert(peer.Join(5000), "Peer effects update did not finish.");
            if (failure is not null) throw failure;
            TestSuite.Assert(ApoMicrophoneCodec.Matches(lease.Read(), new(35, 1)));
            using var changed = EventWaitHandle.OpenExisting(rig.Name + "_event");
            TestSuite.Assert(changed.WaitOne(0), "Apply did not notify its consumer.");
        });
        suite.Case("Multiple leases survive closing one owner and vanish after the last closes", () => {
            using var rig = new Rig(); var first = WindowsApoObjectLease.OpenExisting(rig.Name);
            using var second = WindowsApoObjectLease.OpenExisting(rig.Name);
            rig.Dispose(); first.Dispose(); second.Read(); second.Dispose(); AssertMissing(rig.Name);
        });
        suite.Case("Lease disposal is idempotent and read after disposal rejects", () => {
            using var rig = new Rig(); using var lease = WindowsApoObjectLease.OpenExisting(rig.Name);
            lease.Dispose(); lease.Dispose(); TestSuite.Throws<ObjectDisposedException>(() => lease.Read());
        });
        suite.Case("Missing objects are not created by retention", () => {
            var name = NewName(); TestSuite.Throws<WaitHandleCannotBeOpenedException>(() => WindowsApoObjectLease.OpenExisting(name));
            AssertMissing(name);
        });
        suite.Case("Missing event fails startup and releases already opened objects", () => {
            using var rig = new Rig(); rig.Changed!.Dispose(); rig.Changed = null;
            TestSuite.Throws<WaitHandleCannotBeOpenedException>(() => WindowsApoObjectLease.OpenExisting(rig.Name));
            rig.Dispose(); AssertMissing(rig.Name);
        });
        suite.Case("Invalid memory header fails startup without modifying data or leaking handles", () => {
            using var rig = new Rig(); rig.View!.Write(0, 3); var before = rig.Bytes();
            TestSuite.Throws<InvalidDataException>(() => WindowsApoObjectLease.OpenExisting(rig.Name));
            TestSuite.Assert(rig.Bytes().SequenceEqual(before)); rig.Dispose(); AssertMissing(rig.Name);
        });
        suite.Case("Unknown mapping capacity fails startup without leaking handles", () => {
            using var rig = new Rig(8192);
            TestSuite.Throws<InvalidDataException>(() => WindowsApoObjectLease.OpenExisting(rig.Name));
            rig.Dispose(); AssertMissing(rig.Name);
        });
        suite.Case("Native 2112-byte mapping exposes a 4096-byte view", () => {
            using var rig = new Rig(2112);
            TestSuite.Assert(rig.View!.Capacity == 4096);
            using var lease = WindowsApoObjectLease.OpenExisting(rig.Name); lease.Read();
        });
        suite.Case("Busy mutex bounds retention startup and releases failed lease handles", () => {
            using var rig = new Rig(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var peer = new Thread(() => { rig.Mutex!.WaitOne(); entered.Set(); release.Wait(5000); rig.Mutex.ReleaseMutex(); });
            peer.Start();
            try {
                TestSuite.Assert(entered.Wait(2000)); var watch = Stopwatch.StartNew();
                TestSuite.Throws<TimeoutException>(() => WindowsApoObjectLease.OpenExisting(rig.Name));
                TestSuite.Assert(watch.Elapsed < TimeSpan.FromSeconds(4));
            } finally { release.Set(); peer.Join(5000); }
            using var lease = WindowsApoObjectLease.OpenExisting(rig.Name); lease.Read();
        });
        suite.Case("Abandoned mutex rejects retention and releases its acquired ownership", () => {
            using var rig = new Rig();
            var peer = new Thread(() => rig.Mutex!.WaitOne()); peer.Start(); TestSuite.Assert(peer.Join(2000));
            TestSuite.Throws<IOException>(() => WindowsApoObjectLease.OpenExisting(rig.Name));
            Exception? failure = null;
            var next = new Thread(() => { try { using var lease = WindowsApoObjectLease.OpenExisting(rig.Name); lease.Read(); } catch (Exception ex) { failure = ex; } });
            next.Start(); TestSuite.Assert(next.Join(3000)); if (failure is not null) throw failure;
        });
        suite.Case("Separate helper process retains objects after creator exits and releases on shutdown", () => {
            using var rig = new Rig();
            var folder = Path.Combine(Path.GetTempPath(), "EposControl.LeaseTests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder); var ready = Path.Combine(folder, "ready"); var stop = Path.Combine(folder, "stop");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(ApoObjectLeaseTests).Assembly.Location);
            foreach (var argument in new[] { "--apo-lease-probe", rig.Name, ready, stop }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            try {
                var watch = Stopwatch.StartNew();
                while (!File.Exists(ready) && !process.HasExited && watch.ElapsedMilliseconds < 5000) Thread.Sleep(25);
                TestSuite.Assert(File.Exists(ready) && !process.HasExited, "Private helper did not become ready.");
                rig.Dispose(); using (var session = new WindowsApoMemory.Session(rig.Name)) ApoMicrophoneCodec.Read(session.Read());
                File.WriteAllText(stop, "stop"); TestSuite.Assert(process.WaitForExit(5000) && process.ExitCode == 0);
                AssertMissing(rig.Name);
            } finally {
                if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); }
                // Only these two exact files and the verified test directory are removed.
                File.Delete(ready); File.Delete(stop); Directory.Delete(folder);
            }
        });
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static int Probe(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 4 ||
            !Regex.IsMatch(args[1], @"\ALocal\\EposControl\.Tests\.[a-f0-9]{32}\z")) return 1;
        try {
            using var lease = WindowsApoObjectLease.OpenExisting(args[1]); File.WriteAllText(args[2], "ready");
            var watch = Stopwatch.StartNew();
            while (!File.Exists(args[3]) && watch.Elapsed < TimeSpan.FromSeconds(15)) Thread.Sleep(25);
            return File.Exists(args[3]) ? 0 : 1;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static string NewName() => "Local\\EposControl.Tests." + Guid.NewGuid().ToString("N");
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertMissing(string name)
    {
        TestSuite.Throws<WaitHandleCannotBeOpenedException>(() => { using var mutex = Mutex.OpenExisting(name + "_mutex"); });
        TestSuite.Throws<FileNotFoundException>(() => { using var map = MemoryMappedFile.OpenExisting(name + "_memory"); });
        TestSuite.Throws<WaitHandleCannotBeOpenedException>(() => { using var changed = EventWaitHandle.OpenExisting(name + "_event"); });
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed class Rig : IDisposable
    {
        public string Name { get; } = NewName();
        public Mutex? Mutex; public MemoryMappedFile? Map; public MemoryMappedViewAccessor? View; public EventWaitHandle? Changed;
        public Rig(int size = 4096)
        {
            try {
                Mutex = new(false, Name + "_mutex", out var created); TestSuite.Assert(created);
                Map = MemoryMappedFile.CreateNew(Name + "_memory", size); View = Map.CreateViewAccessor(0, 0);
                var data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "apo-memory-009f.bin"));
                View.WriteArray(0, data, 0, size < data.Length ? size : data.Length);
                Changed = new(false, EventResetMode.ManualReset, Name + "_event", out created); TestSuite.Assert(created);
            } catch { Dispose(); throw; }
        }
        public byte[] Bytes() { var data = new byte[(int)View!.Capacity]; View.ReadArray(0, data, 0, data.Length); return data; }
        public void Dispose() { Changed?.Dispose(); View?.Dispose(); Map?.Dispose(); Mutex?.Dispose(); Changed = null; View = null; Map = null; Mutex = null; }
    }
}
