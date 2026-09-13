using Magnetar.Protocol.Model;
using Quasar.Models;
using Quasar.Services.PluginSdk;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace Quasar.Services;

public sealed class ClusterFleetService(ClusterGatewayClient gateway, AgentRegistry agents, PluginLogStream logs)
{
    public async Task<Admin.AdminEnvelope<ClusterFleet>> GetAsync(ClusterDefinition cluster, CancellationToken token)
    {
        // One Registry snapshot supplies both the cluster identity and current incarnations.
        var status = await gateway.GetStatusAsync(cluster, token);
        var observations = agents.GetAgents();
        var nodes = status.Data.Nodes.Select(node =>
        {
            var agent = MatchAgent(status.Data.ClusterId, node, observations);
            var snapshot = agent?.Snapshot;
            var telemetry = agent is null ? null : new ClusterNodeTelemetry(agent.AgentId, agent.TelemetryKey,
                agent.IsConnected && snapshot is not null && DateTimeOffset.UtcNow - agent.LastSnapshotReceivedUtc < TimeSpan.FromSeconds(15),
                snapshot?.CapturedAtUtc, snapshot?.Metrics, snapshot?.Profiler, snapshot?.PluginStats,
                snapshot?.Plugins.ToArray() ?? [], logs.GetEntries(agent.TelemetryKey).TakeLast(25).ToArray());
            return new ClusterFleetNode(node, telemetry);
        }).ToArray();
        return new(status.ProtocolVersion, status.CapturedAt, new(status.Data.ClusterId, nodes));
    }

    public static AgentRuntimeState? MatchAgent(string clusterId, Admin.NodeStatus node, IReadOnlyList<AgentRuntimeState> agents)
    {
        var matches = agents.Where(a => a.HasClusterIdentity && a.ClusterId == clusterId
            && a.ClusterSlot == node.SlotKey && a.ClusterNodeId == node.Node && a.ClusterEpoch == node.Epoch).ToArray();
        // Multiple claimants are ambiguous, even if one arrived more recently.
        return matches.Length == 1 ? matches[0] : null;
    }
}

public sealed record ClusterFleet(string ClusterId, ClusterFleetNode[] Nodes);
public sealed record ClusterFleetNode(Admin.NodeStatus Registry, ClusterNodeTelemetry? Telemetry);
public sealed record ClusterNodeTelemetry(string AgentId, string TelemetryKey, bool Connected,
    DateTimeOffset? CapturedAt, ServerMetrics? Metrics, ProfilerSnapshot? Profiler,
    PluginStatsSnapshot? PluginStats, PluginRuntimeInfo[] Plugins, PluginLogEntry[] Logs);
