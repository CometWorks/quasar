using System.Text.Json;
using Magnetar.Protocol.Model;
using Quasar.Models;
using Quasar.Services;
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
        var listing = ServerListProtocol.FromCluster(entry, status, new(), [], now);
        Assert.Equal(1, listing.Cluster!.Online); Assert.Equal(4, listing.Cluster.Total);
        Assert.Equal(12, listing.PlayersOnline); Assert.Equal(20, listing.Cluster.Partitions);
        Assert.Equal(3, listing.Cluster.Handovers); Assert.Equal("Warning", listing.Cluster.Health);
        Assert.Equal("Node 1", listing.Cluster.Nodes![0].Name);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(listing));
        foreach (var observed in new[] { now.AddSeconds(-61), now.AddSeconds(1) })
        {
            var unknown = ServerListProtocol.FromCluster(entry, status with { ObservedAt = observed }, new(), [], now);
            Assert.Null(unknown.Running); Assert.Null(unknown.PlayersOnline);
            Assert.Null(unknown.MaxPlayers); Assert.Null(unknown.SimSpeed);
            Assert.Equal("CometWorks", unknown.Cluster!.Kind); Assert.Null(unknown.Cluster.Online);
            Assert.Null(unknown.Cluster.Nodes); Assert.Null(unknown.Cluster.Health);
        }
    }

    [Fact]
    public void ClusterPublishesDeployedCapacityAndAverageActiveSimulationSpeed()
    {
        var now = DateTimeOffset.UtcNow;
        var first = Agent(now, "node-a", .6f);
        var second = Agent(now.AddSeconds(-10), "node-b", 1.2f);
        first.Snapshot!.Metrics.MaxPlayers = 16; // A node's capacity is not the Gateway's admission limit.
        second.Snapshot!.Metrics.PlayersOnline = 100; // The Registry alone counts cluster clients.
        var status = Status(now, Node(now, "node-a"), Node(now, "node-b"));
        var cluster = Cluster();
        cluster.Preparation = new("staged", """{"memberLimit":120}""", "", []);
        var listing = ServerListProtocol.FromCluster(new(), status, cluster, [first, second], now);
        Assert.Equal(80, listing.MaxPlayers);
        Assert.Equal(.8f, listing.SimSpeed);
        Assert.Equal(12, listing.PlayersOnline);
        Assert.Equal(now.AddSeconds(-10), listing.ObservedAtUtc);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(listing));

        var unknown = ServerListProtocol.FromCluster(new(), status with { ObservedAt = now.AddSeconds(-61) },
            cluster, [first, second], now);
        Assert.Null(unknown.MaxPlayers); Assert.Null(unknown.SimSpeed);
    }

    [Theory]
    [InlineData("world-authority")]
    [InlineData("spare")]
    [InlineData("idle")]
    [InlineData("dead")]
    [InlineData("closed")]
    [InlineData("empty")]
    [InlineData("stale-lease")]
    [InlineData("expired-lease")]
    [InlineData("disconnected")]
    [InlineData("stopped")]
    [InlineData("stale-snapshot")]
    [InlineData("future-snapshot")]
    [InlineData("stale-receipt")]
    [InlineData("future-receipt")]
    [InlineData("wrong-epoch")]
    [InlineData("wrong-node")]
    [InlineData("wrong-slot")]
    [InlineData("wrong-host")]
    [InlineData("wrong-cluster")]
    [InlineData("ambiguous")]
    [InlineData("missing")]
    [InlineData("negative")]
    [InlineData("nan")]
    [InlineData("infinity")]
    public void ClusterAverageExcludesIneligibleNodesAndReadings(string reason)
    {
        var now = DateTimeOffset.UtcNow;
        var node = Node(now);
        var agent = Agent(now);
        var snapshot = agent.Snapshot!;
        switch (reason)
        {
            case "world-authority": node = node with { Role = Admin.NodeRole.WorldAuthority }; break;
            case "spare": node = node with { Spare = true }; break;
            case "idle": snapshot.TelemetryPaused = true; break;
            case "dead": node = node with { State = Admin.NodeState.Dead }; break;
            case "closed": node = node with { State = Admin.NodeState.Closed }; break;
            case "empty": node = node with { State = Admin.NodeState.Empty }; break;
            case "stale-lease": node = node with { LeaseFresh = false }; break;
            case "expired-lease": node = node with { LeaseExpires = now }; break;
            case "disconnected": agent.IsConnected = false; break;
            case "stopped": snapshot.IsRunning = false; break;
            case "stale-snapshot": snapshot.CapturedAtUtc = now.AddSeconds(-61); break;
            case "future-snapshot": snapshot.CapturedAtUtc = now.AddSeconds(1); break;
            case "stale-receipt": agent.LastSnapshotReceivedUtc = now.AddSeconds(-61); break;
            case "future-receipt": agent.LastSnapshotReceivedUtc = now.AddSeconds(1); break;
            case "wrong-epoch": snapshot.ClusterEpoch++; break;
            case "wrong-node": snapshot.ClusterNodeId = "old-node"; break;
            case "wrong-slot": snapshot.ClusterSlot = "other-slot"; break;
            case "wrong-host": snapshot.HostId = "other-host"; break;
            case "wrong-cluster": snapshot.ClusterId = "other-cluster"; break;
            case "negative": snapshot.Metrics.SimSpeed = -.1f; break;
            case "nan": snapshot.Metrics.SimSpeed = float.NaN; break;
            case "infinity": snapshot.Metrics.SimSpeed = float.PositiveInfinity; break;
        }
        AgentRuntimeState[] agents = reason == "missing" ? [] : reason == "ambiguous" ? [agent, agent] : [agent];
        var status = Status(now, node);
        Assert.Null(ServerListProtocol.FromCluster(new(), status, Cluster(), agents, now).SimSpeed);

        // An excluded reading must not lower a healthy active node's average.
        var good = Agent(now, "good", 1f);
        var listing = ServerListProtocol.FromCluster(new(), status with { Nodes = [node, Node(now, "good")] },
            Cluster(), [.. agents, good], now);
        Assert.Equal(1f, listing.SimSpeed);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("broken json")]
    [InlineData("""{"memberLimit":null}""")]
    [InlineData("""{"memberLimit":"80"}""")]
    [InlineData("""{"memberLimit":1}""")]
    [InlineData("""{"memberLimit":-1}""")]
    [InlineData("""{"memberLimit":80.5}""")]
    [InlineData("""{"memberLimit":2147483648}""")]
    public void InvalidDeployedCapacityStaysUnknown(string specification)
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Null(ServerListProtocol.FromCluster(new(), Status(now), Cluster(specification), [], now).MaxPlayers);
    }

    [Fact]
    public void CapacityCannotComeFromStagingOrADifferentDeployment()
    {
        var now = DateTimeOffset.UtcNow;
        var cluster = Cluster();
        var status = Status(now);
        Assert.Null(ServerListProtocol.FromCluster(new(), status with { DeploymentRevision = "other" }, cluster, [], now).MaxPlayers);
        cluster.ActiveDeployment = cluster.ActiveDeployment! with { Provenance = null };
        Assert.Null(ServerListProtocol.FromCluster(new(), status, cluster, [], now).MaxPlayers);
        cluster.ActiveDeployment = null;
        Assert.Null(ServerListProtocol.FromCluster(new(), status, cluster, [], now).MaxPlayers);
    }

    private static ClusterDefinition Cluster(string specification = """{"memberLimit":80}""")
    {
        var preparation = new ClusterPreparationRequest("hash", specification, "", []);
        return new() { UniqueName = "PRIVATE-CLUSTER", Preparation = preparation,
            ActiveDeployment = new("active", [], DateTimeOffset.UtcNow) { Provenance = new(null!, "hash", preparation) } };
    }

    private static Admin.ClusterStatus Status(DateTimeOffset now, params Admin.NodeStatus[] nodes) => new(
        "PRIVATE-CLUSTER", "PRIVATE-WORLD", Admin.ClusterPhase.Serving, Admin.StartupKind.Warm,
        null, null, false, false, [], new(12, nodes.Length, 20, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        new(null, 1, 1, now.AddMinutes(1)), nodes, [], true, Admin.AdminHealth.Healthy, [], now, "active");

    private static Admin.NodeStatus Node(DateTimeOffset now, string id = "node-a") => new(id,
        "PRIVATE-SLOT", 1, Admin.NodeRole.Regular, Admin.NodeState.Active, "PRIVATE-ENDPOINT", null,
        now, now.AddMinutes(1), 100, 0, "PRIVATE-HOST", true);

    private static AgentRuntimeState Agent(DateTimeOffset now, string id = "node-a", float speed = .2f) => new()
    {
        IsConnected = true, LastSnapshotReceivedUtc = now,
        Snapshot = new() { ClusterMode = true, ClusterId = "PRIVATE-CLUSTER", ClusterNodeId = id,
            ClusterSlot = "PRIVATE-SLOT", ClusterEpoch = 1, HostId = "PRIVATE-HOST", CapturedAtUtc = now,
            IsRunning = true, Metrics = new() { SimSpeed = speed } },
    };
}
