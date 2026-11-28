using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Quasar.Services;

// SE-1969: writes the new objects of one capture on a few threads at a lower CPU priority, gzipping the ones that
// compress, while the archive keeps streaming in. At most MaxPendingBytes of file content wait in memory; a
// capture that is faster than its writers waits for them. Complete() returns once every object is published.
internal sealed class ClusterBackupObjectWriter : IDisposable
{
    internal const long MaxPendingBytes = 256L * 1024 * 1024;

    // nice 5, the same as a BelowNormal dedicated server: the cluster and the web UI come first.
    private const int Nice = 5;
    private static readonly ZLibCompressionOptions Level = new() { CompressionLevel = 5 };

    private readonly string _root;
    private readonly BlockingCollection<Job> _jobs = new();
    private readonly HashSet<string> _queued = new(StringComparer.Ordinal);
    private readonly Thread[] _threads;
    private readonly object _budget = new();
    private long _pending;
    private Exception? _failure;

    private sealed record Job(string Sha256, byte[]? Data, string? File, long Size, bool Compress);

    internal ClusterBackupObjectWriter(string clusterRoot, int threads = 0)
    {
        _root = clusterRoot;
        _threads = new Thread[
            threads > 0 ? threads : Math.Clamp(Environment.ProcessorCount / 2, 1, 8)
        ];
        for (int i = 0; i < _threads.Length; i++)
        {
            _threads[i] = new Thread(Run)
            {
                IsBackground = true,
                Name = "Backup objects",
                Priority = ThreadPriority.BelowNormal,
            };
            _threads[i].Start();
        }
    }

    /// <summary>Queues an object unless the store has it or this capture queued it already. Exactly one of
    /// <paramref name="data"/> and <paramref name="file"/> holds its bytes. Returns true when the writer took over
    /// the file; otherwise the file stays the caller's.</summary>
    internal bool Add(string sha256, byte[]? data, string? file, bool compress)
    {
        if (_failure is { } failure)
            throw new IOException("Writing a backup object failed.", failure);
        string target = ClusterBackupStore.ObjectPath(_root, sha256);
        if (
            !_queued.Add(sha256)
            || File.Exists(target)
            || File.Exists(target + ClusterBackupStore.PackedSuffix)
        )
            return false;
        long size = data?.LongLength ?? 0;
        lock (_budget)
        {
            while (_pending > 0 && _pending + size > MaxPendingBytes && _failure is null)
                Monitor.Wait(_budget);
            _pending += size;
        }
        _jobs.Add(new Job(sha256, data, file, size, compress));
        return file is not null;
    }

    /// <summary>Waits for every queued object; throws the first failure.</summary>
    internal void Complete()
    {
        _jobs.CompleteAdding();
        foreach (var thread in _threads)
            thread.Join();
        if (_failure is { } failure)
            throw new IOException("Writing a backup object failed.", failure);
    }

    public void Dispose()
    {
        if (!_jobs.IsAddingCompleted)
            _jobs.CompleteAdding();
        foreach (var thread in _threads)
            thread.Join();
        _jobs.Dispose();
    }

    private void Run()
    {
        LowerPriority();
        foreach (var job in _jobs.GetConsumingEnumerable())
        {
            try
            {
                // After a failure the rest is only drained: the capture fails anyway.
                if (Volatile.Read(ref _failure) is null)
                    Write(job);
            }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref _failure, error, null);
            }
            finally
            {
                if (job.File is not null && File.Exists(job.File))
                    File.Delete(job.File);
                lock (_budget)
                {
                    _pending -= job.Size;
                    Monitor.PulseAll(_budget);
                }
            }
        }
    }

    // Gzipped when that saves at least a tenth, else as it is.
    private void Write(Job job)
    {
        string target = ClusterBackupStore.ObjectPath(_root, job.Sha256);
        if (
            job.Compress
            && ClusterBackupStore.Publish(
                target + ClusterBackupStore.PackedSuffix,
                temporary =>
                {
                    using var input = Open(job);
                    using var output = new FileStream(
                        temporary,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        1 << 16
                    );
                    using (var gzip = new GZipStream(output, Level, leaveOpen: true))
                        input.CopyTo(gzip, 1 << 16);
                    return output.Length <= input.Length - input.Length / 10;
                }
            )
        )
            return;
        if (job.Data is not null)
            ClusterBackupStore.Publish(
                target,
                temporary => File.WriteAllBytes(temporary, job.Data)
            );
        else
            ClusterBackupStore.Publish(target, temporary => File.Move(job.File!, temporary));
    }

    private static Stream Open(Job job) =>
        job.Data is not null
            ? new MemoryStream(job.Data, writable: false)
            : new FileStream(job.File!, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);

    // .NET does not apply thread priorities on Linux, where nice is per thread: renice this one.
    private static void LowerPriority()
    {
        if (!OperatingSystem.IsLinux())
            return;
        try
        {
            _ = setpriority(0, gettid(), Nice);
        } // PRIO_PROCESS with a thread id names that thread alone.
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException)
        { }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setpriority(int which, int who, int prio);

    [DllImport("libc")]
    private static extern int gettid();
}
