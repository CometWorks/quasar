using Magnetar.Protocol.Runtime;

namespace Quasar.Updates;

// SE-1351: a cluster backup and a planned Quasar restart must not overlap. Quasar holds this OS-owned lock
// while it backs up clusters; the updater holds it from just before it stops Quasar until the new release
// is healthy. So a restart waits for a running backup, and a backup due during a restart waits for it.
// A crashed holder cannot leave it locked.
internal static class ClusterBackupLease
{
    internal static string DefaultPath => Path.Combine(MagnetarPaths.GetQuasarDirectory(), "cluster-backup.lock");

    // Null when someone else holds it.
    public static FileStream? TryAcquire(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 11 or 32 or 33) { return null; }
    }

    // Null when it is still held after the wait.
    public static async Task<FileStream?> AcquireAsync(string path, TimeSpan wait, CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow + wait;
        while (true)
        {
            if (TryAcquire(path) is { } lease) return lease;
            if (DateTimeOffset.UtcNow >= deadline) return null;
            await Task.Delay(1000, token);
        }
    }
}
