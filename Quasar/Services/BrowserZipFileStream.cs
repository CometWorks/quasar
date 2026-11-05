using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Quasar.Services;

/// <summary>Seekable access to a browser file without uploading its unused contents.</summary>
/// <remarks>ZipArchive's synchronous metadata reads must run on a worker, outside the Blazor circuit.</remarks>
internal sealed class BrowserZipFileStream : Stream
{
    private readonly IJSObjectReference _module, _file;
    private readonly CancellationToken _cancellation;
    private readonly long _length;
    private long _position, _requestedBytes, _rangePosition, _rangeEnd;
    private long? _entryEnd;
    private IJSStreamReference? _reference;
    private Stream? _range;
    private bool _disposed;

    private BrowserZipFileStream(IJSObjectReference module, IJSObjectReference file, long length, CancellationToken cancellation)
        => (_module, _file, _length, _cancellation) = (module, file, length, cancellation);

    public static async Task<BrowserZipFileStream> OpenAsync(IJSRuntime js, ElementReference input, CancellationToken token)
    {
        var module = await js.InvokeAsync<IJSObjectReference>("import", token, "./server-import-file.js");
        IJSObjectReference? file = null;
        try
        {
            file = await module.InvokeAsync<IJSObjectReference>("openFile", token, input);
            long length = await file.InvokeAsync<long>("getLength", token);
            if (length <= 0 || length > 9_007_199_254_740_991L)
                throw new InvalidOperationException("The selected ZIP file is empty or too large to address safely.");
            return new(module, file, length, token);
        }
        catch
        {
            if (file is not null) await file.DisposeAsync();
            await module.DisposeAsync();
            throw;
        }
    }

    // After ZipArchive opens an entry, Position is its compressed data offset. Stream exactly
    // that range once, avoiding both per-deflate-buffer round trips and speculative read-ahead.
    internal void BeginEntry(long compressedLength)
    {
        if (compressedLength < 0 || compressedLength > Length - Position)
            throw new InvalidDataException("ZIP entry lies outside the selected file.");
        _entryEnd = Position + compressedLength;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellation, cancellationToken);
        var token = cancellation.Token;
        token.ThrowIfCancellationRequested();
        if (buffer.Length == 0 || Position == Length) return 0;
        int count = (int)Math.Min(buffer.Length, Length - Position);
        if (_range is null || _rangePosition != Position || Position >= _rangeEnd)
        {
            await CloseRangeAsync();
            long size = _entryEnd is { } end && end > Position ? end - Position : count;
            if (size > WorldSaveFiles.MaxUploadBytes - _requestedBytes)
                throw new InvalidOperationException("Required ZIP data exceeds the 10 GiB transfer limit.");
            _requestedBytes += size;
            _reference = await _file.InvokeAsync<IJSStreamReference>("readRange", token, Position, size);
            if (_reference.Length != size) throw new InvalidDataException("Browser returned an unexpected ZIP range size.");
            _range = await _reference.OpenReadStreamAsync(size, token);
            _rangePosition = Position;
            _rangeEnd = Position + size;
        }
        int read = await _range.ReadAsync(buffer[..(int)Math.Min(count, _rangeEnd - Position)], token);
        if (read == 0) throw new EndOfStreamException("ZIP transfer ended before the requested data arrived.");
        _position += read;
        _rangePosition += read;
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), _cancellation).AsTask().GetAwaiter().GetResult();

    public override int Read(Span<byte> buffer)
    {
        byte[] bytes = new byte[buffer.Length];
        int read = Read(bytes, 0, bytes.Length);
        bytes.AsSpan(0, read).CopyTo(buffer);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long position = checked((origin switch
        {
            SeekOrigin.Begin => 0,
            SeekOrigin.Current => Position,
            SeekOrigin.End => Length,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        }) + offset);
        if (position < 0 || position > Length) throw new IOException("ZIP seek is outside the selected file.");
        if (position != _position) _entryEnd = null;
        return _position = position;
    }

    private async ValueTask CloseRangeAsync()
    {
        try { if (_range is not null) await _range.DisposeAsync(); }
        finally
        {
            _range = null;
            if (_reference is not null) await _reference.DisposeAsync();
            _reference = null;
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await CloseRangeAsync(); }
        finally
        {
            try { await _file.DisposeAsync(); }
            finally { await _module.DisposeAsync(); }
        }
        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
