using Bunit;
using Microsoft.JSInterop;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class BrowserZipFileStreamTests
{
    [Fact]
    public async Task SeekRequestsExactRangesAndEntryStreamsOnlyItsCompressedWindow()
    {
        await using var context = new QuasarBunitContext();
        byte[] data = Enumerable.Range(0, 128).Select(value => (byte)value).ToArray();
        var file = BrowserZipTestFile.Setup(context, data);
        await using (var stream = await BrowserZipFileStream.OpenAsync(context.JSInterop.JSRuntime, default, default))
        {
            byte[] buffer = new byte[4];
            stream.Seek(100, SeekOrigin.Begin);
            Assert.Equal(4, await stream.ReadAsync(buffer));
            Assert.Equal(data[100..104], buffer);
            stream.Seek(10, SeekOrigin.Begin);
            stream.BeginEntry(12);
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(4, await stream.ReadAsync(buffer));
                Assert.Equal(data[(10 + i * 4)..(14 + i * 4)], buffer);
            }
            Assert.Equal(new[] { (100L, 4L), (10L, 12L) }, file.Ranges);
            Assert.Throws<IOException>(() => stream.Seek(1, SeekOrigin.End));
            Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
            Assert.Throws<InvalidDataException>(() => stream.BeginEntry(129));
        }
        Assert.True(file.Disposed);
    }

    [Fact]
    public async Task CancellationAndUnexpectedRangeLengthsFailWithoutContinuingTransfer()
    {
        await using var context = new QuasarBunitContext();
        var file = BrowserZipTestFile.Setup(context, new byte[64]);
        using var cancellation = new CancellationTokenSource();
        await using var stream = await BrowserZipFileStream.OpenAsync(context.JSInterop.JSRuntime, default, cancellation.Token);
        file.WrongRangeLength = true;
        await Assert.ThrowsAsync<InvalidDataException>(async () => await stream.ReadAsync(new byte[4]));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.ReadAsync(new byte[4]));
        Assert.Single(file.Ranges);
    }

    [Fact]
    public async Task WholeFileCanExceedUploadLimitButRequestedDataCannot()
    {
        await using var context = new QuasarBunitContext();
        var file = BrowserZipTestFile.Setup(context, new byte[64]);
        file.ReportedLength = WorldSaveFiles.MaxUploadBytes * 2;
        await using var stream = await BrowserZipFileStream.OpenAsync(context.JSInterop.JSRuntime, default, default);
        Assert.Equal(file.ReportedLength, stream.Length);
        stream.BeginEntry(WorldSaveFiles.MaxUploadBytes + 1);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await stream.ReadAsync(new byte[4]));
        Assert.Empty(file.Ranges);
    }
}
