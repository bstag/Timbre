using System.Text.Json;
using EposControl.Core;

internal static class TopologyDiagnostics
{
    internal static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 1;
        var exit = 1;
        var thread = new Thread(() => {
            try {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                var rows = new List<object>(); var failed = false;
                foreach (var endpoint in new CoreAudioBackend().Discover()) {
                    try { rows.Add(new { Endpoint = endpoint, Topology = WindowsAudioTopology.Read(endpoint) }); }
                    catch (Exception ex) { failed = true; rows.Add(new { Endpoint = endpoint, Error = ex.ToString() }); }
                }
                var index = Array.IndexOf(args, "--report"); if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Use --topology --report FILE.");
                var path = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Read-only topology: {path}"); exit = failed ? 1 : 0;
            } catch (Exception ex) { Console.Error.WriteLine(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return exit;
    }
}
