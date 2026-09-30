using CometWorks.ClusterGateway.AdminContract.V1;
using Quasar.Components.Dashboard;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterFleetPanelTests
{
    [Fact]
    public void NodeRowsKeepPlannedEmptySlotsAndUnmatchedRegistryNodes()
    {
        var current = new ClusterFleetNode(Node("node-a", "slot-a"), null);
        var unmatched = new ClusterFleetNode(Node("old-node", "slot-a"), null);

        var rows = ClusterFleetPanel.MergeNodes([Plan("slot-a", "node-a"), Plan("slot-b", null)],
            [current, unmatched]).ToArray();

        Assert.Equal(3, rows.Length);
        Assert.Same(current, rows.Single(row => row.Plan?.SlotKey == "slot-a").Fleet);
        Assert.Null(rows.Single(row => row.Plan?.SlotKey == "slot-b").Fleet);
        Assert.Null(rows.Single(row => ReferenceEquals(row.Fleet, unmatched)).Plan);
    }

    private static NodePlan Plan(string slot, string? observedNode) => new(
        slot, "host", NodeRole.Regular, NodeGoal.Wanted,
        observedNode is null ? NodeObservation.Missing : NodeObservation.Ready,
        observedNode, IncumbentAction.None, null, 0, null, null, null, null, 0, false, true);

    private static NodeStatus Node(string node, string slot) => new(
        node, slot, 1, NodeRole.Regular, NodeState.Active, "endpoint", null,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), 0, 0, "host", true);
}
