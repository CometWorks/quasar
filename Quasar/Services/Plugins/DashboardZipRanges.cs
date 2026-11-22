using Quasar.Plugin.Abstractions.Operations;

namespace Quasar.Services.Plugins;

internal sealed record ZipSlice(long Sequence, long Offset, long Length);

// ZipArchive seeks on the worker. The browser answers one numbered request at a time with an SDK upload.
// An upload stays open while the reader consumes it. Its response is the next range, or sequence zero after review is ready.
internal sealed class DashboardZipRanges : IDisposable
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(2);
    private readonly CancellationTokenSource _stop = new(Idle);
    private readonly object _gate = new();
    private TaskCompletionSource<ZipSlice> _next = Signal<ZipSlice>();
    private TaskCompletionSource<Stream>? _incoming;
    private long _sequence;
    private bool _finished;

    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task<ZipSlice> Start(long size, long directoryOffset, Func<Stream, CancellationToken, Task> inspect,
        Action<bool> release, CancellationToken request)
    {
        var file = new BrowserZipFileStream(size, directoryOffset, OpenAsync, _stop.Token);
        var first = _next.Task;
        _ = InspectAsync();
        return WaitForNextAsync(first, request);

        async Task InspectAsync()
        {
            Exception? failure = null;
            try { using (file) await inspect(file, _stop.Token); }
            catch (Exception error) { failure = error; }
            lock (_gate)
            {
                _finished = true;
                // Publish/release staging before the browser is told it may read the review.
                release(failure is not null);
                if (failure is null) _next.TrySetResult(new(0, 0, 0));
                else _next.TrySetException(failure);
                _stop.Dispose();
            }
        }
    }

    private Task<Stream> OpenAsync(long offset, long length, CancellationToken token)
    {
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            _incoming = Signal<Stream>();
            _next.TrySetResult(new(++_sequence, offset, length));
            return _incoming.Task.WaitAsync(token);
        }
    }

    internal Task<ZipSlice> SupplyAsync(long sequence, Stream data, CancellationToken request)
    {
        Task<ZipSlice> next;
        lock (_gate)
        {
            if (_finished || _incoming is null || _sequence != sequence)
                throw new QuasarOperationException(409, "This ZIP range is no longer requested. Reopen the import.");
            _stop.CancelAfter(Idle);
            _next = Signal<ZipSlice>();
            next = _next.Task;
            _incoming.SetResult(data);
            _incoming = null;
        }
        return WaitForNextAsync(next, request);
    }

    private async Task<ZipSlice> WaitForNextAsync(Task<ZipSlice> next, CancellationToken request)
    {
        // Once the upload has been handed to the reader, wait for it to finish or cancel before
        // the transport can dispose the staged file.
        using var abort = request.Register(Dispose);
        return await next;
    }

    // A large selected world can take longer than the idle timeout to upload on the gateway. The
    // import dialog polls progress during active work. Abandoned tabs cease renewing the job.
    internal void KeepAlive() { lock (_gate) if (!_finished) _stop.CancelAfter(Idle); }
    public void Dispose() { lock (_gate) if (!_finished) _stop.Cancel(); }
}
