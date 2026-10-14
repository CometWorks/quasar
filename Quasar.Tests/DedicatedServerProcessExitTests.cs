using System.Diagnostics;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class DedicatedServerProcessExitTests
{
    [Fact]
    public async Task ProcessIsActiveUntilItIsGone()
    {
        var process = Process.Start(
            OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1 >nul")
                : new ProcessStartInfo("sleep", "1")
        )!;

        Assert.True(DedicatedServerSupervisor.IsProcessActive(process));
        await DedicatedServerSupervisor.WaitForExitAsync(process, CancellationToken.None);
        Assert.False(DedicatedServerSupervisor.IsProcessActive(process));

        process.Dispose();
        Assert.False(DedicatedServerSupervisor.IsProcessActive(process));
        Assert.False(DedicatedServerSupervisor.IsProcessActive(null));
    }
}
