using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Plugins;
using Xunit;

namespace Quasar.Tests;

public sealed class DashboardZipRangesTests
{
    [Fact]
    public async Task EachRangeIsConsumedOnceBeforeTheNextResponse()
    {
        using var job = new DashboardZipRanges();
        bool released = false;
        var first = await job.Start(200, 200, async (stream, ct) =>
        {
            stream.Position = 17;
            var bytes = new byte[2];
            await stream.ReadExactlyAsync(bytes, ct);
            Assert.Equal(new byte[] {7,8}, bytes);
            stream.Position = 97;
            await stream.ReadExactlyAsync(bytes, ct);
            Assert.Equal(new byte[] {9,10}, bytes);
        }, failed => released = !failed, default);
        Assert.Equal(new ZipSlice(1,17,2), first);
        var second = await job.SupplyAsync(first.Sequence, new MemoryStream([7,8]), default);
        Assert.Equal(new ZipSlice(2,97,2), second);
        var error = await Assert.ThrowsAsync<QuasarOperationException>(() => job.SupplyAsync(first.Sequence, new MemoryStream([7,8]), default));
        Assert.Equal(409, error.StatusCode);
        var done = await job.SupplyAsync(second.Sequence, new MemoryStream([9,10]), default);
        Assert.Equal(0, done.Sequence);
        Assert.True(released);
    }

    [Fact]
    public async Task AbandoningTheSelectedFileReleasesWaitingWorker()
    {
        using var job = new DashboardZipRanges();
        var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var range = await job.Start(100,100, async (stream, ct) => await stream.ReadExactlyAsync(new byte[8], ct),
            failed => released.TrySetResult(failed), default);
        Assert.Equal(8, range.Length);
        job.Dispose();
        Assert.True(await released.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<QuasarOperationException>(() => job.SupplyAsync(range.Sequence, new MemoryStream(new byte[8]), default));
    }
}
