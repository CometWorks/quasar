extern alias Bootstrap;

using System.Runtime.InteropServices;
using Xunit;
using BootstrapProgram = Bootstrap::Quasar.Bootstrap.Program;

namespace Quasar.Tests;

public sealed class BootstrapSignalTests
{
    [Fact]
    public void TerminateSignalStopsTheLauncherInsteadOfEndingTheProcess()
    {
        using var shutdown = new CancellationTokenSource();
        var context = new PosixSignalContext(PosixSignal.SIGTERM);

        BootstrapProgram.CancelOnSignal(context, shutdown);

        Assert.True(context.Cancel);
        Assert.True(shutdown.IsCancellationRequested);
    }

    [Fact]
    public void TerminateSignalIsHandledOnLinuxAndMacOS()
    {
        using var shutdown = new CancellationTokenSource();
        using var registration = BootstrapProgram.RegisterTerminateSignal(shutdown);

        Assert.Equal(
            OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
            registration is not null
        );
        Assert.False(shutdown.IsCancellationRequested);
    }
}
