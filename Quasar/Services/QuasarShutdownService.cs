using System.Text.Json;
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

    public QuasarShutdownService(
        IHostApplicationLifetime lifetime,
        DedicatedServerSupervisor supervisor,
        WebServiceOptions options,
        ILogger<QuasarShutdownService> logger)
    {
        _lifetime = lifetime;
        _supervisor = supervisor;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Gracefully stops every running Magnetar server. Quasar itself keeps running;
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

        if (running.Count == 0)
            return;

        progress?.Report($"Stopping {running.Count} server{(running.Count == 1 ? "" : "s")}…");

        foreach (var snapshot in running)
        {
            var label = snapshot.UniqueName;
            progress?.Report($"Stopping \"{label}\"…");

            try
            {
                // Record the intent first (without a competing reconcile-driven stop)
                // so the supervisor never auto-restarts the server we are about to stop.
                if (setGoalStateOff)
                    await _supervisor.SetGoalStateAsync(snapshot.UniqueName, DedicatedServerGoalState.Off, reconcile: false, cancellationToken);

                await _supervisor.StopServerAsync(snapshot.UniqueName, forceAfter: null, cancellationToken);
            }
            catch
            {
                // Best-effort: keep shutting down the remaining servers.
            }
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
