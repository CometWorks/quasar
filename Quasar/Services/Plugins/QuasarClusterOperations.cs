using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;

namespace Quasar.Services.Plugins;

/// <summary>Cluster status reads. Cluster changes are not operations yet.</summary>
public static class QuasarClusterOperations
{
    public sealed record ClusterRequest(string UniqueName = "");

    public sealed record ClusterItem(
        string UniqueName,
        string DisplayName,
        string Goal,
        string ConfigProfileId
    );

    public sealed record ClusterNode(
        string Node,
        string? Slot,
        string Role,
        string State,
        bool LeaseFresh,
        bool Spare,
        string? Host,
        int QuarantinedPartitions
    );

    /// <param name="GatewayError">Why the Gateway status could not be read; the status fields are then null.</param>
    public sealed record ClusterStatusResult(
        string UniqueName,
        string DisplayName,
        string Goal,
        string? GatewayError,
        string? Phase,
        string? Health,
        IReadOnlyList<string>? ReasonCodes,
        IReadOnlyList<string>? DegradedReasons,
        bool? AcceptingPlayers,
        int? PlayersOnline,
        int? Partitions,
        IReadOnlyList<ClusterNode>? Nodes,
        DateTimeOffset? ObservedAt
    );

    private static readonly QuasarOperationMetadata Read = new()
    {
        Effect = QuasarOperationEffect.Read,
    };

    public static void AddQuasarClusterOperations(this IServiceCollection services)
    {
        services.AddQuasarOperation<ClusterRequest, IReadOnlyList<ClusterItem>>(
            QuasarCoreOperations.PluginId,
            "cluster.list",
            [QuasarPolicyNames.ClusterQuery],
            (caller, _, _) =>
                Task.FromResult<IReadOnlyList<ClusterItem>>(
                    caller
                        .Services.GetRequiredService<ClusterCatalog>()
                        .GetClusters()
                        .Where(cluster => caller.User.CanQueryCluster(cluster.UniqueName))
                        .Select(cluster => new ClusterItem(
                            cluster.UniqueName,
                            cluster.DisplayName,
                            cluster.GoalState.ToString(),
                            cluster.ConfigProfileId
                        ))
                        .ToList()
                ),
            Read with
            {
                Description = "Lists the registered clusters with their goal and config profile.",
            }
        );
        services.AddQuasarOperation<ClusterRequest, ClusterStatusResult>(
            QuasarCoreOperations.PluginId,
            "cluster.status",
            [QuasarPolicyNames.ClusterQuery],
            StatusAsync,
            Read with
            {
                Description =
                    "Returns a cluster's status from its Gateway: phase, health and reasons, whether it accepts players, players online, partition count and nodes.",
            }
        );
    }

    private static async Task<ClusterStatusResult> StatusAsync(
        QuasarOperationContext caller,
        ClusterRequest input,
        CancellationToken token
    )
    {
        var name = input.UniqueName?.Trim() ?? string.Empty;
        var cluster = caller.Services.GetRequiredService<ClusterCatalog>().GetCluster(name);
        if (cluster is null || !caller.User.CanQueryCluster(cluster.UniqueName))
            throw new QuasarOperationException(404, $"Cluster '{name}' not found.");
        try
        {
            var envelope = await caller
                .Services.GetRequiredService<ClusterGatewayClient>()
                .GetStatusAsync(cluster, token);
            var status = envelope.Data;
            return new(
                cluster.UniqueName,
                cluster.DisplayName,
                cluster.GoalState.ToString(),
                null,
                status.Phase.ToString(),
                status.Health.ToString(),
                status.ReasonCodes,
                status.DegradedReasons,
                status.AcceptingPlayers,
                status.Counts.ConnectedClients,
                status.Counts.Partitions,
                status
                    .Nodes.Select(node => new ClusterNode(
                        node.Node,
                        node.SlotKey,
                        node.Role.ToString(),
                        node.State.ToString(),
                        node.LeaseFresh,
                        node.Spare,
                        node.Host,
                        node.QuarantinedPartitions
                    ))
                    .ToList(),
                envelope.CapturedAt
            );
        }
        catch (ClusterGatewayException error)
        {
            return new(
                cluster.UniqueName,
                cluster.DisplayName,
                cluster.GoalState.ToString(),
                error.Message,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null
            );
        }
    }
}
