using System.Collections.Concurrent;
using Quasar.Models;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;
using HostContract = global::Quasar.Host.Contract.V1;

namespace Quasar.Services;

public sealed class ClusterReconciler : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private readonly ClusterCatalog _catalog;
    private readonly ClusterGatewayClient _gatewayClient;
    private readonly ClusterHostClient _hostClient;
    private readonly ClusterOperationStore _operations;
    private readonly ILogger<ClusterReconciler> _logger;
    private readonly ConcurrentDictionary<string, ClusterReconcileStatus> _status =
        new(StringComparer.OrdinalIgnoreCase);

    public ClusterReconciler(ClusterCatalog catalog, ClusterGatewayClient gatewayClient,
        ClusterHostClient hostClient, ClusterOperationStore operations, ILogger<ClusterReconciler> logger)
    {
        _catalog = catalog;
        _gatewayClient = gatewayClient;
        _hostClient = hostClient;
        _operations = operations;
        _logger = logger;
    }

    public ClusterReconcileStatus GetStatus(string uniqueName) =>
        _status.GetValueOrDefault(uniqueName)
        ?? new(uniqueName, DedicatedServerGoalState.Off, ClusterReconcileState.Pending,
            null, null, DateTimeOffset.UtcNow, null, "Waiting for the first reconcile pass.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await ReconcileAllAsync(stoppingToken);
                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal static readonly TimeSpan ShutdownRetryDelay = TimeSpan.FromSeconds(60);

    internal async Task ReconcileAllAsync(CancellationToken cancellationToken)
    {
        foreach (ClusterDefinition cluster in _catalog.GetClusters())
        {
            try
            {
                await _catalog.TryWithLifecycleAsync(cluster.UniqueName,
                    current => ReconcileAsync(current, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ClusterGatewayException exception)
            {
                Failed(cluster, exception.Code, exception.Message);
            }
            catch (ClusterHostException exception)
            {
                Failed(cluster, exception.Code, exception.Message);
            }
            catch (ClusterOperationConflictException exception)
            {
                Failed(cluster, exception.Code, exception.Message);
            }
            catch (ClusterOperationStoreUnavailableException exception)
            {
                Failed(cluster, "operation_store_unavailable", exception.Message);
            }
            catch (Exception exception)
            {
                Failed(cluster, "reconcile_failed", exception.Message);
                _logger.LogWarning(exception, "Cluster {Cluster} reconciliation failed.", cluster.UniqueName);
            }
        }
    }

    private async Task ReconcileAsync(ClusterDefinition cluster, CancellationToken cancellationToken)
    {
        if (cluster.PendingDeploymentHash is not null || cluster.PendingRestoreHash is not null)
        {
            Set(cluster, ClusterReconcileState.Pending, null, null, "deployment_pending",
                "Deployment activation is incomplete; resume its original request.");
            return;
        }

        if (cluster.Gateway == null)
        {
            Admin.ClusterStatus observed = (await _gatewayClient.GetStatusAsync(cluster, cancellationToken)).Data;
            Set(cluster, ClusterReconcileState.Observing, null, observed.Phase,
                null, "Observing an existing cluster; process lifecycle is not configured.");
            return;
        }

        if (!_operations.IsReady)
            throw new ClusterOperationStoreUnavailableException();

        if (cluster.GoalState == DedicatedServerGoalState.On && _operations.HasPendingShutdown(cluster.UniqueName))
        {
            Set(cluster, ClusterReconcileState.Converging, null, null, "shutdown_pending",
                "Waiting for the previously requested shutdown to finish before applying the On goal.");
            return;
        }

        HostContract.GatewaySpec on = cluster.Gateway with
        {
            ClusterId = cluster.UniqueName,
            Goal = HostContract.GatewayGoal.On,
            StopFence = null,
            Ports = [.. cluster.Gateway.Ports],
        };
        HostContract.HostStatus host = (await _hostClient.GetStatusAsync(cluster, cancellationToken)).Data;
        HostContract.GatewayStatus? current = host.Gateways?.FirstOrDefault(gateway =>
            string.Equals(gateway.ClusterId, cluster.UniqueName, StringComparison.OrdinalIgnoreCase));
        if (cluster.GoalState == DedicatedServerGoalState.Off
            && (current is null || current.Observed == HostContract.GatewayObservedState.Missing))
        {
            bool clean = host.GatewayStopFencing && current is { Goal: HostContract.GatewayGoal.Off, Failure: null }
                && MatchesSpec(current, on) && HasShutdownProof(cluster)
                && current.CompletedStopFence is not null
                && current.CompletedStopFence == cluster.ShutdownProof!.StopFence;
            if (clean) await _operations.CompleteShutdownAsync(cluster, cancellationToken);
            Set(cluster, clean ? ClusterReconcileState.Converged : ClusterReconcileState.ConfigurationRequired,
                current?.Observed ?? HostContract.GatewayObservedState.Missing,
                clean ? Admin.ClusterPhase.Down : null,
                clean ? null : "shutdown_unverified",
                clean ? "Cluster is cleanly down and the Gateway process is stopped."
                    : "Gateway is absent without matching clean-shutdown proof; explicit recovery is required.");
            return;
        }

        // An Off goal must never launch or replace a Gateway merely to stop it.
        if (cluster.GoalState == DedicatedServerGoalState.Off
            && (current!.Observed != HostContract.GatewayObservedState.Running || !MatchesSpec(current, on)))
        {
            Set(cluster, ClusterReconcileState.ConfigurationRequired, current.Observed, null,
                "gateway_recovery_required", "Gateway identity or process state requires explicit recovery before shutdown.");
            return;
        }
        if (cluster.GoalState == DedicatedServerGoalState.Off
            && (!host.GatewayStopFencing || current!.ProcessId is null || current.LaunchedAt is null))
        {
            Set(cluster, ClusterReconcileState.ConfigurationRequired, current!.Observed, null,
                "gateway_stop_fencing_unavailable", "Host must support fenced Gateway stops and report process identity.");
            return;
        }

        if (cluster.GoalState == DedicatedServerGoalState.On && on.StartGeneration is not null
            && current is { Observed: HostContract.GatewayObservedState.Running }
            && current.StartGeneration != on.StartGeneration
            && MatchesSpec(current, on with { StartGeneration = current.StartGeneration }))
        {
            var prior = (await _gatewayClient.GetStatusAsync(cluster, cancellationToken)).Data;
            if (prior.ClusterId != cluster.UniqueName)
                throw new InvalidOperationException("Gateway identity changed during warm start.");
            if (prior.Phase is Admin.ClusterPhase.Serving or Admin.ClusterPhase.Degraded or Admin.ClusterPhase.Bootstrapping)
            {
                // Off was cancelled before shutdown started. Keep the live generation, without churn.
                await _catalog.RecordStartGenerationAsync(cluster, current.StartGeneration, cancellationToken);
                Set(cluster, ClusterReconcileState.Converging, current.Observed, prior.Phase, null,
                    "Adopted the existing running generation.");
                return;
            }
            if (prior.Phase != Admin.ClusterPhase.Down)
            {
                Set(cluster, ClusterReconcileState.Converging, current.Observed, prior.Phase, "shutdown_pending",
                    "Waiting for clean Down before starting the next generation.");
                return;
            }
            ClusterApi.EnsureGatewayCanStop(prior);
            if (!host.GatewayStopFencing || current.ProcessId is null || current.LaunchedAt is null)
                throw new InvalidOperationException("Warm start requires a fenced Gateway stop.");
            var fence = new HostContract.GatewayStopFence(current.ProcessId.Value, current.LaunchedAt.Value);
            var previousSpec = on with { Goal = HostContract.GatewayGoal.Off,
                StartGeneration = current.StartGeneration, StopFence = fence };
            current = (await _hostClient.ApplyGatewayAsync(cluster, previousSpec, cancellationToken)).Data;
            if (current.Observed != HostContract.GatewayObservedState.Missing || current.CompletedStopFence != fence
                || current.Failure is not null || !MatchesSpec(current, previousSpec))
                throw new InvalidOperationException("Previous Gateway generation has not completed its fenced stop.");
        }

        HostContract.GatewayStatus hostGateway = cluster.GoalState == DedicatedServerGoalState.Off
            ? current! : await EnsureGatewayRunningAsync(cluster, on, current, cancellationToken);
        if (hostGateway.Observed != HostContract.GatewayObservedState.Running)
        {
            Set(cluster, ClusterReconcileState.Converging, hostGateway.Observed, null,
                hostGateway.Failure == null ? null : "gateway_start_failed",
                hostGateway.Failure ?? "Waiting for the Gateway process.");
            return;
        }

        Admin.ClusterStatus gateway;
        try
        {
            gateway = (await _gatewayClient.GetStatusAsync(cluster, cancellationToken)).Data;
        }
        catch (ClusterGatewayException exception) when (exception.Code is "gateway_unavailable" or "gateway_timeout")
        {
            Set(cluster, ClusterReconcileState.Converging, hostGateway.Observed, null,
                "gateway_api_starting", "Gateway process is running; waiting for its admin API.");
            return;
        }
        if (!string.Equals(gateway.ClusterId, cluster.UniqueName, StringComparison.OrdinalIgnoreCase))
            throw new ClusterGatewayException(System.Net.HttpStatusCode.Conflict,
                "cluster_identity_mismatch", "Gateway status belongs to a different cluster.");
        if (cluster.GoalState == DedicatedServerGoalState.On)
        {
            Set(cluster, gateway.Phase == Admin.ClusterPhase.Serving
                    ? ClusterReconcileState.Converged : ClusterReconcileState.Converging,
                hostGateway.Observed, gateway.Phase, null,
                gateway.Phase == Admin.ClusterPhase.Serving
                    ? "Gateway reports Serving."
                    : $"Gateway is {gateway.Phase}; waiting for Registry readiness.");
            return;
        }

        if (gateway.Phase != Admin.ClusterPhase.Down)
        {
            if (cluster.ShutdownProof is not null)
                await _catalog.RecordShutdownProofAsync(cluster, null, cancellationToken);
            // The Gateway drains for GraceSeconds and fails the shutdown 30 s later; it ignores
            // ForceAfterSeconds (kept so a pending request keeps its content hash). A failed drain
            // (players still connected) is tried again under a new key, otherwise goal Off could
            // never be reached for this lifecycle.
            Admin.ShutdownRequest request = new(GraceSeconds: cluster.ShutdownGracePeriodSeconds,
                ForceAfterSeconds: 900);
            ClusterOperation result = await _operations.ExecuteGatewayAsync(cluster,
                "cluster.lifecycle.shutdown", "POST", "shutdown", request,
                _operations.AttemptKey(cluster.UniqueName, "cluster.lifecycle.shutdown", cluster.GetLifecycleId(), ShutdownRetryDelay),
                "reconciler", _gatewayClient, cancellationToken);
            if (result.State == ClusterOperationState.Failed)
                throw new ClusterGatewayException(System.Net.HttpStatusCode.Conflict,
                    result.Error?.Code ?? "shutdown_failed", result.Error?.Message ?? "Cluster shutdown failed.");
            if (result.State == ClusterOperationState.Running)
            {
                Set(cluster, ClusterReconcileState.Converging, hostGateway.Observed, gateway.Phase,
                    null, "Graceful cluster shutdown is still running.");
                return;
            }
            gateway = (await _gatewayClient.GetStatusAsync(cluster, cancellationToken)).Data;
        }

        ClusterApi.EnsureGatewayCanStop(gateway);
        if (!string.Equals(gateway.ClusterId, cluster.UniqueName, StringComparison.OrdinalIgnoreCase))
            throw new ClusterGatewayException(System.Net.HttpStatusCode.Conflict,
                "cluster_identity_mismatch", "Gateway status belongs to a different cluster.");
        var stopFence = new HostContract.GatewayStopFence(hostGateway.ProcessId!.Value, hostGateway.LaunchedAt!.Value);
        await _catalog.RecordShutdownProofAsync(cluster,
            new(cluster.GetLifecycleId(), gateway.LastCleanShutdown!.Value, stopFence), cancellationToken);
        HostContract.GatewayStatus stopped = (await _hostClient.ApplyGatewayAsync(cluster,
            on with
            {
                Goal = HostContract.GatewayGoal.Off,
                StopFence = stopFence,
            }, cancellationToken)).Data;
        bool complete = stopped.Observed == HostContract.GatewayObservedState.Missing
            && stopped.Goal == HostContract.GatewayGoal.Off && MatchesSpec(stopped, on)
            && stopped.CompletedStopFence == stopFence && stopped.Failure is null;
        if (!complete) await _catalog.RecordShutdownProofAsync(cluster, null, cancellationToken);
        else await _operations.CompleteShutdownAsync(_catalog.GetCluster(cluster.UniqueName)!, cancellationToken);
        Set(cluster, complete
                ? ClusterReconcileState.Converged : ClusterReconcileState.Converging,
            stopped.Observed, gateway.Phase, stopped.Failure == null ? null : "gateway_stop_failed",
            stopped.Failure ?? (complete
                ? "Cluster is cleanly down and the Gateway process is stopped."
                : "Waiting for a matching fenced Gateway stop confirmation."));
    }

    private async Task<HostContract.GatewayStatus> EnsureGatewayRunningAsync(ClusterDefinition cluster,
        HostContract.GatewaySpec desired, HostContract.GatewayStatus? current,
        CancellationToken cancellationToken)
    {
        if (current != null && current.Goal == HostContract.GatewayGoal.On && MatchesSpec(current, desired)
            && current.Observed == HostContract.GatewayObservedState.Running)
            return current;
        return (await _hostClient.ApplyGatewayAsync(cluster, desired, cancellationToken)).Data;
    }

    internal static bool MatchesSpec(HostContract.GatewayStatus current, HostContract.GatewaySpec desired) =>
        current.ClusterId.Equals(desired.ClusterId, StringComparison.OrdinalIgnoreCase)
        && current.BundleManifestSha256.Equals(desired.BundleManifestSha256, StringComparison.OrdinalIgnoreCase)
        && current.StartGeneration == desired.StartGeneration
        && current.ConfigRevision == desired.ConfigRevision
        && current.RunRoot == desired.RunRoot
        && current.Ports.SequenceEqual(desired.Ports);

    private static bool HasShutdownProof(ClusterDefinition cluster) =>
        cluster.ShutdownProof?.LifecycleId == cluster.GetLifecycleId();

    private void Failed(ClusterDefinition cluster, string code, string message)
    {
        ClusterReconcileStatus previous = GetStatus(cluster.UniqueName);
        Set(cluster, ClusterReconcileState.Failed, previous.GatewayObserved,
            previous.ClusterPhase, code, message);
    }

    private void Set(ClusterDefinition cluster, ClusterReconcileState state,
        HostContract.GatewayObservedState? observed, Admin.ClusterPhase? phase,
        string? code, string message) => _status[cluster.UniqueName] = new(
            cluster.UniqueName, cluster.GoalState, state, observed, phase,
            DateTimeOffset.UtcNow, code, message);
}

public enum ClusterReconcileState { Pending, Observing, ConfigurationRequired, Converging, Converged, Failed }

public sealed record ClusterReconcileStatus(
    string ClusterId,
    [property: System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<DedicatedServerGoalState>))]
    DedicatedServerGoalState Goal,
    ClusterReconcileState State,
    HostContract.GatewayObservedState? GatewayObserved,
    Admin.ClusterPhase? ClusterPhase,
    DateTimeOffset UpdatedAt,
    string? ErrorCode,
    string Message);
