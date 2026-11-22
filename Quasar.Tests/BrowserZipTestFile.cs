using Quasar.Services;

namespace Quasar.Tests;

internal sealed class BrowserZipTestFile(byte[] bytes)
{
    public List<(long Offset, long Length)> Ranges { get; } = [];
    public long ReportedLength { get; set; } = bytes.LongLength;
    public bool WrongRangeLength { get; set; }

    public BrowserZipFileStream Open(CancellationToken token = default) => new(ReportedLength, ReportedLength, (offset, length, ct) =>
    {
        ct.ThrowIfCancellationRequested();
        Ranges.Add((offset, length));
        return Task.FromResult<Stream>(new MemoryStream(bytes, checked((int)offset), checked((int)length) - (WrongRangeLength ? 1 : 0), false));
    }, token);
}
