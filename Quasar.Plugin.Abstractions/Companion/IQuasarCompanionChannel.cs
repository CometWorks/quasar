namespace Quasar.Plugin.Abstractions.Companion;

/// <summary>
/// Sends typed requests from a Quasar UI plugin to a companion Magnetar plugin
/// running inside a connected Dedicated Server.
/// </summary>
public interface IQuasarCompanionChannel
{
    /// <summary>Stable identities for background companion collection, independent of browser sessions.</summary>
    IReadOnlyList<QuasarCompanionServer> GetConnectedServers() => Array.Empty<QuasarCompanionServer>();

    /// <param name="serverId">
    /// Use ServerId returned by GetConnectedServers or the live AgentId.
    /// Standalone servers also accept their server key or unique name. Cluster
    /// ServerId identifies one node incarnation, not a logical whole cluster.
    /// </param>
    Task<TResponse> SendAsync<TRequest, TResponse>(
        string serverId,
        string companionPluginId,
        string operation,
        TRequest request,
        CancellationToken cancellationToken);
}

public sealed record QuasarCompanionServer(string ServerId, string AgentId, string DisplayName)
{
    /// <summary>Logical cluster identity for grouping; never a companion request target.</summary>
    public string ClusterId { get; init; } = string.Empty;
    public string ClusterDisplayName { get; init; } = string.Empty;
    /// <summary>Stable slot within the cluster, shared by replacement node incarnations.</summary>
    public string ClusterSlot { get; init; } = string.Empty;
}
