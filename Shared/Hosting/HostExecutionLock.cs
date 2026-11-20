namespace Quasar.Hosting;

internal static class HostExecutionLock
{
    internal static FileStream? TryAcquire(string stateDirectory)
    {
        try
        {
            return new FileStream(Path.Combine(stateDirectory, "executor.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception) when (IsContention(exception))
        {
            return null;
        }
    }

    // A shared open detects the executor's exclusive lock without competing with other launch checks.
    // The child still has to acquire the lock after the check.
    internal static bool IsHeld(string stateDirectory)
    {
        try
        {
            using var probe = new FileStream(Path.Combine(stateDirectory, "executor.lock"),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (IOException exception) when (IsContention(exception)) { return true; }
    }

    // FileStream reports EWOULDBLOCK from flock on Linux and ERROR_SHARING_VIOLATION on Windows.
    private static bool IsContention(IOException exception) =>
        (OperatingSystem.IsLinux() && exception.HResult == 11)
        || (OperatingSystem.IsWindows() && exception.HResult == unchecked((int)0x80070020));
}
