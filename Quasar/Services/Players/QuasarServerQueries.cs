using System.Globalization;
using System.Text.Json;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Quasar.Plugin.Abstractions.Servers;

namespace Quasar.Services.Players;

/// <summary>Where servers, players and grids are, for the player operations and UI plugins.</summary>
public sealed class QuasarServerQueries(
    AgentRegistry registry,
    ClusterCatalog? clusters = null,
    ClusterPlayerService? clusterPlayers = null
) : IQuasarServerQueries
{
    private static readonly TimeSpan AgentTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A standalone server, or a cluster as one target with its regular nodes.</summary>
    internal sealed record Target(
        string UniqueName,
        string DisplayName,
        bool IsCluster,
        IReadOnlyList<AgentRuntimeState> Agents
    );

    internal static bool IsWorldAuthority(AgentRuntimeState agent)
    {
        var role = agent.Snapshot?.ClusterNodeRole ?? agent.Hello?.ClusterNodeRole ?? string.Empty;
        return role.Equals("WA", StringComparison.OrdinalIgnoreCase)
            || role.Equals("WorldAuthority", StringComparison.OrdinalIgnoreCase);
    }

    internal IReadOnlyList<Target> Targets()
    {
        var agents = registry
            .GetAgents()
            .Where(agent => agent.IsConnected && agent.Snapshot is not null)
            .ToList();
        var standalone = agents
            .Where(agent => !agent.IsCluster)
            .GroupBy(agent => agent.UniqueNameKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new Target(
                group.Key,
                group.First().ServerDisplayName,
                false,
                group.Take(1).ToList()
            ));
        var clustered = agents
            .Where(agent =>
                agent.IsCluster && agent.ClusterId.Length > 0 && !IsWorldAuthority(agent)
            )
            .GroupBy(agent => agent.ClusterId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new Target(
                group.Key,
                clusters?.GetCluster(group.Key)?.DisplayName ?? group.Key,
                true,
                group.OrderBy(agent => agent.ClusterNodeId, StringComparer.Ordinal).ToList()
            ));
        return standalone
            .Concat(clustered)
            .OrderBy(target => target.UniqueName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal Target? Find(string server)
    {
        var name = server.Trim();
        if (name.StartsWith("cluster:", StringComparison.OrdinalIgnoreCase))
            name = name["cluster:".Length..];
        return Targets()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.UniqueName, name, StringComparison.OrdinalIgnoreCase)
            );
    }

    public IReadOnlyList<QuasarServerTarget> GetServers() =>
        Targets()
            .Select(target => new QuasarServerTarget(
                target.UniqueName,
                target.DisplayName,
                target.IsCluster,
                target
                    .Agents.Select(agent => new QuasarServerNode(
                        agent.AgentId,
                        target.IsCluster ? agent.ClusterNodeId : null,
                        agent
                            .Snapshot!.Plugins.Where(plugin => plugin.IsLoaded)
                            .Select(plugin => plugin.PluginId)
                            .ToList()
                    ))
                    .ToList()
            ))
            .ToList();

    public string? FindPlayer(string server, string steamId) =>
        Find(server) is { } target ? PlayerNode(target, steamId)?.AgentId : null;

    /// <summary>The player's node from the cluster's player list, else from the nodes' own player lists.</summary>
    internal AgentRuntimeState? PlayerNode(Target target, string steamId)
    {
        var node = target.IsCluster
            ? clusterPlayers
                ?.GetSnapshot(target.UniqueName)
                ?.Clients.FirstOrDefault(client =>
                    client.ClientId.ToString(CultureInfo.InvariantCulture) == steamId
                )
                ?.Node
            : null;
        return node is not null
            ? target.Agents.FirstOrDefault(agent => agent.ClusterNodeId == node)
            : target.Agents.FirstOrDefault(agent =>
                agent.Snapshot?.Players.Any(p =>
                    p.SteamId.ToString(CultureInfo.InvariantCulture) == steamId
                ) == true
            );
    }

    public async Task<IReadOnlyDictionary<long, string>> FindGridsAsync(
        string server,
        IEnumerable<long> gridIds,
        CancellationToken token
    )
    {
        var target =
            Find(server)
            ?? throw new InvalidOperationException($"Server '{server}' is not online.");
        var found = new Dictionary<long, string>();
        foreach (var gridId in gridIds.Distinct())
            if (await FindGridAsync(target, gridId, token) is { } grid)
                found[gridId] = grid.Node.AgentId;
        return found;
    }

    internal async Task<(AgentRuntimeState Node, EntitySummary Grid)?> FindGridAsync(
        Target target,
        long gridId,
        CancellationToken token
    )
    {
        var filter = JsonSerializer.Serialize(
            new EntityListFilter
            {
                TypeTag = "Grid",
                Search = gridId.ToString(CultureInfo.InvariantCulture),
                Limit = 50,
            },
            Json
        );
        var answers = await Task.WhenAll(
            target.Agents.Select(async node =>
            {
                var result = await CommandAsync(
                    node,
                    ServerCommandType.ListEntities,
                    filter,
                    token
                );
                if (!result.Success)
                    throw new InvalidOperationException(result.Message);
                var grid = JsonSerializer
                    .Deserialize<EntityListResult>(result.Payload, Json)
                    ?.Entities.FirstOrDefault(entity => entity.EntityId == gridId);
                return (Node: node, Grid: grid);
            })
        );
        // On a cluster a copy can exist on a node that does not own it; the owner's view wins.
        return answers
            .Where(answer => answer.Grid is not null)
            .OrderBy(answer => answer.Grid!.ClusterResidence is null or "Local" ? 0 : 1)
            .Select(answer => ((AgentRuntimeState, EntitySummary)?)(answer.Node, answer.Grid!))
            .FirstOrDefault();
    }

    public async Task<string?> FindPositionOwnerAsync(
        string server,
        double x,
        double y,
        double z,
        double radiusMeters,
        CancellationToken token,
        string? pluginId = null
    )
    {
        var target =
            Find(server)
            ?? throw new InvalidOperationException($"Server '{server}' is not online.");
        var nodes = target
            .Agents.Where(agent =>
                pluginId is null
                || agent.Snapshot?.Plugins.Any(plugin =>
                    plugin.IsLoaded
                    && string.Equals(plugin.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)
                ) == true
            )
            .ToList();
        var payload = JsonSerializer.Serialize(
            new PositionOwnerRequest
            {
                X = x,
                Y = y,
                Z = z,
                RadiusMeters = Math.Max(1, radiusMeters),
            },
            Json
        );
        var answers = await Task.WhenAll(
            nodes.Select(async node =>
            {
                try
                {
                    var result = await CommandAsync(
                        node,
                        ServerCommandType.ResolvePositionOwner,
                        payload,
                        token
                    );
                    return (
                        Node: node,
                        Owner: result.Success
                            ? JsonSerializer.Deserialize<PositionOwnerResult>(result.Payload, Json)
                            : null
                    );
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    return (node, null);
                }
            })
        );
        return Nearest(answers)?.AgentId;
    }

    internal static AgentRuntimeState? Nearest(
        IEnumerable<(AgentRuntimeState Node, PositionOwnerResult? Owner)> answers
    ) =>
        answers
            .Where(answer => answer.Owner?.Found == true)
            .OrderBy(answer => answer.Owner!.DistanceMeters)
            .Select(answer => answer.Node)
            .FirstOrDefault();

    private Task<ServerCommandResult> CommandAsync(
        AgentRuntimeState node,
        ServerCommandType type,
        string payload,
        CancellationToken token
    ) =>
        registry.SendCommandAndWaitAsync(
            new ServerCommandEnvelope
            {
                UniqueName = node.UniqueNameKey,
                AgentId = node.AgentId,
                ServerId = node.ServerKey,
                CommandType = type,
                Payload = payload,
            },
            AgentTimeout,
            token
        );
}
