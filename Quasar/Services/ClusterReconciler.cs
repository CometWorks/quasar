using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
    private readonly ILogger<ClusterReconciler> _logger;
    private readonly ConcurrentDictionary<string, ClusterReconcileStatus> _status =
        new(StringComparer.OrdinalIgnoreCase);

    public ClusterReconciler(ClusterCatalog catalog, ClusterGatewayClient gatewayClient,
        ClusterHostClient hostClient, ILogger<ClusterReconciler> logger)
    {
        _catalog = catalog;
        _gatewayClient = gatewayClient;
        _hostClient = hostClient;
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

    internal async Task ReconcileAllAsync(CancellationToken cancellationToken)
    {
        foreach (ClusterDefinition cluster in _catalog.GetClusters())
        {
            try
            {
                await ReconcileAsync(cluster, cancellationToken);
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
            catch (Exception exception)
            {
                Failed(cluster, "reconcile_failed", exception.Message);
                _logger.LogWarning(exception, "Cluster {Cluster} reconciliation failed.", cluster.UniqueName);
            }
        }
    }

    private async Task ReconcileAsync(ClusterDefinition cluster, CancellationToken cancellationToken)
    {
        if (cluster.Gateway == null)
        {
            Set(cluster, ClusterReconcileState.ConfigurationRequired, null, null,
                "gateway_spec_required", "Configure the cluster Gateway executor spec.");
            return;
        }

        HostContract.GatewaySpec on = cluster.Gateway with
        {
            ClusterId = cluster.UniqueName,
            Goal = HostContract.GatewayGoal.On,
            Ports = [.. cluster.Gateway.Ports],
        };
        HostContract.HostStatus host = (await _hostClient.GetStatusAsync(cluster, cancellationToken)).Data;
        HostContract.GatewayStatus? current = host.Gateways?.FirstOrDefault(gateway =>
            string.Equals(gateway.ClusterId, cluster.UniqueName, StringComparison.OrdinalIgnoreCase));
        if (cluster.GoalState == DedicatedServerGoalState.Off
            && current is { Goal: HostContract.GatewayGoal.Off,
                Observed: HostContract.GatewayObservedState.Missing }
            && MatchesSpec(current, on))
        {
            Set(cluster, ClusterReconcileState.Converged, current.Observed, Admin.ClusterPhase.Down,
                null, "Cluster is cleanly down and the Gateway process is stopped.");
            return;
        }

        HostContract.GatewayStatus hostGateway = await EnsureGatewayRunningAsync(
            cluster, on, current, cancellationToken);
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
        if (cluster.GoalState == DedicatedServerGoalState.On)
        {
            Set(cluster, gateway.Phase == Admin.ClusterPhase.Serving
                    ? ClusterReconcileState.Converged : ClusterReconcileState.Converging,
                hostGateway.Observed, gateway.Phase, null,
                gateway.Phase == Admin.ClusterPhase.Serving
                    ? "Gateway is serving; host executors are actualizing the NodePlan."
                    : $"Gateway is {gateway.Phase}; waiting for Registry readiness.");
            return;
        }

        if (gateway.Phase != Admin.ClusterPhase.Down)
        {
            Admin.ShutdownRequest request = new(
                LifecycleRequestId(cluster),
                Admin.ShutdownMode.Graceful,
                cluster.ShutdownGracePeriodSeconds,
                CompletionTimeoutSeconds: 900,
                ForceAfterSeconds: 900);
            Admin.GatewayLifecycleResult result =
                (await _gatewayClient.ShutdownAsync(cluster, request, cancellationToken)).Data;
            if (result.Phase != Admin.ClusterPhase.Down)
            {
                Set(cluster, ClusterReconcileState.Converging, hostGateway.Observed, result.Phase,
                    null, "Graceful cluster shutdown is still running.");
                return;
            }
            gateway = (await _gatewayClient.GetStatusAsync(cluster, cancellationToken)).Data;
        }

        ClusterApi.EnsureGatewayCanStop(gateway);
        HostContract.GatewayStatus stopped = (await _hostClient.ApplyGatewayAsync(cluster,
            on with { Goal = HostContract.GatewayGoal.Off }, cancellationToken)).Data;
        Set(cluster, stopped.Observed == HostContract.GatewayObservedState.Missing
                ? ClusterReconcileState.Converged : ClusterReconcileState.Converging,
            stopped.Observed, gateway.Phase, stopped.Failure == null ? null : "gateway_stop_failed",
            stopped.Failure ?? "Cluster is cleanly down and the Gateway process is stopped.");
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

    private static bool MatchesSpec(HostContract.GatewayStatus current, HostContract.GatewaySpec desired) =>
        current.BundleManifestSha256.Equals(desired.BundleManifestSha256, StringComparison.OrdinalIgnoreCase)
        && current.ConfigRevision == desired.ConfigRevision
        && current.RunRoot == desired.RunRoot
        && current.Ports.SequenceEqual(desired.Ports);

    private static Guid LifecycleRequestId(ClusterDefinition cluster)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{cluster.UniqueName}\n{cluster.GoalState}\n{cluster.UpdatedAtUtc.UtcTicks}"));
        return new Guid(hash.AsSpan(0, 16));
    }

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

public enum ClusterReconcileState { Pending, ConfigurationRequired, Converging, Converged, Failed }

public sealed record ClusterReconcileStatus(
    string ClusterId,
    DedicatedServerGoalState Goal,
    ClusterReconcileState State,
    HostContract.GatewayObservedState? GatewayObserved,
    Admin.ClusterPhase? ClusterPhase,
    DateTimeOffset UpdatedAt,
    string? ErrorCode,
    string Message);
