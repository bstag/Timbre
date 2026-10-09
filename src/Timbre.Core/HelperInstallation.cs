using System.Security.Cryptography;

namespace Timbre.Core;

internal sealed class HelperInstallation
{
    public string Build { get; }
    public string? Root { get; }
    public HelperInstallation(string buildDirectory)
    {
        Build = Path.GetFullPath(buildDirectory);
        for (var directory = new DirectoryInfo(Build); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "tools", "Test-GsxInitialization.ps1"))) { Root = directory.FullName; break; }
    }
    public string? UnavailableReason(string script)
    {
        if (Root is null || !File.Exists(Path.Combine(Root, "tools", script))) return "This experimental workflow requires its verified source checkout.";
        var copies = new[] { Path.Combine(Build, "Timbre.Core.dll"), Path.Combine(Build, "host", "Timbre.Core.dll"),
            Path.Combine(Root, "tests", "Timbre.Tests", "bin", "Release", "net9.0", "Timbre.Core.dll") };
        try {
            if (copies.Concat(new[] { Path.Combine(Build, "Timbre.exe"), Path.Combine(Build, "host", "Timbre.Host.exe"),
                Path.Combine(Root, "tests", "Timbre.Tests", "bin", "Release", "net9.0", "Timbre.Tests.dll") }).Any(path => !File.Exists(path)))
                return "Build and verify the app, helper and tests first.";
            return copies.Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).Distinct().Count() == 1
                ? null : "App, helper and test binaries differ. Build and verify matching outputs first.";
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "Cannot verify helper files: " + ex.Message; }
    }
}
