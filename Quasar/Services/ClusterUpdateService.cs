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

    // Waiting is not an error, but an update that waits this long needs the operator: every
    // lifecycle tool is locked meanwhile. The message names the way out.
    internal static readonly TimeSpan StoppingDeadline = TimeSpan.FromMinutes(20), StartingDeadline = TimeSpan.FromMinutes(30);

    /// <summary>Gives up an update that cannot finish and unlocks the lifecycle tools. Nothing is rolled back:
    /// before activation the cluster stays stopped on its current deployment, afterwards it keeps the new one.</summary>
    public Task<ClusterOperation> AbandonAsync(string clusterId, string key, string actor, CancellationToken token) =>
        operations.ExecuteAsync(clusterId, "cluster.update.abandon", key, actor, new { clusterId },
            async ct => new Admin.AdminEnvelope<ClusterUpdate>(Admin.AdminProtocol.Version, DateTimeOffset.UtcNow,
                await catalog.WithLifecycleAsync(clusterId, async cluster =>
                {
                    var update = cluster.Update ?? throw new InvalidOperationException("No update is recorded for this cluster.");
                    if (update.Phase == ClusterUpdatePhase.Complete) return update;
                    // Hosts may hold different revisions now; only the deployment's own resume/recovery can settle that.
                    if (cluster.PendingDeploymentHash is not null)
                        throw new InvalidOperationException("The update is activating a deployment on the Hosts. Resume that activation or recover the cluster; it cannot be abandoned half-way.");
                    var abandoned = update with { Phase = ClusterUpdatePhase.Complete, UpdatedAt = DateTimeOffset.UtcNow,
                        LastError = $"Abandoned by {actor} while {update.Phase}" + (update.LastError is null ? "." : ": " + update.LastError) };
                    await catalog.RecordUpdateAsync(cluster, abandoned, ct);
                    logger.LogWarning("Cluster {Cluster} update {Update} was abandoned by {Actor} in phase {Phase}.", cluster.UniqueName, update.Id, actor, update.Phase);
                    return abandoned;
                }, ct)), token);

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
            await catalog.TryWithLifecycleAsync(snapshot.UniqueName, cluster => AdvanceAsync(cluster), token);

        async Task<bool> AdvanceAsync(ClusterDefinition cluster)
            {
                if (cluster.Update is not { Phase: not ClusterUpdatePhase.Complete } workflow) return false;
                try
                {
                    switch (workflow.Phase)
                    {
                        case ClusterUpdatePhase.Stopping:
                            if (cluster.ShutdownProof?.LifecycleId != cluster.GetLifecycleId())
                                return await OverdueAsync(cluster, workflow, StoppingDeadline, "a verified clean shutdown");
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
                                || observed.DeploymentRevision != workflow.Deployment.Revision || !observed.ManagedDeploymentReady)
                                return await OverdueAsync(cluster, workflow, StartingDeadline, $"the Gateway to serve the new deployment (it reports {observed.Phase})");
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
            }

        // Still waiting: past the deadline the reason is recorded once so the UI and API show it.
        async Task<bool> OverdueAsync(ClusterDefinition cluster, ClusterUpdate workflow, TimeSpan deadline, string awaited)
        {
            // StartedAt covers Stopping; later phases are measured from their own checkpoint.
            var since = workflow.Phase == ClusterUpdatePhase.Stopping ? workflow.StartedAt : workflow.UpdatedAt;
            if (workflow.LastError is not null || DateTimeOffset.UtcNow - since < deadline) return false;
            string message = $"Still waiting for {awaited} after {deadline.TotalMinutes:0} minutes. Check the cluster status; abandon the update to unlock the cluster controls.";
            logger.LogWarning("Cluster {Cluster} update {Update} is overdue in {Phase}.", cluster.UniqueName, workflow.Id, workflow.Phase);
            await catalog.RecordUpdateAsync(cluster, workflow with { LastError = message }, token);
            return false;
        }
    }
}

public sealed record ClusterUpdateRequest(Guid Id, ClusterDeploymentRequest? Deployment = null, bool Rollback = false);
