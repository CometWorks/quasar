using Quasar.Host.Contract.V1;
using Quasar.Models;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterStartEligibilityTests
{
    [Fact]
    public void FreshStoppedActivationCanStartButLiveOrUncertainGatewayCannot()
    {
        ClusterDefinition cluster = NewCluster();
        GatewayStatus stopped = Gateway(cluster, GatewayGoal.Off, GatewayObservedState.Missing);

        Assert.True(ClusterCommandService.CanStartFromHostState(cluster, Host(stopped)));
        Assert.False(ClusterCommandService.CanStartFromHostState(cluster,
            Host(Gateway(cluster, GatewayGoal.On, GatewayObservedState.Running))));
        Assert.False(ClusterCommandService.CanStartFromHostState(cluster,
            Host(Gateway(cluster, GatewayGoal.On, GatewayObservedState.Missing))));
        Assert.False(ClusterCommandService.CanStartFromHostState(cluster,
            Host(stopped with { Failure = "stop_failed" })));
        Assert.False(ClusterCommandService.CanStartFromHostState(cluster,
            Host(stopped, stopped)));
        cluster.Gateway = cluster.Gateway! with { StartGeneration = Guid.NewGuid() };
        Assert.False(ClusterCommandService.CanStartFromHostState(cluster,
            Host(Gateway(cluster, GatewayGoal.Off, GatewayObservedState.Missing))));
    }

    [Fact]
    public void LaterStartRequiresMatchingCleanStopProof()
    {
        ClusterDefinition cluster = NewCluster();
        cluster.Gateway = cluster.Gateway! with { StartGeneration = Guid.NewGuid() };
        var fence = new GatewayStopFence(42, DateTimeOffset.UnixEpoch);
        GatewayStatus stopped = Gateway(cluster, GatewayGoal.Off, GatewayObservedState.Missing)
            with { CompletedStopFence = fence };

        Assert.False(ClusterCommandService.CanStartFromHostState(cluster, Host(stopped)));
        cluster.ShutdownProof = new(cluster.GetLifecycleId(), DateTimeOffset.UnixEpoch, fence);
        Assert.True(ClusterCommandService.CanStartFromHostState(cluster, Host(stopped)));
        Assert.False(ClusterCommandService.CanStartFromHostState(cluster,
            Host(stopped with { CompletedStopFence = new GatewayStopFence(43, DateTimeOffset.UnixEpoch) })));
    }

    private static ClusterDefinition NewCluster()
    {
        var gateway = new GatewaySpec("demo", GatewayGoal.Off, "/bundle/manifest.json",
            new string('a', 64), "r1", [28000], "/runs/demo", StartGeneration: Guid.NewGuid());
        var attachment = new HostAttachmentSpec("demo", "http://gateway.test", "TOKEN");
        var deployment = new HostActiveDeployment("demo", "current", new string('a', 64), attachment, gateway);
        return new ClusterDefinition
        {
            UniqueName = "demo",
            Gateway = gateway,
            ActiveDeployment = new("current", [new("host", "http://host.test", "TOKEN", deployment)], DateTimeOffset.UnixEpoch),
        };
    }

    private static HostStatus Host(params GatewayStatus[] gateways) =>
        new("executor", "host", [], gateways, GatewayStopFencing: true);

    private static GatewayStatus Gateway(ClusterDefinition cluster, GatewayGoal goal, GatewayObservedState observed) => new(
        "demo", goal, observed, new string('a', 64), "r1", [28000], "/runs/demo",
        null, null, null, StartGeneration: cluster.Gateway!.StartGeneration);
}
