using System.Globalization;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using static Quasar.Services.Plugins.QuasarCoreOperations;

namespace Quasar.Services.Plugins;

/// <summary>Cluster status and player reads. Cluster changes are in QuasarClusterControlOperations.</summary>
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

    /// <param name="AdmissionState">The Gateway's admission state of the connection, Admitted once in the game.</param>
    public sealed record ClusterPlayer(
        string SteamId,
        string Name,
        string AdmissionState,
        bool? IsAdmin,
        string? Node,
        DateTimeOffset ConnectedAt
    );

    /// <param name="Error">Why the player list is unavailable or stale; Online is then empty.</param>
    public sealed record ClusterPlayersResult(
        string UniqueName,
        string DisplayName,
        string? Error,
        IReadOnlyList<ClusterPlayer> Online,
        DateTimeOffset? ObservedAt
    );

    private static readonly QuasarOperationMetadata Read = new()
    {
        Effect = QuasarOperationEffect.Read,
    };

    public static void AddQuasarClusterOperations(this IServiceCollection services)
    {
        services.AddQuasarOperation<EmptyRequest, IReadOnlyList<ClusterItem>>(
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
        services.AddQuasarOperation<ClusterRequest, ClusterPlayersResult>(
            QuasarCoreOperations.PluginId,
            "cluster.players",
            [QuasarPolicyNames.ClusterQuery],
            (caller, input, _) => Task.FromResult(Players(caller, input)),
            Read with
            {
                Description =
                    "Lists a cluster's online players as the Players page shows them: Steam ID, name, admission state, admin flag, node and connect time. Collected from the Gateway every 5 seconds.",
            }
        );
    }

    private static ClusterPlayersResult Players(QuasarOperationContext caller, ClusterRequest input)
    {
        var name = input.UniqueName?.Trim() ?? string.Empty;
        var cluster = caller.Services.GetRequiredService<ClusterCatalog>().GetCluster(name);
        if (cluster is null || !caller.User.CanQueryCluster(cluster.UniqueName))
            throw new QuasarOperationException(404, $"Cluster '{name}' not found.");
        var snapshot = caller.Services.GetService<ClusterPlayerService>()?.GetSnapshot(cluster.UniqueName);
        var error = snapshot switch
        {
            null => "Players not collected yet, try again in a few seconds.",
            { Error: { } message } => message,
            { Available: false } => "The Gateway has not answered for over 15 seconds.",
            _ => null,
        };
        // A client without a name in the Gateway keeps the one Quasar saw last.
        var known = caller
            .Services.GetRequiredService<KnownPlayerCatalog>()
            .GetPlayers()
            .Where(record =>
                record.IsCluster
                && string.Equals(record.UniqueName, cluster.UniqueName, StringComparison.OrdinalIgnoreCase)
            )
            .GroupBy(record => (ulong)record.SteamId)
            .ToDictionary(group => group.Key, group => group.First().DisplayName);
        var online =
            error is null
                ? snapshot!
                    .Clients.Select(client => new ClusterPlayer(
                        client.ClientId.ToString(CultureInfo.InvariantCulture),
                        string.IsNullOrWhiteSpace(client.PlayerName)
                            ? known.GetValueOrDefault(client.ClientId, string.Empty)
                            : TextSanitizer.CleanGameText(client.PlayerName),
                        client.AdmissionState,
                        client.IsAdmin,
                        client.Node,
                        client.ConnectedAt
                    ))
                    .OrderBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];
        return new(cluster.UniqueName, cluster.DisplayName, error, online, snapshot?.ObservedAt);
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
