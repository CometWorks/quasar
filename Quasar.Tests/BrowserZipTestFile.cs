using Bunit;
using Microsoft.JSInterop;

namespace Quasar.Tests;

internal sealed class BrowserZipTestFile(byte[] bytes) : IJSObjectReference
{
    public List<(long Offset, long Length)> Ranges { get; } = [];
    public bool Disposed { get; private set; }
    public long ReportedLength { get; set; } = bytes.LongLength;
    public bool WrongRangeLength { get; set; }

    public static BrowserZipTestFile Setup(BunitContext context, byte[] bytes)
    {
        var file = new BrowserZipTestFile(bytes);
        var module = context.JSInterop.SetupModule("./server-import-file.js");
        module.AddInvocationHandler(new OpenFileHandler().SetResult(file));
        return file;
    }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Disposed, this);
        object value;
        if (identifier == "getLength") value = ReportedLength;
        else if (identifier == "readRange")
        {
            long offset = (long)args![0]!, length = (long)args[1]!;
            Ranges.Add((offset, length));
            value = new Reference(bytes, offset, length, WrongRangeLength);
        }
        else throw new NotSupportedException(identifier);
        return ValueTask.FromResult((TValue)value);
    }

    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }

    private sealed class OpenFileHandler() : JSRuntimeInvocationHandler<IJSObjectReference>(invocation => invocation.Identifier == "openFile", false);

    private sealed class Reference(byte[] bytes, long offset, long length, bool wrongLength) : IJSStreamReference
    {
        public long Length => wrongLength ? length + 1 : length;
        public ValueTask<Stream> OpenReadStreamAsync(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(bytes, checked((int)offset), checked((int)length), false));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
