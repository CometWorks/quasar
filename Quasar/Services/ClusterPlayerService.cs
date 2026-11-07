using System.Collections.Concurrent;
using Quasar.Models;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace Quasar.Services;

/// <summary>Collects cluster-wide presence and player history without requiring an open UI.</summary>
public sealed class ClusterPlayerService(ClusterCatalog catalog, ClusterGatewayClient gateway,
    KnownPlayerCatalog knownPlayers, ILogger<ClusterPlayerService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, ClusterPlayerSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    public event Action? Changed;

    public ClusterPlayerSnapshot? GetSnapshot(string clusterId) =>
        _snapshots.TryGetValue(clusterId, out var snapshot) ? snapshot : null;

    internal async Task RefreshAsync(ClusterDefinition cluster, CancellationToken token)
    {
        var observedAt = DateTimeOffset.UtcNow;
        ClusterPlayerSnapshot snapshot;
        Admin.ClusterStatus? status = null;
        try
        {
            // Verify the registered endpoint's identity before attributing its clients to this cluster.
            status = (await gateway.GetStatusAsync(cluster, token)).Data;
            var clients = await gateway.GetClientsAsync(cluster, token);
            var bans = await gateway.GetBansAsync(cluster, token);
            var capabilities = await gateway.GetCapabilitiesAsync(cluster, token);
            Admin.PlayerModerationStatus? moderation = null;
            if (capabilities.Data.Operations.Contains("GET /admin/v1/players/moderation", StringComparer.Ordinal))
                moderation = (await gateway.GetPlayerModerationAsync(cluster, token)).Data;
            knownPlayers.ObserveClusterPlayers(cluster, clients.Data, bans.Data.Banned, clients.CapturedAt, moderation);
            snapshot = new(clients.Data.DistinctBy(client => client.ClientId).ToArray(), capabilities.Data,
                observedAt, null, status, moderation);
        }
        catch (ClusterGatewayException error)
        {
            // A failed moderation/history query must not discard an independently observed status.
            snapshot = new([], null, observedAt, error.Message, status);
        }
        _snapshots[cluster.UniqueName] = snapshot;
        Changed?.Invoke();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            do
            {
                await Task.WhenAll(catalog.GetClusters().Select(async cluster =>
                {
                    try { await RefreshAsync(cluster, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception error) { logger.LogWarning(error, "Cannot collect players for cluster {Cluster}.", cluster.UniqueName); }
                }));
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

public sealed record ClusterPlayerSnapshot(Admin.ClientSummary[] Clients, Admin.AdminCapabilities? Capabilities,
    DateTimeOffset ObservedAt, string? Error, Admin.ClusterStatus? Status = null, Admin.PlayerModerationStatus? Moderation = null)
{
    public bool StatusAvailable => Status is { Counts: not null, Nodes: not null }
        && DateTimeOffset.UtcNow - ObservedAt < TimeSpan.FromSeconds(15);
    public bool Available => Error is null && DateTimeOffset.UtcNow - ObservedAt < TimeSpan.FromSeconds(15);
    public bool CanRun(string operation) => Available && Capabilities?.Scope == Admin.AdminScope.Manage
        && Capabilities.Operations.Contains(operation, StringComparer.Ordinal);

    /// <summary>The Gateway admin operation a cluster player action needs.</summary>
    public static string GatewayOperation(string action) => action switch
    {
        "kick" => "POST /admin/v1/clients/{id}/kick",
        "ban" => "POST /admin/v1/admission/bans",
        "unban" => "DELETE /admin/v1/admission/bans/{userId}",
        "set-player-promotion" => "PUT /admin/v1/players/{userId}/promotion",
        "clear-kick-cooldown" => "DELETE /admin/v1/admission/kicks/{userId}",
        _ => string.Empty,
    };

    /// <summary>Why no player action can run on the cluster now; null when some can.</summary>
    public static string? ModerationUnavailableReason(ClusterPlayerSnapshot? snapshot) => snapshot switch
    {
        null => "Waiting for cluster player status.",
        { Error: { } error } => error,
        { Available: false } => "Cluster player status is stale. Wait for a fresh Gateway connection.",
        { Capabilities.Scope: not Admin.AdminScope.Manage } => "The cluster Gateway credential needs management scope for player actions.",
        _ when snapshot.Capabilities is null => "Cluster Gateway capabilities are unavailable.",
        _ when !snapshot.CanRun(GatewayOperation("kick")) && !snapshot.CanRun(GatewayOperation("ban"))
            && !snapshot.CanRun(GatewayOperation("unban")) => "This cluster Gateway does not support player actions.",
        _ => null,
    };
}
