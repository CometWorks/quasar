using System.Collections.Concurrent;
using System.Text.Json;
using Quasar.Client.Clusters;
using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;
using HostContract = Quasar.Host.Contract.V1;

namespace Quasar.Services.Plugins;

internal static partial class QuasarClusterDetailOperations
{
    private static void AddClusterRuntime(this IServiceCollection services)
    {
        RegisterRead(services, "cluster.panel.events", "Reads a page of recent gateway events after the given cursor.", Query, (g, c, i, t) => g.GetEventsAsync(c, i.Arguments<long>(), 100, t));
        services.AddQuasarRead<ClusterPanelRequest>(Plugin, "cluster.runtime", Query, async (caller, input, token) =>
        {
            var cluster = Cluster(caller, input.UniqueName);
            var gateway = caller.Services.GetRequiredService<ClusterGatewayClient>();
            var errors = new ConcurrentQueue<string>();
            async Task<Admin.AdminEnvelope<T>?> Observe<T>(Task<Admin.AdminEnvelope<T>> task)
            {
                try { return await task; }
                catch (ClusterGatewayException e)
                {
                    if (cluster.GoalState != DedicatedServerGoalState.Off || !e.IsConnectivityFailure) errors.Enqueue(e.Message);
                    return null;
                }
            }
            async Task<HostContract.HostStatus?> ReadHost()
            {
                if (string.IsNullOrWhiteSpace(cluster.HostCommandUrl)) return null;
                try { return (await caller.Services.GetRequiredService<ClusterHostClient>().GetStatusAsync(cluster, token)).Data; }
                catch (ClusterHostException e) { errors.Enqueue(e.Message); return null; }
            }

            var caps = Observe(gateway.GetCapabilitiesAsync(cluster, token));
            var status = Observe(gateway.GetStatusAsync(cluster, token));
            var plan = Observe(gateway.GetPlanAsync(cluster, token));
            var recovery = Observe(gateway.GetRecoveryReadinessAsync(cluster, token));
            var clients = Observe(gateway.GetClientsAsync(cluster, token));
            var partitions = Observe(gateway.GetPartitionsAsync(cluster, token));
            var host = ReadHost();
            await Task.WhenAll(caps, status, plan, recovery, clients, partitions, host);
            var state = (await status)?.Data;
            var machine = await host;
            var process = machine?.Gateways?.FirstOrDefault(g => g.ClusterId.Equals(cluster.UniqueName, StringComparison.OrdinalIgnoreCase));
            var reconcile = caller.Services.GetRequiredService<ClusterReconciler>().GetStatus(cluster.UniqueName);
            var planned = (await plan)?.Data ?? [];
            var fleet = state is null ? [] : caller.Services.GetRequiredService<ClusterFleetService>().Observe(state).Nodes;
            var nodes = ClusterOverview.Nodes(cluster, planned, fleet);
            var hosts = caller.Services.GetRequiredService<ClusterHostCatalog>().GetAll();
            var endpoints = ClusterOverview.Endpoints(cluster, nodes, hosts).Select(e => new ServiceEndpoint(e.Process, e.Service, e.Address, e.Bind)).ToArray();
            var hostIds = nodes.Select(n => n.Host).OfType<string>().Concat(cluster.ActiveDeployment?.Hosts.Select(h => h.HostId) ?? []).ToHashSet();
            var runtime = new ClusterRuntime(state is null ? null : state with { Nodes = [], Executors = [] }, (await status)?.CapturedAt,
                (await caps)?.Data, planned, (await recovery)?.Data, nodes.Select(ProjectNode).ToArray(), endpoints,
                hosts.Where(h => hostIds.Contains(h.Id)).Select(h => new HostLabel(h.Id, h.Name)).ToArray(), hostIds.Count > 1,
                (await clients)?.Data.DistinctBy(c => c.ClientId).Select(c => c.Node).ToArray(), (await partitions)?.Data.Where(p => !p.Retired).Select(p => p.Node).ToArray(),
                reconcile.State.ToString(), reconcile.Message, reconcile.ErrorCode, reconcile.GatewayOutput,
                cluster.GoalState == DedicatedServerGoalState.Off && reconcile.State == ClusterReconcileState.Converged && reconcile.ClusterPhase == Admin.ClusterPhase.Down,
                machine is null ? string.IsNullOrWhiteSpace(cluster.HostCommandUrl) ? "Not configured (observation only)" : "Unavailable"
                    : $"{machine.HostId} · {machine.Attachments.Length} configured {(machine.Attachments.Length == 1 ? "cluster" : "clusters")}",
                process is null ? "Not assigned" : $"{process.Observed} · PID {process.ProcessId?.ToString() ?? "-"} · {process.ConfigRevision}",
                process?.Observed.ToString(), process is { Observed: HostContract.GatewayObservedState.Running, Metrics: { } sample }
                    ? new(sample.CapturedAt, sample.CpuPercent, sample.MemoryWorkingSetBytes) : null, errors.IsEmpty ? null : string.Join(" ", errors.Distinct()));
            return JsonRead(runtime);
        }, Read with { Description = "Returns running nodes, gateway state and current runtime metrics." });
        services.AddQuasarRead<ClusterPanelRequest>(Plugin, "cluster.node.details", Query, async (caller, input, token) =>
        {
            var cluster = Cluster(caller, input.UniqueName);
            var node = input.Arguments<string>();
            var fleet = (await caller.Services.GetRequiredService<ClusterFleetService>().GetAsync(cluster, token)).Data;
            var t = fleet.Nodes.FirstOrDefault(n => n.Registry.Node == node)?.Telemetry ?? throw new QuasarOperationException(404, "Node telemetry unavailable.");
            return JsonRead(new PanelNodeDetails(t.AgentId, t.Plugins.Select(p => new NodePlugin(p.DisplayName, p.Version, p.IsLoaded)).ToArray(),
                JsonSerializer.Serialize(new { t.PluginStats, t.Profiler }, new JsonSerializerOptions { WriteIndented = true }),
                t.Logs.Select(l => new ClusterLog(node, l.TimestampUtc, l.Plugin, l.Level.ToString(), l.Message, null)).ToArray()));
        }, Read with { Description = "Returns runtime details for the selected node." });
    }
    private static PanelNode ProjectNode(ClusterOverview.Node node)
    {
        var t = node.Fleet?.Telemetry;
        var telemetry = t is null ? null : new PanelTelemetry(t.Connected, t.ReadinessVerified, t.DeploymentFailure,
            t.Metrics is { } m ? new(m.SimSpeed, m.ServerCpuLoadPercent, m.MemoryWorkingSetMb) : null);
        return new(node.Name, node.Host, node.Role, node.Spare, node.Plan, node.Fleet is { } f
            ? new(f.Registry with { Endpoint = "", ControlEndpoint = null }, telemetry, ClusterOverview.IsIdle(f)) : null);
    }
}
