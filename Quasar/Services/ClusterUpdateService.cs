using Quasar.Models;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;
using HostContract = Quasar.Host.Contract.V1;

namespace Quasar.Services;

/// <summary>Durable full-downtime workflow; ordinary reconciliation owns shutdown and startup.</summary>
public sealed class ClusterUpdateService(ClusterCatalog catalog, ClusterDeploymentService deployments,
    ClusterGatewayClient gateway, ClusterHostClient hosts, ClusterOperationStore operations, ILogger<ClusterUpdateService> logger,
    ClusterBackupService? backups = null) : BackgroundService
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
        if (cluster.QueuedContentUpdates is not null)
            throw new InvalidOperationException("Queued mod/plugin updates must be staged before applying. Stage the active profile to prepare the queued content, or remove the queue to apply only the prepared deployment.");
        if (cluster.GoalState == DedicatedServerGoalState.Off && cluster.Gateway is not null
            && !cluster.IsProvenStopped())
            throw new InvalidOperationException("This stopped cluster has no clean shutdown proof. Recover the stopped cluster after an unclean shutdown before starting an update.");
        var candidate = request.Rollback ? RollbackDeployment(cluster) : request.Deployment
            ?? throw new InvalidDataException("Candidate deployment is required.");
        if (candidate.Revision == cluster.ActiveDeployment.Revision) throw new InvalidOperationException("Candidate must be a different deployment revision.");
        await deployments.ActivateCoreAsync(cluster, candidate, token, dryRun: true, preflight: true, update: true);
        bool backupRequired = ClusterUpdatePreparationService.RequiresProfileMigration(cluster, candidate.Revision);
        if (backupRequired && backups is null)
            throw new InvalidOperationException("Profile migration needs cluster backup service.");
        var workflow = new ClusterUpdate(request.Id, candidate, cluster.ActiveDeployment, request.Rollback,
            ClusterUpdatePhase.Stopping, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, BackupRequired: backupRequired,
            GoalBefore: cluster.GoalState);
        await catalog.RecordUpdateAsync(cluster, workflow, token, DedicatedServerGoalState.Off);
        return workflow; // Accepted, not converged. Query Update.Phase until Complete.
    }

    // Waiting is not an error, but an update that waits this long needs the operator: every
    // lifecycle tool is locked meanwhile. The message names the way out.
    internal static readonly TimeSpan StoppingDeadline = TimeSpan.FromMinutes(20), StartingDeadline = TimeSpan.FromMinutes(30);

    // Past these the update gives itself up, so a stuck update cannot lock the cluster forever.
    // A half-applied activation is the exception: it can only revert from its pre-update backup, or keep retrying.
    internal static readonly TimeSpan StoppingTimeout = TimeSpan.FromMinutes(60), ActivatingTimeout = TimeSpan.FromMinutes(30),
        StartingTimeout = TimeSpan.FromMinutes(60);

    // A revert first lets the reconciler stop the failed deployment cleanly; its data is replaced by the
    // backup anyway, so after this grace a fenced stop is enough. Restores move whole worlds, hence the long timeout.
    internal static readonly TimeSpan RevertStopGrace = TimeSpan.FromMinutes(10),
        RevertingTimeout = TimeSpan.FromHours(3);

    // The revert reuses the operator's restore path: a fresh preparation with rotated credentials, then the restore.
    internal Func<
        ClusterDefinition,
        ClusterUpdate,
        CancellationToken,
        Task<ClusterRestoreRequest>
    > PrepareRevertAsync = (cluster, update, token) =>
        backups!.PrepareRestoreCoreAsync(
            cluster,
            update.Backup!.Value,
            update.RevertRestoreId!.Value,
            "quasar",
            token,
            update.Id
        );
    internal Func<ClusterDefinition, ClusterUpdate, CancellationToken, Task> ApplyRevertAsync = (
        cluster,
        update,
        token
    ) => backups!.RestoreCoreAsync(cluster, update.Revert!, token, update.Id);

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
                    if (cluster.PendingDeploymentHash is not null || cluster.PendingRestoreHash is not null)
                        throw new InvalidOperationException("The update is activating a deployment or restoring a backup on the Hosts. Resume that or recover the cluster; it cannot be abandoned half-way.");
                    return await AbandonCoreAsync(cluster, update, $"Abandoned by {actor} while {update.Phase}"
                        + (update.LastError is null ? "." : ": " + update.LastError), ct);
                }, ct)), token);

    private async Task<ClusterUpdate> AbandonCoreAsync(ClusterDefinition cluster, ClusterUpdate update, string reason, CancellationToken token,
        DedicatedServerGoalState? goal = null)
    {
        var abandoned = update with { Phase = ClusterUpdatePhase.Complete, UpdatedAt = DateTimeOffset.UtcNow, LastError = reason };
        await catalog.RecordUpdateAsync(cluster, abandoned, token, goal);
        logger.LogWarning("Cluster {Cluster} update {Update} was abandoned in phase {Phase}: {Reason}", cluster.UniqueName, update.Id, update.Phase, reason);
        return abandoned;
    }

    private static TimeSpan Timeout(ClusterUpdatePhase phase) => phase switch
    {
        ClusterUpdatePhase.Stopping => StoppingTimeout,
        ClusterUpdatePhase.Activating => ActivatingTimeout,
        ClusterUpdatePhase.Reverting => RevertingTimeout,
        _ => StartingTimeout,
    };

    // Stopping starts with the update; later phases start at their own checkpoint. Records written
    // before PhaseStartedAt existed fall back to UpdatedAt, which can only make a timeout late.
    private static TimeSpan PhaseAge(ClusterUpdate workflow) => DateTimeOffset.UtcNow
        - (workflow.Phase == ClusterUpdatePhase.Stopping ? workflow.StartedAt : workflow.PhaseStartedAt ?? workflow.UpdatedAt);

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
                            if (!cluster.IsProvenStopped())
                                return await OverdueAsync(cluster, workflow, StoppingDeadline, "a verified clean shutdown");
                            // The stopped cut taken here is the way back to the previous version and world
                            // if the new deployment fails after it ran. Begin refuses a migration without it.
                            if (backups is not null)
                                await backups.CaptureCoreAsync(cluster, new(workflow.Id), token, updateId: workflow.Id);
                            await deployments.ActivateCoreAsync(cluster, workflow.Deployment, token, dryRun: true, update: true);
                            await catalog.RecordUpdateAsync(cluster, workflow with { Phase = ClusterUpdatePhase.Activating,
                                LastError = null, UpdatedAt = DateTimeOffset.UtcNow, PhaseStartedAt = DateTimeOffset.UtcNow,
                                Backup = backups is null ? null : workflow.Id }, token);
                            break;
                        case ClusterUpdatePhase.Activating:
                            await deployments.ActivateCoreAsync(cluster, workflow.Deployment, token, update: true);
                            cluster = catalog.GetCluster(cluster.UniqueName)!; // Activation changed lifecycle identity.
                            await catalog.RecordUpdateAsync(cluster, workflow with { Phase = ClusterUpdatePhase.Starting,
                                LastError = null, UpdatedAt = DateTimeOffset.UtcNow, PhaseStartedAt = DateTimeOffset.UtcNow }, token,
                                DedicatedServerGoalState.On);
                            break;
                        case ClusterUpdatePhase.Starting:
                            if (await NotServingAsync(cluster, workflow.Deployment.Revision, "the new deployment") is { } awaited)
                                return await OverdueAsync(cluster, workflow, StartingDeadline, awaited);
                            await catalog.RecordUpdateAsync(cluster, workflow with { Phase = ClusterUpdatePhase.Complete,
                                LastError = null, UpdatedAt = DateTimeOffset.UtcNow }, token);
                            break;
                        case ClusterUpdatePhase.Reverting:
                            return await RevertAsync(cluster, workflow);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    if (workflow.LastError != error.Message) logger.LogWarning(error, "Cluster {Cluster} update {Update} waits in {Phase}.", cluster.UniqueName, workflow.Id, workflow.Phase);
                    cluster = catalog.GetCluster(cluster.UniqueName)!;
                    if (cluster.Update?.Id != workflow.Id || cluster.Update.Phase == ClusterUpdatePhase.Complete) return true;
                    if (await TimedOutAsync(cluster, cluster.Update, error.Message)) return true;
                    if (cluster.Update.LastError != error.Message)
                        await catalog.RecordUpdateAsync(cluster, cluster.Update with { LastError = error.Message, UpdatedAt = DateTimeOffset.UtcNow }, token);
                }
                return true;
            }

        // Past the phase timeout the update gives up by itself. Before any Host switched, the previous
        // version simply carries on; afterwards the pre-update backup is the way back.
        async Task<bool> TimedOutAsync(ClusterDefinition cluster, ClusterUpdate workflow, string awaited)
        {
            var timeout = Timeout(workflow.Phase);
            // A half-applied activation can only be reverted from the backup; without one it keeps retrying.
            bool halfApplied =
                workflow.Phase == ClusterUpdatePhase.Activating
                && cluster.PendingDeploymentHash is not null;
            if (
                PhaseAge(workflow) < timeout
                || cluster.PendingRestoreHash is not null
                || cluster.PendingDeploymentHash is not null
                    && !(halfApplied && workflow.Backup is not null && backups is not null)
            )
                return false;
            if (workflow.Phase == ClusterUpdatePhase.Reverting)
            {
                await AbandonCoreAsync(
                    cluster,
                    workflow,
                    $"{workflow.Failure} The revert did not finish within {timeout.TotalHours:0} hours: "
                        + $"{awaited.TrimEnd('.')}. To return to the previous version and world, restore backup {workflow.Backup:D} in Maintenance.",
                    token
                );
                return true;
            }
            string reason =
                $"Update failed after {timeout.TotalMinutes:0} minutes in {workflow.Phase}: {awaited.TrimEnd('.')}.";
            if (halfApplied || cluster.ActiveDeployment?.Revision == workflow.Deployment.Revision)
            {
                // The new version ran, or the Hosts hold different bundles: only the stopped cut taken
                // before activation is a clean way back.
                if (workflow.Backup is { } backup && backups is not null)
                {
                    await catalog.RecordUpdateAsync(
                        cluster,
                        workflow with
                        {
                            Phase = ClusterUpdatePhase.Reverting,
                            Failure = reason,
                            RevertRestoreId = Guid.NewGuid(),
                            UpdatedAt = DateTimeOffset.UtcNow,
                            PhaseStartedAt = DateTimeOffset.UtcNow,
                            LastError =
                                $"{reason} Reverting to the previous version and world from backup {backup:D}.",
                        },
                        token,
                        DedicatedServerGoalState.Off
                    );
                    logger.LogWarning(
                        "Cluster {Cluster} update {Update} failed; reverting from backup {Backup}: {Reason}",
                        cluster.UniqueName,
                        workflow.Id,
                        backup,
                        reason
                    );
                    return true;
                }
                await AbandonCoreAsync(
                    cluster,
                    workflow,
                    reason
                        + " The cluster keeps the new deployment. Start a rollback update if it cannot serve.",
                    token
                );
                return true;
            }
            string? blocked = workflow.GoalBefore != DedicatedServerGoalState.On ? "it was stopped before the update"
                : await StartBlockedAsync(cluster);
            await AbandonCoreAsync(cluster, workflow, reason + " Nothing was activated; the cluster stays on its current deployment"
                + (blocked is null ? " and its goal is On again." : $" and stays stopped: {blocked}."), token,
                blocked is null ? DedicatedServerGoalState.On : null);
            return true;
        }

        // Stop the failed deployment, restore the pre-update backup with fresh credentials, then start the
        // previous version again if it ran before the update. Every step resumes after a Quasar restart.
        async Task<bool> RevertAsync(ClusterDefinition cluster, ClusterUpdate workflow)
        {
            if (workflow.Revert is null)
            {
                if (!cluster.IsProvenStopped())
                {
                    if (PhaseAge(workflow) < RevertStopGrace)
                        return false;
                    await deployments.StopFencedAsync(cluster, token);
                }
                var request = await PrepareRevertAsync(cluster, workflow, token);
                if (cluster.PendingDeploymentHash is not null)
                    request = await ExpectWhatHostsRunAsync(cluster, request);
                cluster = catalog.GetCluster(cluster.UniqueName)!;
                workflow = workflow with { Revert = request, UpdatedAt = DateTimeOffset.UtcNow };
                await catalog.RecordUpdateAsync(cluster, workflow, token);
                cluster = catalog.GetCluster(cluster.UniqueName)!;
            }
            if (
                cluster.ActiveDeployment?.Revision != workflow.Revert.Deployment.Revision
                || cluster.PendingRestoreHash is not null
            )
            {
                await ApplyRevertAsync(cluster, workflow, token);
                cluster = catalog.GetCluster(cluster.UniqueName)!; // The restore changed lifecycle identity.
            }
            string reverted =
                $"{workflow.Failure} Reverted to the previous version and world from backup {workflow.Backup:D}.";
            if (workflow.GoalBefore != DedicatedServerGoalState.On)
            {
                await CompleteRevertAsync(
                    reverted + " The cluster stays stopped, as it was before the update."
                );
                return true;
            }
            if (cluster.GoalState == DedicatedServerGoalState.Off)
            {
                if (await StartBlockedAsync(cluster) is { } blocked)
                    await CompleteRevertAsync($"{reverted} It stays stopped: {blocked}.");
                else
                    await catalog.RecordUpdateAsync(
                        cluster,
                        workflow with
                        {
                            UpdatedAt = DateTimeOffset.UtcNow,
                            LastError = reverted + " Starting it again.",
                        },
                        token,
                        DedicatedServerGoalState.On
                    );
                return true;
            }
            if (
                await NotServingAsync(
                    cluster,
                    workflow.Revert.Deployment.Revision,
                    "the restored deployment"
                ) is
                { } awaited
            )
                return await TimedOutAsync(cluster, workflow, "still waiting for " + awaited);
            await CompleteRevertAsync(reverted);
            return true;

            async Task CompleteRevertAsync(string message)
            {
                await catalog.RecordUpdateAsync(
                    cluster,
                    workflow with
                    {
                        Phase = ClusterUpdatePhase.Complete,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        LastError = message,
                    },
                    token
                );
                logger.LogWarning(
                    "Cluster {Cluster} update {Update} was reverted: {Message}",
                    cluster.UniqueName,
                    workflow.Id,
                    message
                );
            }
        }

        // After a half-applied activation some Hosts run the new bundle; each is restored from the one it runs.
        async Task<ClusterRestoreRequest> ExpectWhatHostsRunAsync(
            ClusterDefinition cluster,
            ClusterRestoreRequest request
        )
        {
            var aligned = new List<ClusterHostActivation>();
            foreach (var host in request.Deployment.Hosts)
            {
                var target = cluster.Clone();
                target.HostCommandUrl = host.CommandUrl;
                target.HostCommandTokenEnvironmentVariable = host.TokenEnvironmentVariable;
                var status = (await hosts.GetStatusAsync(target, token)).Data;
                if (status.HostId != host.HostId)
                    throw new InvalidDataException("Host endpoint identifies a different host.");
                aligned.Add(
                    host with
                    {
                        Activation = host.Activation with
                        {
                            ExpectedBundleManifestSha256 = status
                                .Attachments.SingleOrDefault(a => a.ClusterId == cluster.UniqueName)
                                ?.BundleManifestSha256,
                        },
                    }
                );
            }
            return request with { Deployment = request.Deployment with { Hosts = [.. aligned] } };
        }

        // Serving is runtime-gated; every Host must also still run the exact activated bundle.
        async Task<string?> NotServingAsync(ClusterDefinition cluster, string revision, string what)
        {
            var observed = (await gateway.GetStatusAsync(cluster, token)).Data;
            if (
                observed.ClusterId != cluster.UniqueName
                || observed.Phase != Admin.ClusterPhase.Serving
                || !observed.AcceptingPlayers
                || observed.DeploymentRevision != revision
                || !observed.ManagedDeploymentReady
            )
                return $"the Gateway to serve {what} (it reports {observed.Phase})";
            foreach (var host in cluster.ActiveDeployment!.Hosts)
            {
                var target = cluster.Clone();
                target.HostCommandUrl = host.CommandUrl;
                target.HostCommandTokenEnvironmentVariable = host.TokenEnvironmentVariable;
                var status = (await hosts.GetStatusAsync(target, token)).Data;
                if (
                    status.HostId != host.HostId
                    || status
                        .Attachments.SingleOrDefault(a => a.ClusterId == cluster.UniqueName)
                        ?.BundleManifestSha256 != host.Deployment.BundleManifestSha256
                )
                    return $"Host {host.HostId} to run the activated bundle";
            }
            return null;
        }

        // The previous version may run again if its Gateway never stopped (the reconciler adopts it, or waits
        // for a clean Down) or stopped with the same clean proof a normal start needs.
        async Task<string?> StartBlockedAsync(ClusterDefinition cluster)
        {
            try
            {
                var status = (await hosts.GetStatusAsync(cluster, token)).Data;
                if (status.Gateways?.SingleOrDefault(g => g.ClusterId.Equals(cluster.UniqueName, StringComparison.OrdinalIgnoreCase))
                        is { Observed: HostContract.GatewayObservedState.Running }
                    || ClusterCommandService.CanStartFromHostState(cluster, status))
                    return null;
                return "its Gateway has no clean shutdown proof; recover it in Maintenance → Recovery";
            }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            {
                return "its Gateway Host cannot be checked (" + error.Message.TrimEnd('.') + ")";
            }
        }

        // Still waiting: past the deadline the reason is recorded once so the UI and API show it.
        async Task<bool> OverdueAsync(ClusterDefinition cluster, ClusterUpdate workflow, TimeSpan deadline, string awaited)
        {
            if (await TimedOutAsync(cluster, workflow, "still waiting for " + awaited)) return true;
            if (workflow.LastError is not null || PhaseAge(workflow) < deadline) return false;
            string message = $"Still waiting for {awaited} after {deadline.TotalMinutes:0} minutes. Check the cluster status; abandon the update to unlock the cluster controls, or Quasar gives it up after {Timeout(workflow.Phase).TotalMinutes:0} minutes.";
            logger.LogWarning("Cluster {Cluster} update {Update} is overdue in {Phase}.", cluster.UniqueName, workflow.Id, workflow.Phase);
            await catalog.RecordUpdateAsync(cluster, workflow with { LastError = message }, token);
            return false;
        }
    }
}

public sealed record ClusterUpdateRequest(Guid Id, ClusterDeploymentRequest? Deployment = null, bool Rollback = false);
