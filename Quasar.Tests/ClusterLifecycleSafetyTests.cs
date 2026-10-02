using Admin = CometWorks.ClusterGateway.AdminContract.V1;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterLifecycleSafetyTests
{
    [Fact]
    public void DirectHostMutationsCannotBypassManagedDeployment()
    {
        var cluster = new Quasar.Models.ClusterDefinition();
        ClusterApi.EnsureDirectHostMutationAllowed(cluster);
        cluster.PendingDeploymentHash = "pending";
        Assert.Throws<ClusterOperationConflictException>(() => ClusterApi.EnsureDirectHostMutationAllowed(cluster));
        cluster.PendingDeploymentHash = null;
        cluster.PendingRestoreHash = "restore";
        Assert.Throws<ClusterOperationConflictException>(() => ClusterApi.EnsureDirectHostMutationAllowed(cluster));
        cluster.PendingRestoreHash = null;
        cluster.ActiveDeployment = new("revision", [], DateTimeOffset.UtcNow);
        Assert.Throws<ClusterOperationConflictException>(() => ClusterApi.EnsureDirectHostMutationAllowed(cluster));
    }

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

    [Fact]
    public void GatewayStopRejectsMarkerFromEarlierShutdown()
    {
        var status = Status(Admin.ClusterPhase.Down, hasMarker: true) with
        {
            LastCleanShutdown = DateTimeOffset.UnixEpoch,
            ShutdownStarted = DateTimeOffset.UnixEpoch.AddMinutes(1),
        };
        Assert.Throws<ClusterGatewayException>(() => ClusterApi.EnsureGatewayCanStop(status));
    }

    private static Admin.ClusterStatus Status(Admin.ClusterPhase phase, bool hasMarker) => new(
        "demo", "world", phase, Admin.StartupKind.Recovery, null,
        hasMarker ? DateTimeOffset.UtcNow : null, false, false, [],
        new Admin.ClusterCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        new Admin.WorldAuthorityStatus(null, 0, 0, DateTimeOffset.MinValue), [], [], false, Admin.AdminHealth.Healthy, [], DateTimeOffset.UtcNow);
}
