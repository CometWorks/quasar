namespace Quasar.Plugin.Abstractions.Servers;

/// <summary>
/// Read-only questions about the connected servers that a companion plugin can't answer alone, mostly where things
/// are on a cluster. Implemented by core; resolve it from DI. A server is named by its unique name; a cluster by its
/// unique name, with or without <c>cluster:</c>.
/// </summary>
public interface IQuasarServerQueries
{
    /// <summary>The online servers: each standalone server, and each cluster with its regular nodes (no World
    /// Authority), ordered by unique name.</summary>
    IReadOnlyList<QuasarServerTarget> GetServers();

    /// <summary>The agent ID of the node the player is on, null when they are not online there.</summary>
    string? FindPlayer(string server, string steamId);

    /// <summary>Which node has each grid now, as grid ID to agent ID. IDs no node has are left out. On a cluster the
    /// node that owns a grid wins over one that only has a copy.</summary>
    Task<IReadOnlyDictionary<long, string>> FindGridsAsync(
        string server,
        IEnumerable<long> gridIds,
        CancellationToken token
    );

    /// <summary>The agent ID of the node that owns the entities nearest the position within the radius, null when
    /// none does. On a standalone server that is the server itself when it has any entity there.</summary>
    Task<string?> FindPositionOwnerAsync(
        string server,
        double x,
        double y,
        double z,
        double radiusMeters,
        CancellationToken token
    );
}

/// <param name="UniqueName">The standalone server's or the cluster's unique name.</param>
public sealed record QuasarServerTarget(
    string UniqueName,
    string DisplayName,
    bool IsCluster,
    IReadOnlyList<QuasarServerNode> Nodes
);

/// <param name="AgentId">The node's live agent ID, a companion request target.</param>
/// <param name="ClusterNode">The cluster node ID; null on a standalone server.</param>
/// <param name="LoadedPlugins">The IDs of the Magnetar plugins loaded on the node.</param>
public sealed record QuasarServerNode(
    string AgentId,
    string? ClusterNode,
    IReadOnlyList<string> LoadedPlugins
)
{
    public bool HasPlugin(string pluginId) =>
        LoadedPlugins.Any(id => string.Equals(id, pluginId, StringComparison.OrdinalIgnoreCase));
}
