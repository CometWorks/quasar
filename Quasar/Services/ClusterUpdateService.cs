using Quasar.Models;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace Quasar.Services;

/// <summary>Durable full-downtime workflow; ordinary reconciliation owns shutdown and startup.</summary>
public sealed class ClusterUpdateService(ClusterCatalog catalog, ClusterDeploymentService deployments,
    ClusterGatewayClient gateway, ClusterHostClient hosts, ClusterOperationStore operations, ILogger<ClusterUpdateService> logger) : BackgroundService
{
    public Task<ClusterOperation> BeginAsync(string clusterId, ClusterUpdateRequest request, string key, string actor, CancellationToken token) =>
        operations.ExecuteAsync(clusterId, "cluster.update.begin", key, actor, request,
            async ct => new Admin.AdminEnvelope<ClusterUpdate>(Admin.AdminProtocol.Version, DateTimeOffset.UtcNow,
                await catalog.WithLifecycleAsync(clusterId, cluster => BeginCoreAsync(cluster, request, ct), ct)), token);

    private async Task<ClusterUpdate> BeginCoreAsync(ClusterDefinition cluster, ClusterUpdateRequest request, CancellationToken token)
    {
        if (cluster.Update is { } recorded && recorded.Id == request.Id)
        {
            if (recorded.Rollback != request.Rollback || !request.Rollback && System.Text.Json.JsonSerializer.Serialize(recorded.Deployment)
                != System.Text.Json.JsonSerializer.Serialize(request.Deployment))
                throw new InvalidOperationException("Update ID already identifies different inputs.");
            return recorded;
        }
        if (request.Id == Guid.Empty || cluster.ActiveDeployment is null || cluster.PendingDeploymentHash is not null || cluster.PendingRestoreHash is not null)
            throw new InvalidOperationException("Update requires an operation ID and a complete managed deployment.");
        if (cluster.Update is { Phase: not ClusterUpdatePhase.Complete }) throw new InvalidOperationException("An update is already in progress.");
        var candidate = request.Rollback ? RollbackDeployment(cluster) : request.Deployment
            ?? throw new InvalidDataException("Candidate deployment is required.");
        if (candidate.Revision == cluster.ActiveDeployment.Revision) throw new InvalidOperationException("Candidate must be a different deployment revision.");
        await deployments.ActivateCoreAsync(cluster, candidate, token, dryRun: true, preflight: true, update: true);
        var workflow = new ClusterUpdate(request.Id, candidate, cluster.ActiveDeployment, request.Rollback,
            ClusterUpdatePhase.Stopping, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await catalog.RecordUpdateAsync(cluster, workflow, token, DedicatedServerGoalState.Off);
        return workflow; // Accepted, not converged. Query Update.Phase until Complete.
    }

    private static ClusterDeploymentRequest RollbackDeployment(ClusterDefinition cluster)
    {
        var previous = cluster.PreviousDeployment ?? throw new InvalidOperationException("No previous installation is recorded.");
        return new(cluster.ActiveDeployment!.Revision, previous.Revision, previous.Hosts.Select(host => new ClusterHostActivation(
            host.HostId, host.CommandUrl, host.TokenEnvironmentVariable, new(cluster.UniqueName,
                cluster.ActiveDeployment.Hosts.Single(h => h.HostId == host.HostId).Deployment.BundleManifestSha256,
                host.Deployment.Attachment.BundleManifestPath!, host.Deployment.BundleManifestSha256,
                host.Deployment.Attachment.GatewayUrl, host.Deployment.Attachment.TokenEnvironmentVariable))).ToArray());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await AdvanceAllAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception error) { logger.LogError(error, "Cluster update reconciliation failed; persisted workflows will be retried."); }
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal async Task AdvanceAllAsync(CancellationToken token)
    {
        foreach (var snapshot in catalog.GetClusters().Where(c => c.Update is { Phase: not ClusterUpdatePhase.Complete }))
            await catalog.WithLifecycleAsync(snapshot.UniqueName, async cluster =>
            {
                if (cluster.Update is not { Phase: not ClusterUpdatePhase.Complete } workflow) return false;
                try
                {
                    switch (workflow.Phase)
                    {
                        case ClusterUpdatePhase.Stopping:
                            if (cluster.ShutdownProof?.LifecycleId != cluster.GetLifecycleId()) return false;
                            await deployments.ActivateCoreAsync(cluster, workflow.Deployment, token, dryRun: true, update: true);
                            await catalog.RecordUpdateAsync(cluster, workflow with { Phase = ClusterUpdatePhase.Activating,
                                LastError = null, UpdatedAt = DateTimeOffset.UtcNow }, token);
                            break;
                        case ClusterUpdatePhase.Activating:
                            await deployments.ActivateCoreAsync(cluster, workflow.Deployment, token, update: true);
                            cluster = catalog.GetCluster(cluster.UniqueName)!; // Activation changed lifecycle identity.
                            await catalog.RecordUpdateAsync(cluster, workflow with { Phase = ClusterUpdatePhase.Starting,
                                LastError = null, UpdatedAt = DateTimeOffset.UtcNow }, token, DedicatedServerGoalState.On);
                            break;
                        case ClusterUpdatePhase.Starting:
                            var observed = (await gateway.GetStatusAsync(cluster, token)).Data;
                            if (observed.ClusterId != cluster.UniqueName || observed.Phase != Admin.ClusterPhase.Serving || !observed.AcceptingPlayers
                                || observed.DeploymentRevision != workflow.Deployment.Revision || !observed.ManagedDeploymentReady) return false;
                            // Serving is runtime-gated; prove every Host still runs the exact activated revision too.
                            foreach (var host in cluster.ActiveDeployment!.Hosts)
                            {
                                var target = cluster.Clone(); target.HostCommandUrl = host.CommandUrl; target.HostCommandTokenEnvironmentVariable = host.TokenEnvironmentVariable;
                                var status = (await hosts.GetStatusAsync(target, token)).Data;
                                if (status.HostId != host.HostId || status.Attachments.SingleOrDefault(a => a.ClusterId == cluster.UniqueName)?.BundleManifestSha256 != host.Deployment.BundleManifestSha256) return false;
                            }
                            await catalog.RecordUpdateAsync(cluster, workflow with { Phase = ClusterUpdatePhase.Complete,
                                LastError = null, UpdatedAt = DateTimeOffset.UtcNow }, token);
                            break;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    if (workflow.LastError != error.Message) logger.LogWarning(error, "Cluster {Cluster} update {Update} waits in {Phase}.", cluster.UniqueName, workflow.Id, workflow.Phase);
                    cluster = catalog.GetCluster(cluster.UniqueName)!;
                    if (cluster.Update?.Id == workflow.Id && cluster.Update.LastError != error.Message)
                        await catalog.RecordUpdateAsync(cluster, cluster.Update with { LastError = error.Message, UpdatedAt = DateTimeOffset.UtcNow }, token);
                }
                return true;
            }, token);
    }
}

public sealed record ClusterUpdateRequest(Guid Id, ClusterDeploymentRequest? Deployment = null, bool Rollback = false);
