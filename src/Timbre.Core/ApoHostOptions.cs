namespace Timbre.Core;

public sealed record ApoHostOptions(string Mode, string ReportDirectory, string StopFile, int Seconds,
    string? InitialState = null, string? StateDirectory = null, string? DeviceInstance = null)
{
    public static ApoHostOptions Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("--retain-b20" or "--initialize-b20" or "--manage-b20" or "--manage-gsx" or "--initialize-gsx" or "--initialize-gsx-paused" or "--validate-gsx" or "--validate-managed-gsx") || args.Length % 2 != 1)
            throw new ArgumentException("Choose an initialization, retention, managed or validation mode followed by named value pairs.");
        var values = new Dictionary<string, string>();
        for (var i = 1; i < args.Length; i += 2) {
            if (args[i] is not ("--initial-state" or "--state-directory" or "--device-instance" or "--report-directory" or "--stop-file" or "--seconds") ||
                string.IsNullOrWhiteSpace(args[i + 1]) || !values.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Unknown, duplicate or empty host option.");
        }
        string Required(string key) => values.GetValueOrDefault(key) ?? throw new ArgumentException("Missing " + key);
        if (!int.TryParse(Required("--seconds"), out var seconds) || seconds is < 30 or > 600) throw new ArgumentException("Host duration must be 30..600 seconds.");
        var mode = args[0];
        var startup = mode == "--retain-b20" ? null : Path.GetFullPath(Required("--initial-state"));
        var managed = mode is "--manage-b20" or "--manage-gsx" or "--validate-managed-gsx";
        var state = managed ? Path.GetFullPath(Required("--state-directory")) : null;
        var instance = managed ? Required("--device-instance") : null;
        if (mode == "--retain-b20" && values.ContainsKey("--initial-state") || !managed && (values.ContainsKey("--state-directory") || values.ContainsKey("--device-instance")))
            throw new ArgumentException("Settings restore options require managed mode; retention is read-only.");
        return new(mode, Path.GetFullPath(Required("--report-directory")), Path.GetFullPath(Required("--stop-file")), seconds, startup, state, instance);
    }
}
