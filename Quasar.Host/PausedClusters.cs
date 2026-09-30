using System.Collections.Concurrent;

namespace Quasar.Host;

// Clusters whose interrupted activation or restore could not be recovered at startup. Their
// state may be half applied, so the executor loop leaves them alone until the operation is
// retried successfully; the Host itself and every other cluster keep running.
internal static class PausedClusters
{
    private static readonly ConcurrentDictionary<string, string> Reasons = new(StringComparer.OrdinalIgnoreCase);

    internal static void Pause(string clusterId, string reason)
    {
        Reasons[clusterId] = reason;
        Console.Error.WriteLine($"cluster={clusterId} paused: {reason}");
    }

    internal static void Resume(string clusterId)
    {
        if (Reasons.TryRemove(clusterId, out _))
            Console.Error.WriteLine($"cluster={clusterId} resumed");
    }

    internal static bool IsPaused(string clusterId) => Reasons.ContainsKey(clusterId);
}
