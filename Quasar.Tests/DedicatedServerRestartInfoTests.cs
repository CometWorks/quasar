using System.Text.Json;
using Quasar.Models;
using Xunit;

namespace Quasar.Tests;

public sealed class DedicatedServerRestartInfoTests
{
    [Fact]
    public void RuntimeSnapshotRoundTripPreservesRestartDetails()
    {
        var requestedAt = new DateTimeOffset(2026, 8, 3, 12, 34, 56, TimeSpan.Zero);
        var snapshot = new DedicatedServerRuntimeSnapshot
        {
            UniqueName = "test",
            LastRestart = new DedicatedServerRestartInfo
            {
                Cause = DedicatedServerRestartCause.HealthPolicy,
                Reason = "Quasar.Agent heartbeat stale beyond 30s timeout.",
                RequestedAtUtc = requestedAt,
                CompletedAtUtc = requestedAt.AddMinutes(2),
                Outcome = DedicatedServerRestartOutcome.Recovered,
            },
        };

        var copy = JsonSerializer.Deserialize<DedicatedServerRuntimeSnapshot>(
            JsonSerializer.Serialize(snapshot));

        Assert.NotNull(copy?.LastRestart);
        Assert.Equal(DedicatedServerRestartCause.HealthPolicy, copy.LastRestart.Cause);
        Assert.Equal(snapshot.LastRestart.Reason, copy.LastRestart.Reason);
        Assert.Equal(requestedAt, copy.LastRestart.RequestedAtUtc);
        Assert.Equal(requestedAt.AddMinutes(2), copy.LastRestart.CompletedAtUtc);
        Assert.Equal(DedicatedServerRestartOutcome.Recovered, copy.LastRestart.Outcome);
    }

    [Fact]
    public void OlderRuntimeSnapshotWithoutRestartDetailsStillLoads()
    {
        var snapshot = JsonSerializer.Deserialize<DedicatedServerRuntimeSnapshot>(
            """{"UniqueName":"test","State":2}""");

        Assert.NotNull(snapshot);
        Assert.Null(snapshot.LastRestart);
    }
}
