using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class LinuxNiceTests
{
    // Fields 3 to 22 of /proc/<pid>/stat: priority 25 in field 18, nice 5 in
    // field 19.
    private const string StatTail = " S 1 2 3 4 5 6 7 8 9 10 11 12 13 14 25 5 4 0 99\n";

    [Theory]
    [InlineData("1234 (MagnetarInterim.bin)")]
    [InlineData("1234 (a) b (c d)")]
    [InlineData("1234 ()")]
    public void ParsesNiceAfterTheCommandName(string head) =>
        Assert.Equal(5, DedicatedServerSupervisor.ParseLinuxNice(head + StatTail));

    [Theory]
    [InlineData("")]
    [InlineData("1234 MagnetarInterim.bin S 1 2 3")]
    [InlineData("1234 (MagnetarInterim.bin) S 1 2 3")]
    [InlineData("1234 (MagnetarInterim.bin) S 1 2 3 4 5 6 7 8 9 10 11 12 13 14 25 x 4")]
    public void UnreadableStatGivesNull(string stat) =>
        Assert.Null(DedicatedServerSupervisor.ParseLinuxNice(stat));

    [Fact]
    public void ReadsEveryThreadOfARunningProcess()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var started = new ManualResetEventSlim();
        using var stop = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            started.Set();
            stop.Wait();
        });
        thread.Start();
        started.Wait();
        try
        {
            var nices = DedicatedServerSupervisor.ReadLinuxThreadNices(Environment.ProcessId);

            Assert.NotNull(nices);
            Assert.Contains(Environment.ProcessId, nices.Keys);
            Assert.True(nices.Count > 1);
        }
        finally
        {
            stop.Set();
            thread.Join();
        }
    }

    [Fact]
    public void GoneProcessGivesNull() =>
        Assert.Null(DedicatedServerSupervisor.ReadLinuxThreadNices(int.MaxValue));
}
