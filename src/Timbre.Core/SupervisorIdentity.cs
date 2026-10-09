using System.ComponentModel;
using System.Diagnostics;

namespace Timbre.Core;

public static class SupervisorIdentity
{
    public static bool IsAlive(int id, DateTimeOffset started) => IsAlive(id, started, processId => {
        try { using var process = Process.GetProcessById(processId); return process.HasExited ? null : new DateTimeOffset(process.StartTime.ToUniversalTime()); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
    });
    internal static bool IsAlive(int id, DateTimeOffset started, Func<int, DateTimeOffset?> lookup)
        => id > 0 && lookup(id) is { } observed && observed.UtcTicks == started.UtcTicks;
}
