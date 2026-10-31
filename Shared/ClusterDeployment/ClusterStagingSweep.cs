namespace Quasar.ClusterDeployment;

// Workflows remove their staging in finally blocks, so only a killed process leaves any behind.
internal static class ClusterStagingSweep
{
    // Only entries older than startedUtc: anything newer may belong to a workflow of this process.
    internal static void Sweep(string parent, DateTime startedUtc, Action<string> report, params string[] patterns)
    {
        if (!Directory.Exists(parent)) return;
        foreach (string pattern in patterns)
            foreach (string path in Directory.EnumerateFileSystemEntries(parent, pattern))
                try
                {
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0 || File.GetLastWriteTimeUtc(path) >= startedUtc) continue;
                    if ((attributes & FileAttributes.Directory) != 0) DeleteTree(path);
                    else File.Delete(path);
                    report("removed staging leftover " + path);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { report($"staging leftover {path} not removed: {error.Message}"); }
    }

    // Runtime trees and snapshots hold read-only directories, which a plain recursive delete cannot empty.
    // Files keep their modes: they can be hard links of read-only content objects. Links are not followed.
    internal static void DeleteTree(string path)
    {
        Writable(path);
        Directory.Delete(path, true);
    }

    private static void Writable(string directory)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (string child in Directory.EnumerateDirectories(directory))
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) Writable(child);
    }
}
