using Magnetar.Protocol.Model;
using Quasar.Models;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace Quasar.Services;

internal sealed record FleetSummary(int ConfiguredServers, int RunningServers, int UnobservedClusters,
    int? PlayersOnline, int? PlayerCapacity, int ReportingServers, double? AverageSimSpeed,
    int SimulationProcesses, double? MemoryGiB, int MemoryProcesses, int HealthWarnings, int HealthErrors)
{
    internal static FleetSummary Build(IReadOnlyList<DedicatedServerDefinition> servers,
        IReadOnlyList<DedicatedServerRuntimeSnapshot> runtimes, IReadOnlyList<AgentRuntimeState> agents,
        IReadOnlyDictionary<string, ClusterPlayerSnapshot?> clusters)
    {
        int running = 0, unobserved = 0, players = 0, capacity = 0, reporting = 0;
        bool capacityKnown = true;
        int warnings = runtimes.Count(r => r.HealthState == DedicatedServerHealthState.Warning);
        int errors = runtimes.Count(r => r.HealthState == DedicatedServerHealthState.Unhealthy);
        var simulations = new List<double>();
        var memory = new List<long>();
        bool Fresh(AgentRuntimeState? agent) => agent is { IsConnected: true, Snapshot: not null }
            && DateTimeOffset.UtcNow - agent.LastSnapshotReceivedUtc < TimeSpan.FromSeconds(15);
        void AddMemory(ServerMetrics metrics)
        {
            if (metrics.MemoryWorkingSetMb is >= 0) memory.Add(metrics.MemoryWorkingSetMb.Value);
        }
        void AddSimulation(ServerMetrics metrics)
        {
            if (float.IsFinite(metrics.SimSpeed) && metrics.SimSpeed >= 0) simulations.Add(metrics.SimSpeed);
        }

        foreach (var server in servers)
        {
            var agent = agents.FirstOrDefault(a => !a.IsCluster && a.IsConnected
                && a.UniqueNameKey.Equals(server.UniqueName, StringComparison.OrdinalIgnoreCase));
            if (!Fresh(agent)) continue;
            var snapshot = agent!.Snapshot!;
            var metrics = snapshot.Metrics;
            reporting++;
            players += metrics.PlayersOnline;
            capacity += metrics.MaxPlayers;
            AddMemory(metrics);
            if (snapshot.IsRunning) AddSimulation(metrics);
            if (runtimes.Any(r => r.UniqueName.Equals(server.UniqueName, StringComparison.OrdinalIgnoreCase)
                && r.State == DedicatedServerProcessState.Running)) running++;
        }
        foreach (var (clusterId, observation) in clusters)
        {
            if (observation is not { StatusAvailable: true, Status: { } status }
                || !status.ClusterId.Equals(clusterId, StringComparison.OrdinalIgnoreCase)) { unobserved++; continue; }
            reporting++;
            players += status.Counts.ConnectedClients; // Registry owns cluster-wide presence; node counts overlap.
            if (status.Phase is Admin.ClusterPhase.Serving or Admin.ClusterPhase.Degraded) running++;
            if (status.Health == Admin.AdminHealth.Warning) warnings++;
            if (status.Health == Admin.AdminHealth.Unhealthy) errors++;
            var limits = new HashSet<int>();
            foreach (var node in status.Nodes.Where(n => n.LeaseFresh && n.State != Admin.NodeState.Dead)
                .DistinctBy(n => (n.SlotKey, n.Node, n.Epoch, n.Host)))
            {
                var agent = ClusterFleetService.MatchAgent(clusterId, node, agents);
                if (!Fresh(agent)) continue;
                var snapshot = agent!.Snapshot!;
                AddMemory(snapshot.Metrics); // Include World Authority and warm spares, once per current process.
                if (node.Role != Admin.NodeRole.Regular || node.Spare || snapshot.TelemetryPaused || !snapshot.IsRunning) continue;
                AddSimulation(snapshot.Metrics);
                if (snapshot.Metrics.MaxPlayers > 0) limits.Add(snapshot.Metrics.MaxPlayers);
            }
            // Every node shares the cluster's player limit. Never add it once per node.
            if (limits.Count == 1) capacity += limits.Single();
            else capacityKnown = false;
        }
        int configured = servers.Count + clusters.Count;
        return new(configured, running, unobserved, reporting > 0 || configured == 0 ? players : null,
            capacityKnown && (reporting > 0 || configured == 0) ? capacity : null, reporting,
            simulations.Count > 0 ? simulations.Average() : null, simulations.Count,
            memory.Count > 0 ? memory.Sum() / 1024d : null, memory.Count, warnings, errors);
    }
}
