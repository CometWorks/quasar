using Admin = CometWorks.ClusterGateway.AdminContract.V1;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterLifecycleSafetyTests
{
    [Theory]
    [InlineData(Admin.ClusterPhase.Serving, true)]
    [InlineData(Admin.ClusterPhase.Down, false)]
    public void GatewayStopFailsWithoutCleanDownProof(Admin.ClusterPhase phase, bool hasMarker)
    {
        ClusterGatewayException exception = Assert.Throws<ClusterGatewayException>(() =>
            ClusterApi.EnsureGatewayCanStop(Status(phase, hasMarker)));

        Assert.Equal("cluster_not_cleanly_down", exception.Code);
    }

    [Fact]
    public void GatewayStopAcceptsCleanDownProof() =>
        ClusterApi.EnsureGatewayCanStop(Status(Admin.ClusterPhase.Down, hasMarker: true));

    private static Admin.ClusterStatus Status(Admin.ClusterPhase phase, bool hasMarker) => new(
        "demo", "world", phase, Admin.StartupKind.Recovery, null,
        hasMarker ? DateTimeOffset.UtcNow : null, false, false, [],
        new Admin.ClusterCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        new Admin.WorldAuthorityStatus(null, 0, 0, DateTimeOffset.MinValue), [], []);
}
