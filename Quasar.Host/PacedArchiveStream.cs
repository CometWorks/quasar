using System.Diagnostics;

namespace Quasar.Host;

// Pace the tar writer so headers, padding, file data and the snapshot manifest share one budget.
internal sealed class PacedArchiveStream(Stream output, long bytesPerSecond) : Stream
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double dueMilliseconds;

    public override bool CanRead => false;
    public override bool CanSeek => output.CanSeek;
    public override bool CanWrite => true;
    public override long Length => output.Length;
    public override long Position
    {
        get => output.Position;
        set => output.Position = value;
    }

    public override void Flush() => output.Flush();

    public override long Seek(long offset, SeekOrigin origin) => output.Seek(offset, origin);

    public override void SetLength(long value) => output.SetLength(value);

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            int count = Math.Min(buffer.Length, 64 * 1024);
            double now = clock.Elapsed.TotalMilliseconds;
            // Allow for scheduler jitter, but a slow source or disk cannot bank seconds of bandwidth.
            dueMilliseconds = Math.Max(dueMilliseconds, now - 16) + count * 1000d / bytesPerSecond;
            int delay = (int)(dueMilliseconds - now);
            if (delay > 0)
                Thread.Sleep(delay);
            output.Write(buffer[..count]);
            buffer = buffer[count..];
        }
    }
}
