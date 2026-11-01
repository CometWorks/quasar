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
            knownPlayers.ObserveClusterPlayers(cluster, clients.Data, bans.Data.Banned, clients.CapturedAt);
            snapshot = new(clients.Data.DistinctBy(client => client.ClientId).ToArray(), capabilities.Data,
                observedAt, null, status);
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
    DateTimeOffset ObservedAt, string? Error, Admin.ClusterStatus? Status = null)
{
    public bool StatusAvailable => Status is { Counts: not null, Nodes: not null }
        && DateTimeOffset.UtcNow - ObservedAt < TimeSpan.FromSeconds(15);
    public bool Available => Error is null && DateTimeOffset.UtcNow - ObservedAt < TimeSpan.FromSeconds(15);
    public bool CanRun(string operation) => Available && Capabilities?.Scope == Admin.AdminScope.Manage
        && Capabilities.Operations.Contains(operation, StringComparer.Ordinal);
}
