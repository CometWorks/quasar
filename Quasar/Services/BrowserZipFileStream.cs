namespace Quasar.Services;

/// <summary>Seekable access to a browser file without uploading its unused contents.</summary>
internal sealed class BrowserZipFileStream : Stream
{
    private readonly Func<long, long, CancellationToken, Task<Stream>> _openRange;
    private readonly CancellationToken _cancellation;
    private readonly long _length, _directoryOffset;
    private long _position, _requestedBytes, _rangeStart, _rangeEnd;
    private long? _entryEnd;
    private Stream? _range;
    private bool _disposed;

    internal BrowserZipFileStream(long length, long directoryOffset,
        Func<long, long, CancellationToken, Task<Stream>> openRange, CancellationToken cancellation)
    {
        if (length is <= 0 or > 9_007_199_254_740_991L || directoryOffset < 0 || directoryOffset > length)
            throw new InvalidDataException("The selected ZIP size or central directory is invalid.");
        (_length, _directoryOffset, _openRange, _cancellation) = (length, directoryOffset, openRange, cancellation);
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
        if (_range is null || Position < _rangeStart || Position >= _rangeEnd)
        {
            _range = null;
            // Buffer the central-directory metadata, so browsing a large archive doesn't require a network turn per name.
            long size = _entryEnd is { } end && end > Position ? end - Position
                : Position >= _directoryOffset ? Math.Min(Length - Position, Math.Max(256 * 1024, count)) : count;
            if (size > WorldSaveFiles.MaxUploadBytes - _requestedBytes)
                throw new InvalidOperationException("Required ZIP data exceeds the 10 GiB transfer limit.");
            _requestedBytes += size;
            _range = await _openRange(Position, size, token);
            if (!_range.CanSeek || _range.Length != size) throw new InvalidDataException("Browser returned an unexpected ZIP range size.");
            _rangeStart = Position;
            _rangeEnd = Position + size;
        }
        _range.Position = Position - _rangeStart;
        int read = await _range.ReadAsync(buffer[..(int)Math.Min(count, _rangeEnd - Position)], token);
        if (read == 0) throw new EndOfStreamException("ZIP transfer ended before the requested data arrived.");
        _position += read;
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

    protected override void Dispose(bool disposing)
    {
        // The SDK owns each staged upload until the callback asks for another range.
        _disposed = true;
        _range = null;
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
