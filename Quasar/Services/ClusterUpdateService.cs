using System.Diagnostics;
using Quasar.Models;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;
using HostContract = Quasar.Host.Contract.V1;

namespace Quasar.Services;

/// <summary>Durable full-downtime workflow; ordinary reconciliation owns shutdown and startup.</summary>
public sealed class ClusterUpdateService(ClusterCatalog catalog, ClusterDeploymentService deployments,
    ClusterGatewayClient gateway, ClusterHostClient hosts, ClusterOperationStore operations, ILogger<ClusterUpdateService> logger,
    ClusterBackupService? backups = null, ClusterPackageService? packages = null) : BackgroundService
{
    public Task<ClusterOperation> BeginAsync(string clusterId, ClusterUpdateRequest request, string key, string actor, CancellationToken token) =>
        operations.ExecuteAsync(clusterId, "cluster.update.begin", key, actor, request,
            async ct =>
            {
                long requested = Stopwatch.GetTimestamp();
                return new Admin.AdminEnvelope<ClusterUpdate>(Admin.AdminProtocol.Version, DateTimeOffset.UtcNow,
                    await catalog.WithLifecycleAsync(clusterId, cluster =>
                    {
                        LogStep(cluster, "begin: wait for the lifecycle gate", requested);
                        return BeginCoreAsync(cluster, request, actor, ct);
                    }, ct));
            }, token);

    // At Information, which the logging configuration keeps for this class at every minimum level.
    private void LogStep(ClusterDefinition cluster, string step, long started) =>
        logger.LogInformation("Cluster {Cluster} update step '{Step}' took {Seconds:0.0} s.", cluster.UniqueName, step,
            Stopwatch.GetElapsedTime(started).TotalSeconds);

    private async Task TimedAsync(ClusterDefinition cluster, string step, Func<Task> action)
    {
        long started = Stopwatch.GetTimestamp();
        await action();
        LogStep(cluster, step, started);
    }

    /// <summary>What the last shutdown left unsaved, as the stopped Gateway records it, sorted. The operator reviews this list
    /// before an update despite a dirty shutdown and passes it back as <see cref="ClusterUpdateRequest.AcceptDirtyShutdown"/>.</summary>
    public Task<string[]> GetRecordedDirtyShutdownAsync(string clusterId, CancellationToken token) =>
        RecordedDirtyShutdownAsync(catalog.GetCluster(clusterId) ?? throw new KeyNotFoundException(clusterId), token);

    private async Task<string[]> RecordedDirtyShutdownAsync(ClusterDefinition cluster, CancellationToken token)
    {
        Admin.ClusterStatus status;
        Admin.SnapshotSummary[] snapshots;
        try
        {
            status = (await gateway.GetStatusAsync(cluster, token)).Data;
            snapshots = (await gateway.GetSnapshotsAsync(cluster, token)).Data;
        }
        catch (ClusterGatewayException error)
        {
            throw new InvalidOperationException("The stopped Gateway must answer to name what the shutdown left unsaved ("
                + error.Message.TrimEnd('.') + "). Recover the stopped cluster instead.", error);
        }
        if (status.ClusterId != cluster.UniqueName) throw new InvalidOperationException("Gateway status belongs to a different cluster.");
        if (status.Phase != Admin.ClusterPhase.Down)
            throw new InvalidOperationException($"The Gateway is {status.Phase}. Wait for the shutdown to end before updating despite a dirty shutdown.");
        return ClusterReconciler.DirtyList(status, snapshots)?.Order(StringComparer.Ordinal).ToArray() ?? throw new InvalidOperationException(
            "The Gateway has no shutdown snapshot for its last shutdown, so it cannot name what was left unsaved. Recover the stopped cluster instead.");
    }

    // The new deployment's Registry runs the recovery start, so its release must accept a changed revision there.
    internal Func<ClusterDefinition, ClusterDeploymentRequest, CancellationToken, Task> RequireRecoveryUpdateAsync =
        async (cluster, candidate, token) =>
        {
            if (packages is null || cluster.PackageSelection is not { } selection || cluster.Preparation is not { } preparation
                || ClusterDependencyService.DeploymentRevision(preparation) != candidate.Revision)
                throw new InvalidOperationException("An update despite a dirty shutdown needs a deployment prepared from the selected cluster release.");
            var package = await packages.GetInstalledAsync(new(selection.Version, selection.Sha256), token);
            Quasar.ClusterDeployment.ClusterDeploymentFiles.RequireRecoveryUpdate(package.PackagePath);
        };

    private async Task<ClusterUpdate> BeginCoreAsync(ClusterDefinition cluster, ClusterUpdateRequest request, string actor,
        CancellationToken token)
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
        bool dirty = cluster.GoalState == DedicatedServerGoalState.Off && cluster.Gateway is not null && !cluster.IsProvenStopped();
        if (dirty && request.AcceptDirtyShutdown is null)
            throw new InvalidOperationException("This stopped cluster has no clean shutdown proof. Recover the stopped cluster after an unclean shutdown, or update it despite the recorded dirty shutdown in Maintenance, before starting an update.");
        if (!dirty && request.AcceptDirtyShutdown is not null)
            throw new InvalidOperationException("Only a stopped cluster without a clean shutdown proof can be updated despite a dirty shutdown.");
        if (dirty && request.Rollback)
            throw new InvalidOperationException("A rollback cannot start despite a dirty shutdown. Restore a backup instead.");
        var candidate = request.Rollback ? RollbackDeployment(cluster) : request.Deployment
            ?? throw new InvalidDataException("Candidate deployment is required.");
        if (candidate.Revision == cluster.ActiveDeployment.Revision) throw new InvalidOperationException("Candidate must be a different deployment revision.");
        string[]? accepted = null;
        if (dirty)
        {
            // The operator accepts exactly what the Gateway names now. Those parts recover from their newest
            // catalogued saves at the recovery start, as Recovery would; everything else was saved.
            accepted = await RecordedDirtyShutdownAsync(cluster, token);
            if (!accepted.SequenceEqual(request.AcceptDirtyShutdown!.Order(StringComparer.Ordinal)))
                throw new InvalidOperationException("The recorded dirty shutdown is not the one that was accepted. It now lists: "
                    + (accepted.Length == 0 ? "nothing" : string.Join(", ", accepted)) + ". Review it and try again.");
            await RequireRecoveryUpdateAsync(cluster, candidate, token);
        }
        await TimedAsync(cluster, "begin: online dry run", () =>
            deployments.ActivateCoreAsync(cluster, candidate, token, dryRun: true, preflight: true, update: true));
        bool backupRequired = ClusterUpdatePreparationService.RequiresProfileMigration(cluster, candidate.Revision);
        if (backupRequired && backups is null)
            throw new InvalidOperationException("Profile migration needs cluster backup service.");
        var workflow = new ClusterUpdate(request.Id, candidate, cluster.ActiveDeployment, request.Rollback,
            ClusterUpdatePhase.Stopping, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, BackupRequired: backupRequired,
            GoalBefore: cluster.GoalState, DirtyShutdown: accepted);
        await catalog.RecordUpdateAsync(cluster, workflow, token, DedicatedServerGoalState.Off);
        if (accepted is not null)
            logger.LogWarning("Cluster {Cluster} update {Update} starts despite the recorded dirty shutdown, accepted by {Actor}: {Dirty}",
                cluster.UniqueName, workflow.Id, actor, accepted.Length == 0 ? "nothing listed" : string.Join(", ", accepted));
        return workflow; // Accepted, not converged. Query Update.Phase until Complete.
    }

    // Waiting is not an error, but an update that waits this long needs the operator: every
    // lifecycle tool is locked meanwhile. The message names the way out.
    internal static readonly TimeSpan StoppingDeadline = TimeSpan.FromMinutes(20), StartingDeadline = TimeSpan.FromMinutes(30);

    // Past these the update gives itself up, so a stuck update cannot lock the cluster forever.
    // A half-applied activation is the exception: it can only revert from its pre-update backup, or keep retrying.
    internal static readonly TimeSpan StoppingTimeout = TimeSpan.FromMinutes(60), ActivatingTimeout = TimeSpan.FromMinutes(30),
        StartingTimeout = TimeSpan.FromMinutes(60);

    // Once every node fails at startup, the Registry halts all spawns and tries again after its 10 minute failure
    // window. Past this that one retry failed as well, so waiting longer only keeps the cluster down.
    internal static readonly TimeSpan NodesFailingTimeout = TimeSpan.FromMinutes(12);

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
                            bool dirtyShutdown = workflow.DirtyShutdown is not null;
                            if (!cluster.IsProvenStopped())
                            {
                                if (!dirtyShutdown)
                                    return await StoppedDirtyAsync(cluster, workflow)
                                        || await OverdueAsync(cluster, workflow, StoppingDeadline, "a verified clean shutdown (if it "
                                            + "ended dirty, abandon this update and apply it with Update despite the recorded dirty shutdown)");
                                if (!workflow.FencedStop)
                                {
                                    // As Recovery does: every Host proves its nodes stopped, then the Gateway stops fenced.
                                    await deployments.StopFencedAsync(cluster, token);
                                    workflow = workflow with { FencedStop = true, UpdatedAt = DateTimeOffset.UtcNow };
                                    cluster = await catalog.RecordUpdateAsync(cluster, workflow, token);
                                }
                            }
                            // The stopped cut taken here is the way back to the previous version and world
                            // if the new deployment fails after it ran. Begin refuses a migration without it.
                            // After a dirty shutdown it is crash-consistent: it holds what the Dirty list names as unsaved.
                            logger.LogInformation("Cluster {Cluster} update stopped {Seconds:0.0} s after it began.", cluster.UniqueName,
                                (DateTimeOffset.UtcNow - workflow.StartedAt).TotalSeconds);
                            // A first deployment that never ran has no world to back up yet.
                            bool backUp = backups is not null && (dirtyShutdown || cluster.IsProvenStopped(requireRun: true));
                            if (backUp)
                                await TimedAsync(cluster, "backup", () => backups!.CaptureCoreAsync(cluster,
                                    new(workflow.Id, AllowCrashConsistent: dirtyShutdown), token, updateId: workflow.Id));
                            await TimedAsync(cluster, "dry run", () => deployments.ActivateCoreAsync(cluster, workflow.Deployment, token,
                                dryRun: true, update: true, dirtyShutdown: dirtyShutdown));
                            await catalog.RecordUpdateAsync(cluster, workflow with { Phase = ClusterUpdatePhase.Activating,
                                LastError = null, UpdatedAt = DateTimeOffset.UtcNow, PhaseStartedAt = DateTimeOffset.UtcNow,
                                Backup = backUp ? workflow.Id : null }, token);
                            break;
                        case ClusterUpdatePhase.Activating:
                            await TimedAsync(cluster, "activation", () => deployments.ActivateCoreAsync(cluster, workflow.Deployment, token,
                                update: true, dirtyShutdown: workflow.DirtyShutdown is not null));
                            cluster = catalog.GetCluster(cluster.UniqueName)!; // Activation changed lifecycle identity.
                            // After a dirty shutdown the new deployment's first start is a recovery start.
                            await catalog.RecordUpdateAsync(cluster, workflow with { Phase = ClusterUpdatePhase.Starting,
                                LastError = null, UpdatedAt = DateTimeOffset.UtcNow, PhaseStartedAt = DateTimeOffset.UtcNow }, token,
                                DedicatedServerGoalState.On, recoveryUpdate: workflow.DirtyShutdown is not null);
                            break;
                        case ClusterUpdatePhase.Starting:
                            if (await NotServingAsync(cluster, workflow.Deployment.Revision, "the new deployment") is { } awaited)
                                return await NodesFailingAsync(cluster, workflow) || await OverdueAsync(cluster, workflow, StartingDeadline, awaited);
                            await catalog.RecordUpdateAsync(cluster, workflow with { Phase = ClusterUpdatePhase.Complete,
                                LastError = null, UpdatedAt = DateTimeOffset.UtcNow }, token);
                            logger.LogInformation("Cluster {Cluster} update served {Seconds:0.0} s after activation, {Total:0.0} s after it began.",
                                cluster.UniqueName, PhaseAge(workflow).TotalSeconds, (DateTimeOffset.UtcNow - workflow.StartedAt).TotalSeconds);
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
        async Task<bool> TimedOutAsync(ClusterDefinition cluster, ClusterUpdate workflow, string awaited, TimeSpan? limit = null)
        {
            var timeout = limit ?? Timeout(workflow.Phase);
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
                    if (PhaseAge(workflow) < RevertStopGrace && !await GatewayDownAsync(cluster))
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

        // A Gateway that reached Down won't stop any cleaner, for example when no node of a failed start ever saved.
        async Task<bool> GatewayDownAsync(ClusterDefinition cluster)
        {
            try { return (await gateway.GetStatusAsync(cluster, token)).Data.Phase == Admin.ClusterPhase.Down; }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested) { return false; }
        }

        // A shutdown that ended dirty never turns into the clean proof this update waits for, so say so right away.
        async Task<bool> StoppedDirtyAsync(ClusterDefinition cluster, ClusterUpdate workflow)
        {
            string[]? dirty;
            try
            {
                var status = (await gateway.GetStatusAsync(cluster, token)).Data;
                dirty = status.Phase != Admin.ClusterPhase.Down ? null
                    : ClusterReconciler.DirtyList(status, (await gateway.GetSnapshotsAsync(cluster, token)).Data);
            }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested) { return false; }
            if (dirty is not { Length: > 0 }) return false;
            string message = ClusterReconciler.UnsavedState(dirty)
                + " This update can't go on. Abandon it, then apply it with Update despite the recorded dirty shutdown in Maintenance, or recover the stopped cluster.";
            if (await TimedOutAsync(cluster, workflow, message)) return true;
            if (workflow.LastError != message)
                await catalog.RecordUpdateAsync(cluster, workflow with { LastError = message, UpdatedAt = DateTimeOffset.UtcNow }, token);
            return true;
        }

        // Nodes that keep failing at startup won't serve however long the update waits. Says why as soon as the Gateway
        // gives up on them; when all of them fail, the update then fails as a timeout does, without the long wait.
        async Task<bool> NodesFailingAsync(ClusterDefinition cluster, ClusterUpdate workflow)
        {
            bool halted = (await gateway.GetStatusAsync(cluster, token)).Data.GlobalSpawnHalted;
            var wanted = (await gateway.GetPlanAsync(cluster, token)).Data.Where(slot => slot.Goal == Admin.NodeGoal.Wanted).ToArray();
            var failing = wanted.Where(slot => slot.Observed != Admin.NodeObservation.Ready && slot.LastFailure is not null
                && (halted || slot.FailureAlert)).ToArray();
            if (failing.Length == 0) return false;
            string why = await DescribeNodeFailuresAsync(cluster, failing);
            bool allFailing = failing.Length == wanted.Length;
            if (allFailing && await TimedOutAsync(cluster, workflow, why, NodesFailingTimeout)) return true;
            string message = why + (allFailing
                ? $" Unless a retry succeeds, Quasar gives this update up {NodesFailingTimeout.TotalMinutes:0} minutes after it reached Starting. Abandon it to stage a fixed build now."
                : " Abandon this update to stage a fixed build.");
            if (workflow.LastError == message) return true;
            logger.LogWarning("Cluster {Cluster} update {Update} is not starting: {Reason}", cluster.UniqueName, workflow.Id, why);
            await catalog.RecordUpdateAsync(cluster, workflow with { LastError = message, UpdatedAt = DateTimeOffset.UtcNow }, token);
            return true;
        }

        // The Gateway knows which slots fail, the Host that ran them why. A Host that can't answer only costs the detail.
        async Task<string> DescribeNodeFailuresAsync(ClusterDefinition cluster, Admin.NodePlan[] failing)
        {
            var reported = new List<HostContract.HostNodeFailure>();
            foreach (var host in cluster.ActiveDeployment!.Hosts)
            {
                var target = cluster.Clone();
                target.HostCommandUrl = host.CommandUrl;
                target.HostCommandTokenEnvironmentVariable = host.TokenEnvironmentVariable;
                try { reported.AddRange((await hosts.GetStatusAsync(target, token)).Data.NodeFailures ?? []); }
                catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
                { logger.LogDebug(error, "Host {Host} did not say why its nodes failed.", host.HostId); }
            }
            var details = failing.Select(slot => (slot.SlotKey, slot.LastFailure, Reported: reported.FirstOrDefault(
                failure => failure.ClusterId == cluster.UniqueName && failure.SlotKey == slot.SlotKey)));
            return "Nodes do not start. " + string.Join(" ", details
                .GroupBy(detail => (detail.LastFailure, detail.Reported?.Cause))
                .OrderBy(group => group.Min(detail => detail.SlotKey), StringComparer.Ordinal)
                .Select(group => string.Join(", ", group.Select(detail => detail.SlotKey).Order(StringComparer.Ordinal)) + ": " + group.Key.LastFailure
                    + (group.Key.Cause is null ? "." : $" ({group.Key.Cause}).")))
                + (details.Select(detail => detail.Reported?.LogPath).FirstOrDefault(path => path is not null) is { } log ? $" Node log: {log}." : "");
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

/// <param name="AcceptDirtyShutdown">Update a stopped cluster without a clean shutdown proof: the exact Dirty list of its
/// last shutdown (<see cref="ClusterUpdateService.GetRecordedDirtyShutdownAsync"/>, or the Gateway's CleanShutdown
/// snapshot). The new deployment then starts as a recovery start.</param>
public sealed record ClusterUpdateRequest(Guid Id, ClusterDeploymentRequest? Deployment = null, bool Rollback = false,
    string[]? AcceptDirtyShutdown = null);
