using Magnetar.Protocol.Model;
using Quasar.Models;
using Quasar.Services;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;
using Xunit;

namespace Quasar.Tests;

public sealed class FleetSummaryTests
{
    [Fact]
    public void ActiveClusterCountsPlayersAndCapacityOnceAndUsesOnlyCurrentProcesses()
    {
        var (status, agents) = Sample();
        var summary = FleetSummary.Build([], [], agents, new Dictionary<string, ClusterPlayerSnapshot?>
            { ["fleet"] = Observation(status) });
        Assert.Equal(1, summary.ConfiguredServers);
        Assert.Equal(1, summary.RunningServers);
        Assert.Equal(7, summary.PlayersOnline);
        Assert.Equal(32, summary.PlayerCapacity);
        Assert.Equal(.9, summary.AverageSimSpeed!.Value, 5);
        Assert.Equal(2, summary.SimulationProcesses);
        Assert.Equal(6.5, summary.MemoryGiB);
        Assert.Equal(4, summary.MemoryProcesses);
    }

    [Fact]
    public void MixedFleetKeepsStandaloneAndClusterWithTheSameNameSeparate()
    {
        var (status, agents) = Sample();
        var standalone = new AgentRuntimeState { IsConnected = true, LastSnapshotReceivedUtc = DateTimeOffset.UtcNow,
            Snapshot = new() { UniqueName = "fleet", IsRunning = true,
                Metrics = new() { PlayersOnline = 2, MaxPlayers = 20, SimSpeed = .6f, MemoryWorkingSetMb = 1024 } } };
        var summary = FleetSummary.Build([new() { UniqueName = "fleet" }],
            [new() { UniqueName = "fleet", State = DedicatedServerProcessState.Running }],
            agents.Append(standalone).ToArray(), new Dictionary<string, ClusterPlayerSnapshot?> { ["fleet"] = Observation(status) });
        Assert.Equal(2, summary.ConfiguredServers);
        Assert.Equal(2, summary.RunningServers);
        Assert.Equal(9, summary.PlayersOnline);
        Assert.Equal(52, summary.PlayerCapacity);
        Assert.Equal(.8, summary.AverageSimSpeed!.Value, 5);
        Assert.Equal(7.5, summary.MemoryGiB);
    }

    [Fact]
    public void MissingOrStaleRegistryStatusDoesNotFabricateZeroPlayerOrResourceReadings()
    {
        var (status, agents) = Sample();
        foreach (var observation in new ClusterPlayerSnapshot?[] { null, Observation(status) with { ObservedAt = DateTimeOffset.UtcNow.AddMinutes(-1) } })
        {
            var summary = FleetSummary.Build([], [], agents, new Dictionary<string, ClusterPlayerSnapshot?> { ["fleet"] = observation });
            Assert.Equal(1, summary.UnobservedClusters);
            Assert.Null(summary.PlayersOnline);
            Assert.Null(summary.PlayerCapacity);
            Assert.Null(summary.AverageSimSpeed);
            Assert.Null(summary.MemoryGiB);
        }
    }

    [Fact]
    public void DuplicateClaimsAndStaleSnapshotsDoNotInflateTotals()
    {
        var (status, agents) = Sample();
        var duplicate = agents[0].Clone();
        duplicate.AgentId = "duplicate";
        agents[1].LastSnapshotReceivedUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var summary = FleetSummary.Build([], [], agents.Append(duplicate).ToArray(),
            new Dictionary<string, ClusterPlayerSnapshot?> { ["fleet"] = Observation(status) });
        Assert.Equal(7, summary.PlayersOnline); // Registry presence is independent of Agent coverage.
        Assert.Null(summary.AverageSimSpeed);
        Assert.Null(summary.PlayerCapacity);
        Assert.Equal(1.5, summary.MemoryGiB); // World Authority and the spare still report memory.
        Assert.Equal(2, summary.MemoryProcesses);
    }

    [Fact]
    public void CurrentRegistryHealthAndPlayersRemainUsefulWhenModerationQueriesFail()
    {
        var (status, agents) = Sample();
        var summary = FleetSummary.Build([], [], agents, new Dictionary<string, ClusterPlayerSnapshot?>
        {
            ["fleet"] = Observation(status with { Health = Admin.AdminHealth.Warning }) with { Error = "Moderation query unavailable" },
            ["unknown"] = null,
        });
        Assert.Equal(1, summary.RunningServers);
        Assert.Equal(1, summary.UnobservedClusters);
        Assert.Equal(1, summary.ReportingServers);
        Assert.Equal(7, summary.PlayersOnline);
        Assert.Equal(1, summary.HealthWarnings);
    }

    private static ClusterPlayerSnapshot Observation(Admin.ClusterStatus status) => new([], null, DateTimeOffset.UtcNow, null, status);

    internal static (Admin.ClusterStatus Status, AgentRuntimeState[] Agents) Sample()
    {
        Admin.NodeStatus Node(string name, Admin.NodeRole role = Admin.NodeRole.Regular, bool spare = false) =>
            new(name, name, 2, role, Admin.NodeState.Active, "127.0.0.1:28417", null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), 0, 0, "host", true, spare);
        var nodes = new[] { Node("node-1"), Node("node-2"), Node("world-authority", Admin.NodeRole.WorldAuthority), Node("spare", spare: true) };
        var agents = nodes.Select((node, i) => new AgentRuntimeState
        {
            AgentId = node.Node, IsConnected = true, LastSnapshotReceivedUtc = DateTimeOffset.UtcNow,
            Snapshot = new() { ClusterMode = true, ClusterId = "fleet", ClusterSlot = node.SlotKey!, ClusterNodeId = node.Node,
                ClusterEpoch = node.Epoch, HostId = node.Host!, IsRunning = true, TelemetryPaused = node.Spare,
                Metrics = new() { PlayersOnline = 7, MaxPlayers = 32, SimSpeed = new[] { .8f, 1f, .1f, .05f }[i],
                    MemoryWorkingSetMb = new long[] { 2048, 3072, 1024, 512 }[i] } },
        }).ToList();
        var old = agents[0].Clone();
        old.AgentId = "old";
        old.Snapshot = new() { ClusterMode = true, ClusterId = "fleet", ClusterSlot = nodes[0].SlotKey!,
            ClusterNodeId = nodes[0].Node, ClusterEpoch = 1, HostId = "host", IsRunning = true,
            Metrics = new() { MemoryWorkingSetMb = 999999, MaxPlayers = 32, PlayersOnline = 7, SimSpeed = .2f } };
        agents.Add(old);
        var status = ClusterHealthOverviewTests.Sample().Status with
        {
            ClusterId = "fleet", Nodes = nodes.Append(nodes[0]).ToArray(),
            Counts = ClusterHealthOverviewTests.Sample().Status.Counts with { ConnectedClients = 7 },
        };
        return (status, agents.ToArray());
    }
}
