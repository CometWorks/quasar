namespace Quasar.Diagnostics;

/// <summary>Bulk evidence can move independently of stable enrollment and consent state.</summary>
public static class DiagnosticStorage
{
    public static string Resolve(string? configuredDirectory, string defaultDirectory)
    {
        if (string.IsNullOrWhiteSpace(configuredDirectory)) return Path.GetFullPath(defaultDirectory);
        if (!Path.IsPathFullyQualified(configuredDirectory))
            throw new ArgumentException("Diagnostic storage must be an absolute local directory.", nameof(configuredDirectory));
        string path = Path.GetFullPath(configuredDirectory);
        if (Path.TrimEndingDirectorySeparator(path) == Path.GetPathRoot(path))
            throw new ArgumentException("Diagnostic storage must not be a filesystem root.", nameof(configuredDirectory));
        return path;
    }

    public static DiagnosticCollector Create(string stateDirectory, string hostId, string? configuredDirectory = null) =>
        new(Resolve(configuredDirectory, stateDirectory), hostId, stateDirectory);

    public static long AvailableFreeSpace(string directory)
    {
        string path = Path.GetFullPath(directory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // Linux's root path is always '/', even when evidence lives on another mounted disk.
        var drive = DriveInfo.GetDrives().Where(d => path.Equals(Path.TrimEndingDirectorySeparator(d.Name), comparison)
            || path.StartsWith(Path.EndsInDirectorySeparator(d.Name) ? d.Name : d.Name + Path.DirectorySeparatorChar, comparison))
            .OrderByDescending(d => d.Name.Length).FirstOrDefault();
        return (drive ?? new DriveInfo(Path.GetPathRoot(path)!)).AvailableFreeSpace;
    }
}
