using System.Text.Json;
using System.Collections.Concurrent;
using Magnetar.Protocol.Runtime;
using Quasar.Models;

namespace Quasar.Services;

/// <summary>
/// Orchestrates stopping all managed Magnetar servers, and recycling the Quasar
/// worker with or without leaving those servers running.
/// </summary>
public sealed class QuasarShutdownService
{
    private static readonly TimeSpan LauncherRestartFallbackDelay = TimeSpan.FromSeconds(8);

    private readonly IHostApplicationLifetime _lifetime;
    private readonly DedicatedServerSupervisor _supervisor;
    private readonly WebServiceOptions _options;
    private readonly ILogger<QuasarShutdownService> _logger;
    private readonly ClusterCatalog? _clusters;
    private readonly ClusterCommandService? _clusterCommands;
    private readonly ClusterReconciler? _clusterReconciler;

    public QuasarShutdownService(
        IHostApplicationLifetime lifetime,
        DedicatedServerSupervisor supervisor,
        WebServiceOptions options,
        ILogger<QuasarShutdownService> logger,
        ClusterCatalog? clusters = null,
        ClusterCommandService? clusterCommands = null,
        ClusterReconciler? clusterReconciler = null)
    {
        _lifetime = lifetime;
        _supervisor = supervisor;
        _options = options;
        _logger = logger;
        _clusters = clusters;
        _clusterCommands = clusterCommands;
        _clusterReconciler = clusterReconciler;
    }

    /// <summary>
    /// Gracefully stops running standalone servers. Explicit goal-Off requests also
    /// stop managed clusters through their normal lifecycle. Quasar itself keeps running;
    /// the worker is not restarted. Progress messages are reported via
    /// <paramref name="progress"/> so the caller can update a UI while waiting.
    /// </summary>
    /// <param name="setGoalStateOff">
    /// When <c>true</c> the goal state of each server is set to Off before stopping
    /// it, so the reconcile loop treats the shutdown as intentional and does not
    /// auto-restart the server. Used by the admin "Shut down all servers" action,
    /// where Quasar stays up. Left <c>false</c> for full Quasar shutdown, so the
    /// servers resume on the next worker boot per their configured goal state.
    /// </param>
    public async Task StopAllServersAsync(IProgress<string>? progress = null, bool setGoalStateOff = false, CancellationToken cancellationToken = default)
    {
        var running = _supervisor.GetSnapshots()
            .Where(static s => s.State is DedicatedServerProcessState.Starting
                or DedicatedServerProcessState.Running
                or DedicatedServerProcessState.Restarting
                or DedicatedServerProcessState.Stopping)
            .ToList();

        // The explicit power action persists Off goals for the whole managed fleet.
        // Worker-only shutdown retains its existing standalone goal-preservation policy.
        var clusters = setGoalStateOff
            ? _clusters?.GetClusters().Where(cluster => cluster.Gateway is not null).ToArray() ?? []
            : [];
        var total = running.Count + clusters.Length;
        var completed = 0;
        var failures = new ConcurrentQueue<string>();
        void Report(string message) => progress?.Report($"{Volatile.Read(ref completed)} of {total} stopped. {message}");
        async Task StopOneAsync(string label, Func<Task> stop)
        {
            Report($"Saving and stopping {label}…");
            try
            {
                await stop();
                Interlocked.Increment(ref completed);
                Report($"{label} stopped.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                failures.Enqueue($"{label}: {error.Message}");
                _logger.LogError(error, "Save and stop failed for {Server}", label);
                Report($"Could not stop {label}: {error.Message}");
            }
        }

        var stops = running.Select(snapshot => StopOneAsync($"server '{snapshot.UniqueName}'", async () =>
        {
            if (setGoalStateOff)
                await _supervisor.SetGoalStateAsync(snapshot.UniqueName, DedicatedServerGoalState.Off, reconcile: false, cancellationToken);
            await _supervisor.StopServerAsync(snapshot.UniqueName, forceAfter: null, cancellationToken);
        })).Concat(clusters.Select(cluster =>
        {
            var label = string.IsNullOrWhiteSpace(cluster.DisplayName) ? cluster.UniqueName : cluster.DisplayName;
            return StopOneAsync($"cluster '{label}'", () => StopClusterAsync(cluster, message => Report($"{label}: {message}"), cancellationToken));
        }));
        await Task.WhenAll(stops);
        if (!failures.IsEmpty)
            throw new InvalidOperationException("Some servers could not be stopped. " + string.Join(" ", failures));
        progress?.Report(total == 0 ? "No managed servers or clusters need stopping." : "All managed servers and clusters have stopped.");
    }

    private async Task StopClusterAsync(ClusterDefinition cluster, Action<string> progress, CancellationToken cancellationToken)
    {
        if (_clusterCommands is null || _clusterReconciler is null || _clusters is null)
            throw new InvalidOperationException("Cluster lifecycle control is unavailable.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(cluster.ShutdownGracePeriodSeconds) + TimeSpan.FromMinutes(3));
        try
        {
            var request = await _clusterCommands.SetGoalAsync(cluster.UniqueName, new(DedicatedServerGoalState.Off),
                Guid.NewGuid().ToString("N"), "quasar-power", deadline.Token);
            if (request.State != ClusterOperationState.Succeeded)
                throw new InvalidOperationException(request.Error?.Message ?? "Could not request cluster shutdown.");
            var lifecycle = _clusters.GetCluster(cluster.UniqueName)!.GetLifecycleId();
            while (true)
            {
                var current = _clusters.GetCluster(cluster.UniqueName)
                    ?? throw new InvalidOperationException("The cluster was removed during shutdown.");
                if (current.GoalState != DedicatedServerGoalState.Off || current.GetLifecycleId() != lifecycle)
                    throw new InvalidOperationException("Cluster configuration or goal changed during shutdown. Check its current state.");
                var status = await _clusterReconciler.ReconcileNowAsync(cluster.UniqueName, deadline.Token);
                progress(status?.Message ?? "Waiting for another cluster operation to finish…");
                if (status is { Goal: DedicatedServerGoalState.Off, State: ClusterReconcileState.Converged,
                    GatewayObserved: Quasar.Host.Contract.V1.GatewayObservedState.Missing,
                    ClusterPhase: CometWorks.ClusterGateway.AdminContract.V1.ClusterPhase.Down }
                    && _clusters.GetCluster(cluster.UniqueName) is { } stopped && stopped.IsProvenStopped())
                    return;
                if (status?.State is ClusterReconcileState.Failed or ClusterReconcileState.ConfigurationRequired
                    || status?.ErrorCode == "deployment_pending")
                    throw new InvalidOperationException(status.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out waiting for verified cluster shutdown. Check the cluster page; an accepted Off goal remains in effect.");
        }
    }

    /// <summary>
    /// Gracefully stops every running Magnetar server, then requests host shutdown.
    /// Used by the launcher-driven full shutdown (drain endpoint / POSIX signals).
    /// </summary>
    public async Task ShutdownAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        await StopAllServersAsync(progress, cancellationToken: cancellationToken);
        progress?.Report("Shutting down Quasar…");
        _lifetime.StopApplication();
    }

    /// <summary>
    /// Restarts the Quasar web worker <em>without</em> stopping any managed server.
    /// Running Magnetar servers stay up (detached via <c>-daemon</c>) and are
    /// re-adopted by process id once the Bootstrap launcher respawns the worker.
    /// When Quasar runs standalone (no launcher) the worker simply stops and is not
    /// brought back.
    /// </summary>
    public async Task RestartWorkerAsync(IProgress<string>? progress = null)
    {
        // BeginLauncherDrainAsync marks the supervisor to preserve servers on this stop and
        // persists the runtime snapshot (including PIDs) so the next worker can adopt
        // them.
        progress?.Report("Restarting Quasar worker…");
        await _supervisor.BeginLauncherDrainAsync();

        if (TryRequestLauncherWorkerRestart())
        {
            ScheduleRestartFallback();
            return;
        }

        _lifetime.StopApplication();
    }

    /// <summary>
    /// Stops Quasar while preserving managed servers. When launched by Bootstrap,
    /// the worker asks the launcher to exit successfully after the worker stops,
    /// so the external service/task restart-on-failure policy leaves it stopped.
    /// </summary>
    public async Task ShutdownQuasarPreservingServersAsync(IProgress<string>? progress = null)
    {
        progress?.Report("Shutting down Quasar…");
        _logger.LogInformation("Quasar shutdown requested; preserving managed server state before stopping.");
        await _supervisor.BeginLauncherDrainAsync();
        RequestLauncherShutdown();
        _logger.LogInformation("Managed server state saved; stopping the Quasar web worker.");
        _lifetime.StopApplication();
    }

    private void RequestLauncherShutdown()
    {
        if (string.IsNullOrWhiteSpace(_options.LauncherToken))
            return;

        try
        {
            File.WriteAllText(GetLauncherShutdownRequestPath(), DateTimeOffset.UtcNow.ToString("O"));
            _logger.LogInformation("Requested Bootstrap shutdown after the web worker exits.");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed writing Quasar launcher shutdown request; Quasar remains running.");
            throw new InvalidOperationException("Could not request Bootstrap shutdown. Check write access to the Quasar install directory.", exception);
        }
    }

    private static string GetLauncherShutdownRequestPath() =>
        Path.Combine(MagnetarPaths.GetQuasarDirectory(), "launcher-shutdown-request");

    private bool TryRequestLauncherWorkerRestart()
    {
        if (string.IsNullOrWhiteSpace(_options.LauncherToken))
            return false;

        var path = MagnetarPaths.GetQuasarWorkerRestartRequestPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(new
            {
                requestedAtUtc = DateTimeOffset.UtcNow,
                reason = "worker-restart",
                processId = Environment.ProcessId,
            });
            File.WriteAllText(path, json);
            _logger.LogInformation("Requested Quasar worker restart through Bootstrap at {Path}.", path);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed writing Quasar worker restart request.");
            return false;
        }
    }

    private void ScheduleRestartFallback()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(LauncherRestartFallbackDelay).ConfigureAwait(false);
                TryDeleteWorkerRestartRequest();
                _logger.LogWarning(
                    "Bootstrap did not consume the Quasar worker restart request within {DelaySeconds:0} seconds; stopping worker directly.",
                    LauncherRestartFallbackDelay.TotalSeconds);
                _lifetime.StopApplication();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Quasar worker restart fallback failed.");
            }
        });
    }

    private static void TryDeleteWorkerRestartRequest()
    {
        try
        {
            var path = MagnetarPaths.GetQuasarWorkerRestartRequestPath();
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}
