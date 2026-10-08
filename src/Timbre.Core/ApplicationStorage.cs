namespace Timbre.Core;

// Shared by real startup and offline verification. Opening stores never imports
// older application data or persists settings; saves remain explicit operations.
public sealed class ApplicationStorage
{
    public string DirectoryPath { get; }
    public ProfileStore Profiles { get; }
    public SetupProfileStore Setups { get; }
    public ProcessingStateStore Processing { get; }

    private ApplicationStorage(string directory)
    {
        DirectoryPath = Path.GetFullPath(directory);
        Profiles = new(Path.Combine(DirectoryPath, "profiles.json"));
        Setups = new(Path.Combine(DirectoryPath, "setups.json"));
        Processing = new(Path.Combine(DirectoryPath, "processing-state"));
    }

    public static ApplicationStorage Open(string executableDirectory, string localApplicationData,
        bool demo = false, string? dataDirectory = null, string? renderDestination = null)
    {
        var directory = renderDestination is not null
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(renderDestination))!, "ui-verification-" + Guid.NewGuid().ToString("N"))
            : dataDirectory is not null ? dataDirectory
            : demo ? Path.Combine(executableDirectory, "data", "demo")
            : Path.Combine(localApplicationData, "Timbre");
        return new(directory);
    }
}
