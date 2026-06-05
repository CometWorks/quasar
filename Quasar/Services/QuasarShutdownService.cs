using Quasar.Models;

namespace Quasar.Services;

/// <summary>
/// Orchestrates stopping all managed Magnetar servers, and recycling the Quasar
/// worker with or without leaving those servers running.
/// </summary>
public sealed class QuasarShutdownService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly DedicatedServerSupervisor _supervisor;

    public QuasarShutdownService(IHostApplicationLifetime lifetime, DedicatedServerSupervisor supervisor)
    {
        _lifetime = lifetime;
        _supervisor = supervisor;
    }

    /// <summary>
    /// Gracefully stops every running Magnetar server. Quasar itself keeps running;
    /// the worker is not restarted. Progress messages are reported via
    /// <paramref name="progress"/> so the caller can update a UI while waiting.
    /// </summary>
    public async Task StopAllServersAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
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
        await StopAllServersAsync(progress, cancellationToken);
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
    public void RestartWorker(IProgress<string>? progress = null)
    {
        // BeginLauncherDrain marks the supervisor to preserve servers on this stop and
        // persists the runtime snapshot (including PIDs) so the next worker can adopt
        // them; StopApplication then exits the worker for the launcher to respawn.
        progress?.Report("Restarting Quasar worker…");
        _supervisor.BeginLauncherDrain();
        _lifetime.StopApplication();
    }
}
