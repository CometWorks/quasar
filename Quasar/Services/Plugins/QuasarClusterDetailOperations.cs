using System.Text.Json;
using Quasar.Client.Clusters;
using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using Quasar.Services.PluginSdk;

namespace Quasar.Services.Plugins;

internal static partial class QuasarClusterDetailOperations
{
    private const string Plugin = QuasarCoreOperations.PluginId;
    private static readonly string[] Query = [QuasarPolicyNames.ClusterQuery];
    private static readonly string[] Manage = [QuasarPolicyNames.ClusterQuery, QuasarPolicyNames.ClusterManage];
    private static readonly QuasarOperationMetadata Change = new() { RequireDefaultPolicies = true, AgentAccess = QuasarAgentAccess.Denied };
    private static readonly QuasarOperationMetadata Read = Change with { Effect = QuasarOperationEffect.Read };
    internal sealed record RemoveInput(string UniqueName, bool Forget = false, string? Confirmation = null);
    internal static void AddQuasarClusterDetail(this IServiceCollection services)
    {
        services.AddClusterPartitions();
        services.AddClusterSettings();
        services.AddClusterContent();
        services.AddClusterRuntime();
        services.AddQuasarRead<ClusterPanelRequest>(Plugin, "cluster.console", Query, async (caller, input, token) =>
        {
            var cluster = Cluster(caller, input.UniqueName);
            var gateway = caller.Services.GetRequiredService<ClusterGatewayClient>();
            try
            {
                var page = await gateway.GetEventsAsync(cluster, input.Arguments<long>(), 200, token);
                var fleet = await caller.Services.GetRequiredService<ClusterFleetService>().GetAsync(cluster, token);
                var logs = caller.Services.GetRequiredService<PluginLogStream>();
                var rows = fleet.Data.Nodes.Where(n => n.Telemetry is not null)
                    .SelectMany(n => logs.GetEntries(n.Telemetry!.TelemetryKey).TakeLast(200).Select(entry =>
                        new ClusterLog(n.Registry.Node, entry.TimestampUtc, entry.Plugin, entry.Level.ToString(), entry.Message, entry.Exception)))
                    .OrderByDescending(item => item.TimestampUtc).Take(1000).ToArray();
                return JsonRead(new ClusterConsoleSnapshot(page.Data.Events.Select(e => new ClusterEvent(e.Seq, e.At, e.Type, e.Actor, e.Target,
                    e.Data is { Count: > 0 } ? JsonSerializer.Serialize(e.Data) : null)).ToArray(),
                    page.Data.NextCursor, page.Data.EarliestRetained, page.CapturedAt, rows));
            }
            catch (ClusterGatewayException error) when (error.IsConnectivityFailure)
            {
                return JsonRead(new ClusterConsoleSnapshot([], 0, 0, DateTimeOffset.UtcNow, [], error.Message));
            }
        }, Read with { Description = "Reads the selected cluster's console output." });
        services.AddQuasarOperation<RemoveInput, bool>(Plugin, "cluster.registration.remove", Manage, async (caller, input, token) =>
        {
            var cluster = Cluster(caller, input.UniqueName);
            var deployments = caller.Services.GetRequiredService<ClusterDeploymentService>();
            if (input.Forget) await deployments.ForgetAsync(cluster.UniqueName, input.Confirmation ?? "", token);
            else await deployments.DeleteAsync(cluster.UniqueName, token);
            return true;
        }, Change with { Description = "Removes the selected cluster registration after confirmation.", Deadline = QuasarOperationMetadata.MaximumDeadline });
    }

    private static ClusterDefinition Cluster(QuasarOperationContext caller, string uniqueName)
    {
        if (string.IsNullOrWhiteSpace(uniqueName) || !caller.User.CanQueryCluster(uniqueName))
            throw new QuasarOperationException(404, "Cluster not found.");
        return caller.Services.GetRequiredService<ClusterCatalog>().GetCluster(uniqueName) ?? throw new QuasarOperationException(404, "Cluster not found.");
    }
}
