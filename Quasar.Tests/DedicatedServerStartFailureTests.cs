using Quasar.Models;
using Quasar.Services;
using Xunit;
using static Quasar.Models.DedicatedServerProcessState;

namespace Quasar.Tests;

// SE1-0217: a server that exits before its world is ready failed to start. It is neither an
// admin stop nor the old process of an in-game restart.
public sealed class DedicatedServerStartFailureTests
{
    private static readonly DateTimeOffset Requested = DateTimeOffset.Parse("2026-10-15T18:00:00Z");

    private static readonly DedicatedServerRestartInfo PendingRestart = new()
    {
        Cause = DedicatedServerRestartCause.CrashRecovery,
        RequestedAtUtc = Requested,
        Outcome = DedicatedServerRestartOutcome.Pending,
    };

    [Fact]
    public void FirstStartIsStartingUpUntilRunning()
    {
        Assert.True(DedicatedServerSupervisor.IsStartingUp(Starting, Requested, null));
        Assert.False(DedicatedServerSupervisor.IsStartingUp(Running, Requested, null));
        Assert.False(DedicatedServerSupervisor.IsStartingUp(Stopping, Requested, null));
    }

    [Fact]
    public void OldProcessOfARestartIsQuittingNotStarting()
    {
        var oldProcessStarted = Requested.AddHours(-2);

        Assert.True(
            DedicatedServerSupervisor.IsRestartExit(Restarting, oldProcessStarted, PendingRestart)
        );
        Assert.False(
            DedicatedServerSupervisor.IsStartingUp(Restarting, oldProcessStarted, PendingRestart)
        );
    }

    // Before the fix the replacement process counted as the restart's own exit, so a start
    // failing after a crash restart reset the attempt counter and restarted forever.
    [Fact]
    public void ReplacementProcessOfARestartIsStartingUp()
    {
        var replacementStarted = Requested.AddSeconds(5);

        Assert.False(
            DedicatedServerSupervisor.IsRestartExit(Restarting, replacementStarted, PendingRestart)
        );
        Assert.True(
            DedicatedServerSupervisor.IsStartingUp(Restarting, replacementStarted, PendingRestart)
        );
    }

    [Fact]
    public void RestartExitNeedsTheRestartingState()
    {
        Assert.False(
            DedicatedServerSupervisor.IsRestartExit(Running, Requested.AddHours(-2), PendingRestart)
        );
        Assert.False(
            DedicatedServerSupervisor.IsRestartExit(
                Starting,
                Requested.AddHours(-2),
                PendingRestart
            )
        );
    }
}
