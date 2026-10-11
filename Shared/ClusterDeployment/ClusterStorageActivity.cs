namespace Quasar.ClusterDeployment;

// Collection waits for whole workflows, including the gap between transfer and
// catalog commit. Concurrent workflows share read access; nested leases are safe.
internal static class ClusterStorageActivity
{
    private static readonly SemaphoreSlim Readers = new(1, 1);
    private static readonly SemaphoreSlim Writer = new(1, 1);
    private static int count;
    private static long revision;
    internal static long Revision => Interlocked.Read(ref revision);
    internal static void MarkChanged() => Interlocked.Increment(ref revision);
    internal static event Action? Idle;

    internal static async Task<IDisposable> BeginAsync(CancellationToken token)
    {
        await Readers.WaitAsync(token);
        try
        {
            if (count == 0) await Writer.WaitAsync(token);
            count++;
            return new Lease(false);
        }
        finally { Readers.Release(); }
    }

    internal static async Task<IDisposable> CollectAsync(CancellationToken token)
    {
        await Writer.WaitAsync(token);
        return new Lease(true);
    }

    internal static IDisposable? TryBegin()
    {
        if (!Readers.Wait(0)) return null;
        try
        {
            if (count == 0 && !Writer.Wait(0)) return null;
            count++;
            return new Lease(false);
        }
        finally { Readers.Release(); }
    }

    private sealed class Lease(bool writer) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            if (writer) { Writer.Release(); return; }
            bool idle;
            Readers.Wait();
            try { idle = --count == 0; if (idle) Writer.Release(); }
            finally { Readers.Release(); }
            if (idle) Idle?.Invoke();
        }
    }
}
