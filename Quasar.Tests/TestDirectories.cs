namespace Quasar.Tests;

/// <summary>Cleanup helpers for the temporary folders tests create.</summary>
internal static class TestDirectories
{
    /// <summary>
    /// Deletes a folder tree, retrying for up to five seconds. On Windows a file can
    /// stay open for a moment after the code under test released it, because a
    /// cancelled task is still unwinding or a scanner is reading it, and
    /// <see cref="Directory.Delete(string, bool)"/> then throws. A folder that is
    /// still locked after the retries fails the test with the original error.
    /// </summary>
    public static void Delete(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException && attempt < 50)
            {
                Thread.Sleep(100);
            }
        }
    }
}
