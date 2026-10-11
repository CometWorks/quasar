using System.Text.Json;
using Quasar.Services.ServerList;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;
using Xunit;

namespace Quasar.Tests;

public sealed class ServerListClusterTests
{
    [Fact]
    public void ClusterProjectionUsesFreshLeasesAndOmitsPrivateTopology()
    {
        var now = DateTimeOffset.UtcNow;
        Admin.NodeStatus Node(string id, Admin.NodeState state, bool fresh, DateTimeOffset lease) => new(id,
            "PRIVATE-SLOT", 1, Admin.NodeRole.Regular, state, "PRIVATE-ENDPOINT", "PRIVATE-CONTROL", now,
            lease, 100, 2, "PRIVATE-HOST", fresh);
        var status = new Admin.ClusterStatus("PRIVATE-CLUSTER", "PRIVATE-WORLD", Admin.ClusterPhase.Serving,
            Admin.StartupKind.Warm, null, null, false, false, ["PRIVATE-REASON"],
            new(12, 4, 20, 0, 3, 0, 0, 0, 0, 0, 0, 0), new("PRIVATE-AUTHORITY", 1, 1, now.AddMinutes(1)),
            [Node("PRIVATE-NODE-A", Admin.NodeState.Active, true, now.AddMinutes(1)),
             Node("PRIVATE-NODE-B", Admin.NodeState.Active, false, now.AddMinutes(1)),
             Node("PRIVATE-NODE-C", Admin.NodeState.Active, true, now),
             Node("PRIVATE-NODE-D", Admin.NodeState.Dead, true, now.AddMinutes(1))], [], true,
            Admin.AdminHealth.Warning, ["PRIVATE-REASON"], now);
        var entry = new ServerListEntry { ListingId = new string('a', 32), Name = "Public", PublicHost = "play.example.org", PublicPort = 27016 };
        var listing = ServerListProtocol.FromCluster(entry, status, now);
        Assert.Equal(1, listing.Cluster!.Online); Assert.Equal(4, listing.Cluster.Total);
        Assert.Equal(12, listing.PlayersOnline); Assert.Equal(20, listing.Cluster.Partitions);
        Assert.Equal(3, listing.Cluster.Handovers); Assert.Equal("Warning", listing.Cluster.Health);
        Assert.Equal("Node 1", listing.Cluster.Nodes![0].Name);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(listing));
        foreach (var observed in new[] { now.AddSeconds(-61), now.AddSeconds(1) })
        {
            var unknown = ServerListProtocol.FromCluster(entry, status with { ObservedAt = observed }, now);
            Assert.Null(unknown.Running); Assert.Null(unknown.PlayersOnline);
            Assert.Equal("CometWorks", unknown.Cluster!.Kind); Assert.Null(unknown.Cluster.Online);
            Assert.Null(unknown.Cluster.Nodes); Assert.Null(unknown.Cluster.Health);
        }
    }
}
